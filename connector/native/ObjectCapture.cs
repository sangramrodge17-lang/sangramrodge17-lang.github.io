using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Schema-less, read-only capture of every Tally master object as JSON, so the backend chatbot can query
/// any field (GST/TDS settings, stock item details, cost centres, payroll masters, ...) even where no
/// dedicated table exists.
///
/// For each object type: list names + AlterID with one collection request, then export only objects that are
/// new or whose AlterID changed since the last sync (cached in the local SQLite store), so repeat syncs are fast.
/// A type that this Tally version or company does not support simply returns nothing and is skipped.
/// Everything is best-effort and time-boxed; the caller wraps it in try/catch.
/// </summary>
public static class ObjectCapture
{
    // Small masters first, the two large ones (ledgers, stock items) last so the time budget never starves the rest.
    private static readonly (string Kind, string Type)[] Kinds =
    {
        ("company", "Company"), ("group", "Group"), ("voucher_type", "Voucher Type"), ("unit", "Unit"), ("godown", "Godown"),
        ("cost_category", "Cost Category"), ("cost_centre", "Cost Centre"), ("stock_group", "Stock Group"), ("stock_category", "Stock Category"),
        ("currency", "Currency"), ("gst_classification", "GST Classification"), ("tds_nature", "TDS Nature Of Payment"),
        ("employee", "Employee"), ("employee_group", "Employee Group"), ("pay_head", "Pay Head"), ("budget", "Budget"),
        ("ledger", "Ledger"), ("stock_item", "Stock Item"),
    };

    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);
    private const int MaxJsonChars = 1_500_000;

    private sealed record Listed(string Name, string? Guid, long? AlterId);

    public static async Task<EnrichmentResult> RunAsync(Func<string, Task<string>> post)
    {
        var timer = Stopwatch.StartNew();
        var summary = new List<string>();
        var warnings = new List<string>();
        var exportedTotal = 0;
        var capped = false;

        foreach (var (kind, type) in Kinds)
        {
            if (timer.Elapsed > Budget) { capped = true; break; }
            try
            {
                var listing = await ListAsync(post, type);
                if (listing.Count == 0) continue;
                var known = SyncStore.GetObjectAlterIds(kind);
                var exported = 0;
                var kindCapped = false;
                foreach (var item in listing)
                {
                    if (timer.Elapsed > Budget) { kindCapped = true; capped = true; break; }
                    if (item.AlterId is not null && known.TryGetValue(item.Name, out var old) && old == item.AlterId) continue;
                    try
                    {
                        var json = ToJson(await post(ObjectXml(type, item.Name)));
                        if (json is null) continue;
                        SyncStore.UpsertObject(kind, item.Name, item.Guid, item.AlterId, json);
                        exported++;
                    }
                    catch (Exception ex) { if (warnings.Count < 5) warnings.Add($"{type} '{item.Name}': {ex.Message}"); }
                }
                if (!kindCapped) SyncStore.PruneObjects(kind, listing.Select(i => i.Name));
                summary.Add($"{kind} {listing.Count}" + (exported > 0 ? $" ({exported} new/changed)" : ""));
                exportedTotal += exported;
            }
            catch (Exception ex) { if (warnings.Count < 5) warnings.Add($"{type} not captured: {ex.Message}"); }
        }

        if (capped) warnings.Add($"Master capture stopped at the {Budget.TotalMinutes:0}-minute limit; run the sync again to continue (unchanged objects are skipped).");
        var detail = summary.Count == 0 ? "No master objects were returned by Tally." : string.Join(", ", summary);
        return new EnrichmentResult(warnings.Count == 0 ? "complete" : "warning", detail, exportedTotal, warnings.Count == 0 ? null : string.Join(" ", warnings.Take(5)));
    }

    private static async Task<List<Listed>> ListAsync(Func<string, Task<string>> post, string type)
    {
        var body = await post(CollectionXml(type));
        var doc = XDocument.Parse(TallyXml.Sanitize(body));
        var collection = doc.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase));
        var list = new List<Listed>();
        if (collection is null) return list;
        foreach (var node in collection.Elements())
        {
            var name = OwnName(node);
            if (name.Length == 0) continue;
            var guid = Clean(Val(node, "GUID"));
            list.Add(new Listed(name, guid.Length == 0 ? null : guid, long.TryParse(Val(node, "ALTERID"), out var alter) ? (long?)alter : null));
        }
        return list;
    }

    private static string? ToJson(string body)
    {
        var doc = XDocument.Parse(TallyXml.Sanitize(body));
        var message = doc.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("TALLYMESSAGE", StringComparison.OrdinalIgnoreCase));
        var obj = message?.Elements().FirstOrDefault()
            ?? doc.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("DATA", StringComparison.OrdinalIgnoreCase))?.Elements().FirstOrDefault();
        if (obj is null) return null;
        var node = ToNode(obj);
        if (node is null) return null;
        var text = node.ToJsonString();
        return text.Length > MaxJsonChars ? "{\"_truncated\":true}" : text;
    }

    // XML -> JSON: tag names become keys (upper-case, as Tally writes them), repeated tags and every *.LIST tag become arrays.
    private static JsonNode? ToNode(XElement element)
    {
        if (!element.HasElements)
        {
            var text = Clean(element.Value);
            return text.Length == 0 ? null : JsonValue.Create(text);
        }
        var obj = new JsonObject();
        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
        {
            var items = group.Select(ToNode).Where(n => n is not null).ToList();
            if (items.Count == 0) continue;
            obj[group.Key] = items.Count == 1 && !group.Key.EndsWith(".LIST", StringComparison.OrdinalIgnoreCase)
                ? items[0]
                : new JsonArray(items.ToArray());
        }
        foreach (var attribute in element.Attributes())
        {
            var value = Clean(attribute.Value);
            if (value.Length > 0 && !attribute.IsNamespaceDeclaration) obj["@" + attribute.Name.LocalName] = value;
        }
        return obj.Count == 0 ? null : obj;
    }

    private static string CollectionXml(string type) =>
        $"<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>Universal Object List</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES><TDL><TDLMESSAGE><COLLECTION NAME=\"Universal Object List\"><TYPE>{type}</TYPE><FETCH>Name</FETCH><FETCH>GUID</FETCH><FETCH>AlterID</FETCH></COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";

    private static string ObjectXml(string type, string name) =>
        $"<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Object</TYPE><SUBTYPE>{type}</SUBTYPE><ID TYPE=\"Name\">{System.Security.SecurityElement.Escape(name)}</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES></DESC></BODY></ENVELOPE>";

    private static string OwnName(XElement node)
    {
        var attribute = node.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(attribute)) return Clean(attribute);
        var child = node.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("NAME", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value));
        return Clean(child?.Value ?? Val(node, "NAME"));
    }

    private static string? Val(XElement node, string name)
    {
        var element = node.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value));
        if (element is not null) return element.Value.Trim();
        return node.DescendantsAndSelf().Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
    }

    private static string Clean(string? value) => Regex.Replace(value ?? "", @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", "").Trim();
}
