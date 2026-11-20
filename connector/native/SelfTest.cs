using System.Xml.Linq;

/// <summary>
/// Run with:  UniversalTallyConnector.exe --selftest
/// No Tally and no test framework required. Exit code 0 = all passed.
/// The HDFC fixture mirrors the ledger the user showed in TallyPrime (5 journal vouchers).
/// SYNTHETIC parts, not taken from real Tally output: the XML wrapper, the narration text,
/// and the 9-Jul-25 voucher amount (the Tally screen showed no amount, so it is tested as zero).
/// </summary>
public static class SelfTest
{
    static int passed;
    static int failed;

    static void Check(string name, bool condition, string detail = "")
    {
        if (condition) { passed++; Console.WriteLine("  PASS  " + name); }
        else { failed++; Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
    }

    static LedgerStatement Run(string xml, string ledger, decimal? opening = null, decimal? tallyClosing = null)
    {
        var document = XDocument.Parse(TallyXml.Sanitize(xml));
        var parsed = TallyLedgerParser.Parse(document, ledger);
        return TallyLedgerParser.BuildStatement(ledger, parsed, opening, tallyClosing);
    }

    const string Hdfc = "HDFC BANK LTD_Buoyant";
    const string Demat = "Buoyant Capital Demat Account";

    static string JournalVoucher(string date, string number, string narration, string debitLedger, string creditLedger, string amount) => $"""
<VOUCHER VCHTYPE="Journal" ACTION="Create">
  <DATE>{date}</DATE><VOUCHERTYPENAME>Journal</VOUCHERTYPENAME><VOUCHERNUMBER>{number}</VOUCHERNUMBER>{(narration.Length > 0 ? $"<NARRATION>{narration}</NARRATION>" : "")}
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>{debitLedger}</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-{amount}</AMOUNT></ALLLEDGERENTRIES.LIST>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>{creditLedger}</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>{amount}</AMOUNT></ALLLEDGERENTRIES.LIST>
</VOUCHER>
""";

    static string Wrap(string vouchers) =>
        "<ENVELOPE><BODY><IMPORTDATA><REQUESTDATA><TALLYMESSAGE xmlns:UDF=\"TallyUDF\">" + vouchers + "</TALLYMESSAGE></REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>";

    public static int Run()
    {
        passed = 0; failed = 0;

        Console.WriteLine("1. HDFC BANK LTD_Buoyant, nested VOUCHER / ALLLEDGERENTRIES.LIST (5 journals)");
        var hdfcXml = Wrap(
            JournalVoucher("20250403", "7", "Test narration (synthetic)", Hdfc, Demat, "44858.03") +
            JournalVoucher("20250505", "26", "", Hdfc, Demat, "42721.36") +
            JournalVoucher("20250603", "43", "", Hdfc, Demat, "84676.83") +
            JournalVoucher("20250709", "54", "", Hdfc, Demat, "0") +
            JournalVoucher("20260331", "2575", "", Hdfc, "STCG on Sale of Investments", "2653.35"));
        var s1 = Run(hdfcXml, Hdfc);
        Check("five rows found", s1.Count == 5, "count=" + s1.Count);
        Check("format detected as nested-voucher", s1.Format == "nested-voucher", s1.Format);
        Check("total debit 174909.57", s1.TotalDebit == 174909.57m, s1.TotalDebit.ToString());
        Check("total credit 0", s1.TotalCredit == 0m);
        Check("all rows are debits", s1.Rows.All(r => r.CreditValue == 0m));
        Check("voucher numbers 7,26,43,54,2575", string.Join(",", s1.Rows.Select(r => r.VoucherNumber)) == "7,26,43,54,2575");
        Check("particulars = counter ledger", s1.Rows[0].Particulars == Demat && s1.Rows[4].Particulars == "STCG on Sale of Investments");
        Check("narration extracted", s1.Rows[0].Narration == "Test narration (synthetic)");
        Check("voucher type Journal", s1.Rows.All(r => r.VoucherType == "Journal"));
        Check("running balances 44858.03 / 87579.39 / 172256.22 / 172256.22 / 174909.57",
            string.Join(" / ", s1.Rows.Select(r => r.RunningBalance)) == "44858.03 / 87579.39 / 172256.22 / 172256.22 / 174909.57",
            string.Join(" / ", s1.Rows.Select(r => r.RunningBalance)));
        Check("closing 174909.57 Dr", s1.Closing.Text == "174909.57 Dr", s1.Closing.Text);
        Check("zero-amount voucher kept without breaking balance", s1.Rows[3].DebitValue == 0m && s1.Rows[3].CreditValue == 0m);

        Console.WriteLine("1b. Voucher nodes nested below Tally export containers");
        var nestedWrapper = "<ENVELOPE><BODY><DATA><COLLECTION>" +
            JournalVoucher("20250403", "N7", "Nested wrapper", Hdfc, Demat, "44858.03") +
            JournalVoucher("20250505", "N26", "", Hdfc, Demat, "42721.36") +
            "</COLLECTION></DATA></BODY></ENVELOPE>";
        var nestedStatement = Run(nestedWrapper, Hdfc);
        Check("nested wrapper still returns every voucher", nestedStatement.Count == 2, "count=" + nestedStatement.Count);
        Check("nested wrapper keeps exact counter ledger", nestedStatement.Rows.All(row => row.Particulars == Demat));

        Console.WriteLine("2. Opening balance + reconciliation against Tally closing");
        var s2 = Run(hdfcXml, Hdfc, opening: 10000m, tallyClosing: 184909.57m);
        Check("opening Dr 10000 carried into first running balance", s2.Rows[0].RunningBalance == "54858.03");
        Check("reconciliation matched", s2.Reconciliation.Checked && s2.Reconciliation.Matched);
        var s2b = Run(hdfcXml, Hdfc, opening: 10000m, tallyClosing: 999m);
        Check("reconciliation mismatch is detected and warned", s2b.Reconciliation.Checked && !s2b.Reconciliation.Matched && s2b.Warnings.Any(w => w.Contains("FAILED")));
        var s2c = Run(hdfcXml, Hdfc);
        Check("missing opening is flagged, not silently zero", !s2c.OpeningKnown && s2c.Warnings.Any(w => w.Contains("could not be reconciled")));
        var s2d = Run(hdfcXml, Hdfc, opening: -500m, tallyClosing: 174409.57m);
        Check("opening Cr balance handled (Cr 500 -> first balance 44358.03 Dr)", s2d.Rows[0].RunningBalance == "44358.03" && s2d.Reconciliation.Matched);

        Console.WriteLine("3. Credit side, plus LEDGERENTRIES.LIST variant");
        var receipt = Wrap("""
<VOUCHER VCHTYPE="Receipt"><DATE>20250410</DATE><VOUCHERTYPENAME>Receipt</VOUCHERTYPENAME><VOUCHERNUMBER>R1</VOUCHERNUMBER>
  <LEDGERENTRIES.LIST><LEDGERNAME>Party A</LEDGERNAME><AMOUNT>5000.00</AMOUNT></LEDGERENTRIES.LIST>
  <LEDGERENTRIES.LIST><LEDGERNAME>HDFC BANK LTD_Buoyant</LEDGERNAME><AMOUNT>-5000.00</AMOUNT></LEDGERENTRIES.LIST>
</VOUCHER>
""");
        var s3 = Run(receipt, "Party A");
        Check("party credited 5000", s3.Count == 1 && s3.Rows[0].CreditValue == 5000m && s3.Rows[0].DebitValue == 0m);
        Check("credit running balance shows Cr", s3.Rows[0].BalanceType == "Cr" && s3.Rows[0].RunningBalance == "5000.00");
        Check("particulars = bank", s3.Rows[0].Particulars == Hdfc);

        Console.WriteLine("4. Amount must be the entry's own AMOUNT, not a nested bill allocation");
        var trap = Wrap("""
<VOUCHER VCHTYPE="Sales"><DATE>20250411</DATE><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME><VOUCHERNUMBER>S1</VOUCHERNUMBER>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>Party A</LEDGERNAME>
    <BILLALLOCATIONS.LIST><NAME>INV-1</NAME><AMOUNT>-999.00</AMOUNT></BILLALLOCATIONS.LIST>
    <AMOUNT>-500.00</AMOUNT></ALLLEDGERENTRIES.LIST>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>Sales</LEDGERNAME><AMOUNT>500.00</AMOUNT></ALLLEDGERENTRIES.LIST>
</VOUCHER>
""");
        var s4 = Run(trap, "Party A");
        Check("debit is 500, not the 999 bill allocation", s4.Count == 1 && s4.Rows[0].DebitValue == 500m, s4.Rows.Count > 0 ? s4.Rows[0].DebitValue.ToString() : "no rows");

        Console.WriteLine("5. Item invoice: sales ledger lives inside ALLINVENTORYENTRIES.LIST / ACCOUNTINGALLOCATIONS.LIST");
        var invoice = Wrap("""
<VOUCHER VCHTYPE="Sales"><DATE>20250412</DATE><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME><VOUCHERNUMBER>S2</VOUCHERNUMBER>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>Party A</LEDGERNAME><AMOUNT>-118.00</AMOUNT></ALLLEDGERENTRIES.LIST>
  <ALLINVENTORYENTRIES.LIST><STOCKITEMNAME>Widget</STOCKITEMNAME>
    <ACCOUNTINGALLOCATIONS.LIST><LEDGERNAME>Sales - Test</LEDGERNAME><AMOUNT>100.00</AMOUNT></ACCOUNTINGALLOCATIONS.LIST>
  </ALLINVENTORYENTRIES.LIST>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output CGST</LEDGERNAME><AMOUNT>9.00</AMOUNT></ALLLEDGERENTRIES.LIST>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>Output SGST</LEDGERNAME><AMOUNT>9.00</AMOUNT></ALLLEDGERENTRIES.LIST>
</VOUCHER>
""");
        var s5 = Run(invoice, "Sales - Test");
        Check("sales ledger found inside inventory allocation", s5.Count == 1 && s5.Rows[0].CreditValue == 100m);
        Check("particulars = party", s5.Rows.Count == 1 && s5.Rows[0].Particulars == "Party A");
        var s5b = Run(invoice, "Party A");
        Check("party with several counter ledgers shows first (+n more)", s5b.Rows.Count == 1 && s5b.Rows[0].Particulars.StartsWith("Sales - Test (+2 more)"), s5b.Rows.Count > 0 ? s5b.Rows[0].Particulars : "no rows");

        Console.WriteLine("6. Skipping rules and name matching");
        var mixed = Wrap(
            JournalVoucher("20250501", "1", "", Hdfc, Demat, "100.00") +
            JournalVoucher("20250502", "2", "", "Some Other Ledger", Demat, "200.00") +
            """
<VOUCHER VCHTYPE="Journal"><DATE>20250503</DATE><VOUCHERTYPENAME>Journal</VOUCHERTYPENAME><VOUCHERNUMBER>3</VOUCHERNUMBER><ISCANCELLED>Yes</ISCANCELLED>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>HDFC BANK LTD_Buoyant</LEDGERNAME><AMOUNT>-300.00</AMOUNT></ALLLEDGERENTRIES.LIST></VOUCHER>
<VOUCHER VCHTYPE="Journal"><DATE>20250504</DATE><VOUCHERTYPENAME>Journal</VOUCHERTYPENAME><VOUCHERNUMBER>4</VOUCHERNUMBER><ISOPTIONAL>Yes</ISOPTIONAL>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>HDFC BANK LTD_Buoyant</LEDGERNAME><AMOUNT>-400.00</AMOUNT></ALLLEDGERENTRIES.LIST></VOUCHER>
""");
        var s6 = Run(mixed, "  hdfc   bank ltd_BUOYANT ");
        Check("ledger name match ignores case and extra spaces", s6.Count == 1 && s6.Rows[0].VoucherNumber == "1");
        Check("cancelled, optional and unrelated vouchers excluded", s6.SkippedCancelled == 1 && s6.SkippedOptional == 1 && s6.SkippedNotInvolved == 1);
        var s6b = Run(mixed, "HDFC Typo Name");
        Check("wrong ledger name gives zero rows plus a warning that lists names seen", s6b.Count == 0 && s6b.Warnings.Any(w => w.Contains("Ledger names seen")));

        Console.WriteLine("7. Ordering and netting");
        var unordered = Wrap(
            JournalVoucher("20250620", "B", "", Hdfc, Demat, "20.00") +
            JournalVoucher("20250105", "A", "", Hdfc, Demat, "10.00"));
        var s7 = Run(unordered, Hdfc);
        Check("rows sorted by date", s7.Rows[0].VoucherNumber == "A" && s7.Rows[1].VoucherNumber == "B");
        var net = Wrap("""
<VOUCHER VCHTYPE="Journal"><DATE>20250701</DATE><VOUCHERTYPENAME>Journal</VOUCHERTYPENAME><VOUCHERNUMBER>N1</VOUCHERNUMBER>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>HDFC BANK LTD_Buoyant</LEDGERNAME><AMOUNT>-100.00</AMOUNT></ALLLEDGERENTRIES.LIST>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>HDFC BANK LTD_Buoyant</LEDGERNAME><AMOUNT>30.00</AMOUNT></ALLLEDGERENTRIES.LIST>
  <ALLLEDGERENTRIES.LIST><LEDGERNAME>Other</LEDGERNAME><AMOUNT>70.00</AMOUNT></ALLLEDGERENTRIES.LIST>
</VOUCHER>
""");
        var s7b = Run(net, Hdfc);
        Check("same ledger twice in one voucher is netted (Dr 70)", s7b.Count == 1 && s7b.Rows[0].DebitValue == 70m && s7b.Rows[0].CreditValue == 0m);

        Console.WriteLine("8. XML hygiene");
        var dirty = Wrap(JournalVoucher("20250801", "D1", "", "&#4; " + Hdfc, Demat, "55.00"));
        var s8 = Run(dirty, Hdfc);
        Check("&#4; character reference in a ledger name no longer breaks parsing", s8.Count == 1 && s8.Rows[0].DebitValue == 55m);
        Check("Sanitize strips raw control character", TallyXml.Sanitize("a\u0004b") == "ab");

        Console.WriteLine("9. Amount and balance parsing");
        Check("ParseAmount handles commas", TallyLedgerParser.ParseAmount("-1,234.50") == -1234.50m);
        Check("ParseAmount takes forex value after '='", TallyLedgerParser.ParseAmount("$ 10 @ Rs.80/$ = Rs.800.00") == 800m);
        Check("balance '123.45 Dr' -> +123.45", TallyLedgerParser.ParseBalanceToDrPositive("123.45 Dr") == 123.45m);
        Check("balance '123.45 Cr' -> -123.45", TallyLedgerParser.ParseBalanceToDrPositive("123.45 Cr") == -123.45m);
        Check("bare '-500' (Tally Dr) -> +500", TallyLedgerParser.ParseBalanceToDrPositive("-500") == 500m);
        Check("bare '500' (Tally Cr) -> -500", TallyLedgerParser.ParseBalanceToDrPositive("500") == -500m);
        Check("empty balance -> 0, null -> null", TallyLedgerParser.ParseBalanceToDrPositive("") == 0m && TallyLedgerParser.ParseBalanceToDrPositive(null) is null);

        Console.WriteLine("10. Flat display-report layout (best effort, tag names inferred)");
        var flat = """
<ENVELOPE>
<DSPVCHDATE>3-Apr-25</DSPVCHDATE><DSPVCHLEDACCOUNT>Buoyant Capital Demat Account</DSPVCHLEDACCOUNT><DSPVCHTYPE>Journal</DSPVCHTYPE><DSPVCHDRAMT>44858.03</DSPVCHDRAMT><DSPVCHCRAMT></DSPVCHCRAMT>
<DSPVCHDATE>5-May-25</DSPVCHDATE><DSPVCHLEDACCOUNT>Buoyant Capital Demat Account</DSPVCHLEDACCOUNT><DSPVCHTYPE>Journal</DSPVCHTYPE><DSPVCHDRAMT>42721.36</DSPVCHDRAMT><DSPVCHCRAMT></DSPVCHCRAMT>
</ENVELOPE>
""";
        var s10 = Run(flat, Hdfc);
        Check("flat layout yields rows and is labelled best-effort", s10.Count == 2 && s10.Format == "flat-dsp" && s10.Warnings.Any(w => w.Contains("best effort")));
        Check("flat layout dates parsed (3-Apr-25 -> 2025-04-03)", s10.Rows[0].DateIso == "2025-04-03");

        Console.WriteLine("11. Local SQLite statement query");
        var localDb = Path.Combine(Path.GetTempPath(), "tally-selftest-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var localSync = SyncStore.UpsertVoucherXml(hdfcXml, "Test Company", "20250401", "20260331", localDb);
            var localStatement = SyncStore.QueryLedgerStatement(Hdfc, "20250401", "20260331", localDb);
            Check("local snapshot upserts five vouchers", localSync.VouchersUpserted == 5);
            Check("local query returns five matching rows", localStatement.Rows.Length == 5, "rows=" + localStatement.Rows.Length);
            Check("local query calculates debit total", localStatement.TotalDebit == 174909.57m, localStatement.TotalDebit.ToString());
            var firstLocalRow = localStatement.Rows[0] as IReadOnlyDictionary<string, object?>;
            Check("local query extracts counter-ledger particulars", firstLocalRow is not null && String.Equals(firstLocalRow["Particulars"]?.ToString(), Demat, StringComparison.Ordinal));
            Check("local query warns that opening is unavailable", localStatement.Warnings.Any(w => w.Contains("Opening balance")));
        }
        finally
        {
            if (File.Exists(localDb)) File.Delete(localDb);
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
