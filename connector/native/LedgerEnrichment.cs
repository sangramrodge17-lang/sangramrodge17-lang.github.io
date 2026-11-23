using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public sealed record EnrichmentResult(string Status, string Detail, int Updated, string? Warning);

/// <summary>
/// Read-only enrichment of the local ledger store so the backend chatbot can answer
/// debtors/creditors ageing, GST/PAN and MSME questions.
///
/// Everything here is best-effort: the caller wraps it in try/catch and a failure only produces a
/// warning, so the proven ledger/voucher sync is never affected.
///   1. Group hierarchy  -> ledger.group_path (e.g. "Current Assets > Sundry Debtors")
///   2. Ledger collection -> GSTIN, PAN, credit days, opening bill allocations
///   3. Per-ledger object export for supplier ledgers (and ledgers with opening balances but no
///      bills in the collection) -> MSME / Udyam detection and opening bills.
///      MSME tags are detected by pattern (any tag containing MSME/UDYAM/ENTERPRISE, any value shaped
///      like UDYAM-XX-00-0000000) because the exact TallyPrime tag names were not verified against a live company.
/// </summary>
public static class LedgerEnricher
{
    private const int MaxLedgerExports = 800;
    private static readonly TimeSpan ExportBudget = TimeSpan.FromSeconds(90);
    private static readonly Regex UdyamNumber = new(@"UDYAM-[A-Z]{2}-\d{2}-\d{7}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MsmeTag = new(@"MSME|UDYAM|ENTERPRISE", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record LedgerInfo(string Name, string Parent, decimal Opening, string? Gstin, string? Pan, int? CreditDays, List<SyncStore.OpeningBill> OpeningBills);

    public static async Task<EnrichmentResult> RunAsync(Func<string, Task<string>> post)
    {
        var warnings = new List<string>();

        // 1. Group hierarchy
        var groupParent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var body = await post(CollectionXml("Universal Group Collection", "Group", "Name", "Parent"));
            var doc = XDocument.Parse(TallyXml.Sanitize(body));
            foreach (var node in doc.Descendants().Where(x => x.Name.LocalName.Equals("GROUP", StringComparison.OrdinalIgnoreCase)))
            {
                var name = OwnName(node);
                if (name.Length > 0) groupParent[name] = Clean(Val(node, "PARENT"));
            }
        }
        catch (Exception ex) { warnings.Add($"Group hierarchy unavailable ({ex.Message}); only the immediate parent group will be used."); }

        // 2. Ledger collection
        var ledgers = new List<LedgerInfo>();
        try
        {
            var body = await post(CollectionXml("Universal Ledger Enrichment", "Ledger", "Name", "Parent", "OpeningBalance", "PartyGSTIN", "IncomeTaxNumber", "BillCreditPeriod", "BILLALLOCATIONS.LIST"));
            var doc = XDocument.Parse(TallyXml.Sanitize(body));
            foreach (var node in doc.Descendants().Where(x => x.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase)))
            {
                var name = OwnName(node);
                if (name.Length == 0) continue;
                var gstin = Clean(Val(node, "PARTYGSTIN") ?? Val(node, "GSTIN"));
                var pan = Clean(Val(node, "INCOMETAXNUMBER") ?? Val(node, "PAN"));
                ledgers.Add(new LedgerInfo(name, Clean(Val(node, "PARENT")), ParseAmount(Val(node, "OPENINGBALANCE")),
                    gstin.Length == 0 ? null : gstin, pan.Length == 0 ? null : pan, FirstInt(Val(node, "BILLCREDITPERIOD")), ReadOpeningBills(node, name)));
            }
        }
        catch (Exception ex)
        {
            return new EnrichmentResult("warning", $"Ledger enrichment skipped: {ex.Message}", 0, $"Ledger enrichment failed: {ex.Message}");
        }
        if (ledgers.Count == 0)
            return new EnrichmentResult("warning", "Tally returned no ledgers for the enrichment request.", 0, "Ledger enrichment returned no ledgers; group paths, GST/PAN and MSME flags were not updated.");

        string PathFor(string parent)
        {
            var chain = new List<string>();
            var current = parent;
            for (var guard = 0; guard < 12 && !string.IsNullOrWhiteSpace(current); guard++)
            {
                chain.Insert(0, current);
                if (!groupParent.TryGetValue(current, out var up) || string.IsNullOrWhiteSpace(up) || up.Equals("Primary", StringComparison.OrdinalIgnoreCase)) break;
                current = up;
            }
            return string.Join(" > ", chain);
        }

        // 3. Per-ledger export where the collection is not enough
        var enrichments = new Dictionary<string, SyncStore.LedgerEnrichment>(StringComparer.OrdinalIgnoreCase);
        var openingBills = new List<SyncStore.OpeningBill>();
        var scanned = 0;
        var msmeFound = 0;
        var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew();
        var capped = false;

