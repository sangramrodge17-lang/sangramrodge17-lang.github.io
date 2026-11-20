using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Tally sign convention used throughout this file:
//   Voucher/ledger XML AMOUNT:  negative = Debit, positive = Credit.
// Internally balances are held as "DrPositive": positive = Dr balance, negative = Cr balance.

public static class TallyXml
{
    static readonly Regex CharRef = new(@"&#(?:[xX]([0-9A-Fa-f]+)|([0-9]+));", RegexOptions.Compiled);

    /// <summary>
    /// Removes characters that XML 1.0 forbids, both raw (0x04 bytes) and as character
    /// references (&amp;#4;), which Tally emits in names and which XDocument.Parse rejects.
    /// </summary>
    public static string Sanitize(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var withoutRefs = CharRef.Replace(body, match =>
        {
            var code = 0;
            var parsed = match.Groups[1].Success
                ? int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
                : int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out code);
            if (!parsed) return "";
            if (code == 9 || code == 10 || code == 13) return match.Value;
            if (code < 0x20) return "";
            if (code == 0xFFFE || code == 0xFFFF || (code >= 0xD800 && code <= 0xDFFF)) return "";
            return match.Value;
        });
        var builder = new StringBuilder(withoutRefs.Length);
        foreach (var character in withoutRefs)
        {
            if (character == '\t' || character == '\n' || character == '\r' || character >= ' ')
                builder.Append(character);
        }
        return builder.ToString();
    }
}

public sealed class ParsedVoucherRow
{
    public string Date { get; init; } = "";
    public DateTime? DateValue { get; init; }
    public string VoucherType { get; init; } = "";
    public string VoucherNumber { get; init; } = "";
    public string Particulars { get; init; } = "";
    public string[] CounterLedgers { get; init; } = Array.Empty<string>();
    public string Narration { get; init; } = "";
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
}

public sealed class ParseResult
{
    public string Format { get; set; } = "none";
    public List<ParsedVoucherRow> Rows { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> SampleLedgerNames { get; } = new();
    public int VouchersSeen { get; set; }
    public int SkippedNotInvolved { get; set; }
    public int SkippedCancelled { get; set; }
    public int SkippedOptional { get; set; }
}

public sealed record BalanceInfo(decimal Amount, string Type, string Text);

public sealed record StatementRow(
    string Date, string DateIso, string VoucherType, string VoucherNumber,
    string Particulars, string[] CounterLedgers, string Narration,
    string Amount, string Debit, string Credit,
    string RunningBalance, string BalanceType,
    decimal DebitValue, decimal CreditValue, decimal RunningBalanceDrPositive);

public sealed record Reconciliation(bool Checked, bool Matched, decimal? TallyClosingDrPositive, decimal? Difference);

public sealed record LedgerStatement(
    string Ledger, string Format, bool OpeningKnown,
    BalanceInfo Opening, BalanceInfo Closing,
    decimal TotalDebit, decimal TotalCredit, int Count,
    List<StatementRow> Rows, Reconciliation Reconciliation, List<string> Warnings,
    int VouchersSeen, int SkippedNotInvolved, int SkippedCancelled, int SkippedOptional);

public sealed record StatementRun(LedgerStatement Statement, List<object> Diagnostics, string Strategy, bool TallyResponded, string From, string To, string? Error);

public static class TallyLedgerParser
{
    static readonly string[] DateFormats =
    {
        "yyyyMMdd", "d-MMM-yy", "d-MMM-yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy",
    };

    public static string NormalizeName(string? value) =>
        Regex.Replace((value ?? "").Trim(), @"\s+", " ").ToLowerInvariant();