        foreach (var ledger in ledgers)
        {
            var path = PathFor(ledger.Parent);
            var isCreditor = path.Contains("Sundry Creditors", StringComparison.OrdinalIgnoreCase);
            var isDebtor = path.Contains("Sundry Debtors", StringComparison.OrdinalIgnoreCase);
            var needsOpeningBills = (isCreditor || isDebtor) && Math.Abs(ledger.Opening) > 0.004m && ledger.OpeningBills.Count == 0;
            var bills = new List<SyncStore.OpeningBill>(ledger.OpeningBills);
            string? udyam = null, category = null;
            bool? isMsme = null;

            if (isCreditor || needsOpeningBills)
            {
                if (scanned >= MaxLedgerExports || timer.Elapsed > ExportBudget) capped = true;
                else
                {
                    scanned++;
                    try
                    {
                        var body = await post(ObjectXml(ledger.Name));
                        var doc = XDocument.Parse(TallyXml.Sanitize(body));
                        if (isCreditor)
                        {
                            (udyam, category, isMsme) = ScanMsme(doc, tagNames);
                            if (isMsme == true) msmeFound++;
                        }
                        if (needsOpeningBills)
                        {
                            var root = doc.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase)) ?? doc.Root!;
                            bills.AddRange(ReadOpeningBills(root, ledger.Name));
                        }
                    }
                    catch (Exception ex) { warnings.Add($"Ledger '{ledger.Name}' could not be exported ({ex.Message})."); }
                }
            }

            enrichments[ledger.Name] = new SyncStore.LedgerEnrichment(ledger.Name, path.Length == 0 ? null : path, ledger.Gstin, ledger.Pan, isMsme, udyam, category, ledger.CreditDays);
            openingBills.AddRange(bills);
        }

        var updated = SyncStore.ApplyLedgerEnrichment(enrichments.Values);
        SyncStore.ReplaceOpeningBills(openingBills);

        if (capped) warnings.Add($"Per-ledger export stopped after {scanned} ledger(s) (limit {MaxLedgerExports} or {ExportBudget.TotalSeconds:0}s). Some supplier MSME flags or opening bills may be missing; run the sync again.");
        var creditors = ledgers.Count(l => PathFor(l.Parent).Contains("Sundry Creditors", StringComparison.OrdinalIgnoreCase));
        if (creditors > 0 && msmeFound == 0 && !capped)
            warnings.Add($"No MSME/Udyam value was found in {scanned} supplier ledger export(s). MSME tags seen: {(tagNames.Count == 0 ? "none" : string.Join(", ", tagNames.Take(10)))}. If suppliers are registered in Tally, share one ledger XML so the exact field can be mapped.");

        var detail = $"{groupParent.Count} group(s), {ledgers.Count} ledger(s) read, {updated} updated, {scanned} exported individually, {msmeFound} MSME/Udyam supplier(s) found, {openingBills.Count} opening bill(s)";
        return new EnrichmentResult(warnings.Count == 0 ? "complete" : "warning", detail, updated, warnings.Count == 0 ? null : string.Join(" ", warnings.Take(5)));
    }

    private static (string? Udyam, string? Category, bool? IsMsme) ScanMsme(XDocument doc, HashSet<string> tagNames)
    {
        string? udyam = null, category = null;
        foreach (var element in doc.Descendants())
        {
            var name = element.Name.LocalName;
            var tagMatches = MsmeTag.IsMatch(name);
            if (tagMatches) tagNames.Add(name);
            if (element.HasElements) continue;
            var value = Clean(element.Value);
            if (value.Length == 0) continue;
            if (udyam is null)
            {
                var match = UdyamNumber.Match(value);
                if (match.Success) udyam = match.Value.ToUpperInvariant();
            }
            if (category is null && tagMatches)
            {
                var kind = Regex.Match(value, @"^(micro|small|medium)\b", RegexOptions.IgnoreCase);
                if (kind.Success) category = char.ToUpperInvariant(kind.Value[0]) + kind.Value[1..].ToLowerInvariant();
            }
        }
        return (udyam, category, udyam is not null || category is not null ? true : null);
    }

    private static List<SyncStore.OpeningBill> ReadOpeningBills(XElement ledgerNode, string ledgerName)
    {
        var bills = new List<SyncStore.OpeningBill>();
        foreach (var bill in ledgerNode.Descendants().Where(x => x.Name.LocalName.Equals("BILLALLOCATIONS.LIST", StringComparison.OrdinalIgnoreCase)))
        {
            var billName = Clean(Val(bill, "NAME"));
            if (billName.Length == 0) continue;
            var amount = ParseAmount(Val(bill, "OPENINGBALANCE") ?? Val(bill, "AMOUNT"));
            if (Math.Abs(amount) < 0.004m) continue;
            var billDate = NormalizeDate(Val(bill, "BILLDATE"));
            var credit = Clean(Val(bill, "BILLCREDITPERIOD"));
            bills.Add(new SyncStore.OpeningBill(ledgerName, billName, billDate, credit.Length == 0 ? null : credit, SyncStore.ComputeDueDate(billDate, credit), amount));
        }
        return bills;
    }

    private static string CollectionXml(string name, string type, params string[] fetch) =>
        $"<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>{name}</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES><TDL><TDLMESSAGE><COLLECTION NAME=\"{name}\"><TYPE>{type}</TYPE>{string.Concat(fetch.Select(f => $"<FETCH>{f}</FETCH>"))}</COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";

    private static string ObjectXml(string ledger) =>
        $"<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Object</TYPE><SUBTYPE>Ledger</SUBTYPE><ID TYPE=\"Name\">{System.Security.SecurityElement.Escape(ledger)}</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES></DESC></BODY></ENVELOPE>";

    // Same matching rule as the existing ledger reader (element first, then attribute), preferring the object's own NAME.
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

    // Tally uses control characters in some values (for example the root group "\u0004 Primary").
    private static string Clean(string? value) => Regex.Replace(value ?? "", @"[\u0000-\u001F]", "").Trim();

    private static int? FirstInt(string? value)
    {
        var match = Regex.Match(value ?? "", @"\d{1,4}");
        return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }

    private static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "d-MMM-yy", "dd-MMM-yyyy", "d-MMM-yyyy" };
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    private static decimal ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        var text = value.Replace(",", "").Trim();
        var credit = text.EndsWith("Cr", StringComparison.OrdinalIgnoreCase);
        var debit = text.EndsWith("Dr", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"[^0-9.\-]", "");
        if (!decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)) return 0m;
        return credit ? Math.Abs(amount) : debit ? -Math.Abs(amount) : amount;
    }
}