    static bool Is(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Direct child text only. Deliberately not a deep search, so that nested
    /// BILLALLOCATIONS.LIST / BANKALLOCATIONS.LIST amounts can never be mistaken for the entry amount.</summary>
    static string Child(XElement parent, string name)
    {
        var element = parent.Elements().FirstOrDefault(child => Is(child, name));
        return element?.Value.Trim() ?? "";
    }

    static bool IsYes(string value) => value.Equals("Yes", StringComparison.OrdinalIgnoreCase);

    public static DateTime? ParseDate(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return null;
        return DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Parses a Tally amount. Handles "-1,234.50", " 500.00", and forex strings such as
    /// "$ 10 @ Rs.80/$ = Rs.800" (the value after the last '=' is used).</summary>
    public static decimal ParseAmount(string? value)
    {
        var text = value ?? "";
        var equals = text.LastIndexOf('=');
        if (equals >= 0) text = text[(equals + 1)..];
        text = text.Replace(",", "");
        var match = Regex.Match(text, @"-?\d+(\.\d+)?");
        return match.Success && decimal.TryParse(match.Value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : 0m;
    }

    /// <summary>Converts a Tally balance string to DrPositive. Accepts "123.45 Dr", "123.45 Cr",
    /// or a bare signed number (Tally XML: negative = Dr). Returns null for null input.</summary>
    public static decimal? ParseBalanceToDrPositive(string? value)
    {
        if (value is null) return null;
        var text = value.Trim();
        if (text.Length == 0) return 0m;
        var amount = ParseAmount(text);
        if (Regex.IsMatch(text, @"\bDr\b", RegexOptions.IgnoreCase)) return Math.Abs(amount);
        if (Regex.IsMatch(text, @"\bCr\b", RegexOptions.IgnoreCase)) return -Math.Abs(amount);
        return -amount; // bare number: negative = Dr in Tally XML, so flip to DrPositive
    }

    public static BalanceInfo ToBalanceInfo(decimal drPositive)
    {
        var type = drPositive > 0m ? "Dr" : drPositive < 0m ? "Cr" : "";
        var absolute = Math.Abs(drPositive);
        var text = absolute.ToString("0.00", CultureInfo.InvariantCulture) + (type.Length > 0 ? " " + type : "");
        return new BalanceInfo(absolute, type, text);
    }

    public static ParseResult Parse(XDocument document, string requestedLedger)
    {
        var result = new ParseResult();
        var target = NormalizeName(requestedLedger);
        var vouchers = document.Descendants().Where(element => Is(element, "VOUCHER") || Is(element, "DSPVOUCHER")).ToList();
        if (vouchers.Count > 0)
        {
            result.Format = "nested-voucher";
            foreach (var voucher in vouchers) ParseVoucher(voucher, target, result);
            if (result.Rows.Count == 0)
            {
                var seen = result.SampleLedgerNames.Count > 0
                    ? " Ledger names seen in the response include: " + string.Join("; ", result.SampleLedgerNames.Take(10)) + "."
                    : "";
                result.Warnings.Add($"Tally returned {result.VouchersSeen} voucher(s) but none contained ledger '{requestedLedger}'.{seen}");
            }
        }
        else
        {
            ParseFlat(document, result);
        }
        return result;
    }

    sealed record Entry(string Name, decimal Amount);

    static List<Entry> CollectEntries(XElement voucher)
    {
        var entries = new List<Entry>();
        void Add(XElement element)
        {
            var name = Child(element, "LEDGERNAME");
            if (name.Length == 0) return;
            entries.Add(new Entry(name, ParseAmount(Child(element, "AMOUNT"))));
        }

        // Tally can wrap ledger-entry lists below intermediate export nodes. Search
        // descendants rather than only direct children so a full Day Book export
        // does not collapse to the currently highlighted display row.
        foreach (var element in voucher.Descendants().Where(element =>
                     element.Name.LocalName.Equals("ALLLEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase) ||
                     element.Name.LocalName.Equals("LEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase) ||
                     element.Name.LocalName.Equals("ACCOUNTINGALLOCATIONS.LIST", StringComparison.OrdinalIgnoreCase)))
        {
            Add(element);
        }
        return entries;
    }

    static void ParseVoucher(XElement voucher, string target, ParseResult result)
    {
        result.VouchersSeen++;
        if (IsYes(Child(voucher, "ISCANCELLED"))) { result.SkippedCancelled++; return; }
        if (IsYes(Child(voucher, "ISOPTIONAL"))) { result.SkippedOptional++; return; }

        var entries = CollectEntries(voucher);
        foreach (var entry in entries)
        {
            if (result.SampleLedgerNames.Count < 15 && !result.SampleLedgerNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                result.SampleLedgerNames.Add(entry.Name);
        }

        var mine = entries.Where(entry => NormalizeName(entry.Name) == target).ToList();
        if (mine.Count == 0) { result.SkippedNotInvolved++; return; }

        var net = mine.Sum(entry => entry.Amount); // negative = debit
        var debit = net < 0m ? -net : 0m;
        var credit = net > 0m ? net : 0m;

        var others = entries.Where(entry => NormalizeName(entry.Name) != target).ToList();
        var opposite = others.Where(entry => Math.Sign(entry.Amount) != Math.Sign(net)).ToList();
        var counters = (opposite.Count > 0 ? opposite : others)
            .Select(entry => entry.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var particulars = counters.Length == 0 ? ""
            : counters.Length == 1 ? counters[0]
            : $"{counters[0]} (+{counters.Length - 1} more)";

        var type = Child(voucher, "VOUCHERTYPENAME");
        if (type.Length == 0) type = Child(voucher, "DSPVCHTYPE");
        if (type.Length == 0) type = voucher.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("VCHTYPE", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
        var date = Child(voucher, "DATE");
        if (date.Length == 0) date = Child(voucher, "DSPDATE");

        result.Rows.Add(new ParsedVoucherRow
        {
            Date = date,
            DateValue = ParseDate(date),
            VoucherType = type,
            VoucherNumber = Child(voucher, "VOUCHERNUMBER").Length > 0 ? Child(voucher, "VOUCHERNUMBER") : Child(voucher, "DSPVCHNUMBER"),
            Particulars = particulars,
            CounterLedgers = counters,
            Narration = Child(voucher, "NARRATION"),
            Debit = debit,
            Credit = credit,
        });
    }

    /// <summary>
    /// Best-effort parser for the flat display-report layout (no VOUCHER wrapper). The tag names used here
    /// are inferred, not confirmed against a real Tally response. Treat results as provisional: the
    /// opening/closing reconciliation in BuildStatement will expose a wrong parse.
    /// </summary>
    static void ParseFlat(XDocument document, ParseResult result)
    {
        var rows = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var element in document.Descendants())
        {
            if (element.HasElements) continue;
            var name = element.Name.LocalName.ToUpperInvariant();
            var text = element.Value.Trim();
            if (name == "DSPVCHDATE")
            {
                current = new Dictionary<string, string> { ["date"] = text };
                rows.Add(current);
                continue;
            }
            if (current is null) continue;
            switch (name)
            {
                case "DSPVCHTYPE": current["type"] = text; break;
                case "DSPVCHLEDACCOUNT": current["particulars"] = text; break;
                case "DSPVCHDRAMT": current["debit"] = text; break;
                case "DSPVCHCRAMT": current["credit"] = text; break;
                case "DSPVCHNUMBER": case "DSPVCHNO": case "VCHNUMBER": current["number"] = text; break;
                case "DSPVCHNARRATION": case "NARRATION": current["narration"] = text; break;
            }
        }
        if (rows.Count == 0) return;

        result.Format = "flat-dsp";
        result.Warnings.Add("Parsed from flat display-report tags (best effort; tag names are inferred). Check the reconciliation result before relying on these rows.");
        foreach (var row in rows)
        {
            row.TryGetValue("date", out var date);
            row.TryGetValue("type", out var type);
            row.TryGetValue("number", out var number);
            row.TryGetValue("particulars", out var particulars);
            row.TryGetValue("narration", out var narration);
            row.TryGetValue("debit", out var debit);
            row.TryGetValue("credit", out var credit);
            result.VouchersSeen++;
            result.Rows.Add(new ParsedVoucherRow
            {
                Date = date ?? "",
                DateValue = ParseDate(date),
                VoucherType = type ?? "",
                VoucherNumber = number ?? "",
                Particulars = particulars ?? "",
                CounterLedgers = string.IsNullOrWhiteSpace(particulars) ? Array.Empty<string>() : new[] { particulars! },
                Narration = narration ?? "",
                Debit = Math.Abs(ParseAmount(debit)),
                Credit = Math.Abs(ParseAmount(credit)),
            });
        }
    }

    public static LedgerStatement BuildStatement(string ledger, ParseResult parse, decimal? openingDrPositive, decimal? tallyClosingDrPositive)
    {
        var ordered = parse.Rows
            .Select((row, index) => (row, index))
            .OrderBy(item => item.row.DateValue ?? DateTime.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => item.row)
            .ToList();

        var warnings = new List<string>(parse.Warnings);
        var running = openingDrPositive ?? 0m;
        var totalDebit = 0m;
        var totalCredit = 0m;
        var rows = new List<StatementRow>();
        foreach (var row in ordered)
        {
            running += row.Debit - row.Credit;
            totalDebit += row.Debit;
            totalCredit += row.Credit;
            var balance = ToBalanceInfo(running);
            rows.Add(new StatementRow(
                row.Date,
                row.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                row.VoucherType, row.VoucherNumber, row.Particulars, row.CounterLedgers, row.Narration,
                (row.Credit - row.Debit).ToString("0.00", CultureInfo.InvariantCulture),
                row.Debit == 0m ? "" : row.Debit.ToString("0.00", CultureInfo.InvariantCulture),
                row.Credit == 0m ? "" : row.Credit.ToString("0.00", CultureInfo.InvariantCulture),
                balance.Amount.ToString("0.00", CultureInfo.InvariantCulture), balance.Type,
                row.Debit, row.Credit, running));
        }

        Reconciliation reconciliation;
        if (openingDrPositive.HasValue && tallyClosingDrPositive.HasValue)
        {
            var difference = running - tallyClosingDrPositive.Value;
            var matched = Math.Abs(difference) <= 0.01m;
            reconciliation = new Reconciliation(true, matched, tallyClosingDrPositive, difference);
            if (!matched)
                warnings.Add($"Reconciliation FAILED: opening + debits - credits = {ToBalanceInfo(running).Text}, but Tally reports closing {ToBalanceInfo(tallyClosingDrPositive.Value).Text} (difference {Math.Abs(difference):0.00}). Rows may be missing, or the opening/closing sign or period was read differently.");
        }
        else
        {
            reconciliation = new Reconciliation(false, false, tallyClosingDrPositive, null);
            warnings.Add("Opening and/or closing balance was not returned by Tally, so the running balance starts from zero and could not be reconciled.");
        }

        return new LedgerStatement(
            ledger, parse.Format, openingDrPositive.HasValue,
            ToBalanceInfo(openingDrPositive ?? 0m), ToBalanceInfo(running),
            totalDebit, totalCredit, rows.Count, rows, reconciliation, warnings,
            parse.VouchersSeen, parse.SkippedNotInvolved, parse.SkippedCancelled, parse.SkippedOptional);
    }
}

public static class StatementPrinter
{
    public static void Print(LedgerStatement s)
    {
        Console.WriteLine();
        Console.WriteLine($"Ledger: {s.Ledger}   (parsed as: {s.Format})");
        Console.WriteLine($"Opening balance: {s.Opening.Text}{(s.OpeningKnown ? "" : "  [not returned by Tally]")}");
        Console.WriteLine($"{"Date",-11} {"Vch No",-8} {"Type",-12} {"Particulars",-36} {"Debit",14} {"Credit",14} {"Balance",18}");
        foreach (var r in s.Rows)
        {
            var particulars = r.Particulars.Length > 36 ? r.Particulars[..33] + "..." : r.Particulars;
            Console.WriteLine($"{(r.DateIso.Length > 0 ? r.DateIso : r.Date),-11} {r.VoucherNumber,-8} {r.VoucherType,-12} {particulars,-36} {r.Debit,14:0.00} {r.Credit,14:0.00} {r.RunningBalance + " " + r.BalanceType,18}");
            if (r.Narration.Length > 0) Console.WriteLine($"{"",-11} narration: {r.Narration}");
        }
        Console.WriteLine($"{"Total",-69} {s.TotalDebit,14:0.00} {s.TotalCredit,14:0.00}");
        Console.WriteLine($"Closing balance: {s.Closing.Text}");
        Console.WriteLine(s.Reconciliation.Checked
            ? $"Reconciliation vs Tally closing: {(s.Reconciliation.Matched ? "MATCHED" : "MISMATCH")}"
            : "Reconciliation vs Tally closing: not checked");
        Console.WriteLine($"Vouchers seen {s.VouchersSeen}, other-ledger {s.SkippedNotInvolved}, cancelled {s.SkippedCancelled}, optional {s.SkippedOptional}");
        foreach (var warning in s.Warnings) Console.WriteLine("WARNING: " + warning);
    }
}
