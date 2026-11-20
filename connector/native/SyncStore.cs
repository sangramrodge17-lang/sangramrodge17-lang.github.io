using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

public sealed record SyncSummary(string Company, string From, string To, int VouchersUpserted, int EntriesUpserted, int BillsUpserted, string DatabasePath, string? Warning);
public sealed record TallyXmlDiagnostics(string Root, int VoucherNodes, int DisplayVoucherNodes, string[] LineErrors, string Preview);

public sealed record SyncStoreStatus(string DatabasePath, bool Ready, string? Company, string? SyncedFrom, string? SyncedTo, int VoucherCount, int EntryCount, int LedgerCount, string? LastError);
public sealed record LocalLedgerStatementResult(string Ledger, string From, string To, object[] Rows, decimal TotalDebit, decimal TotalCredit, decimal RunningBalance, int VouchersScanned, int VouchersMatched, string[] Warnings, string Source);
public sealed record SyncCategoryStatus(string Category, int VoucherCount, int EntryCount);

public static class SyncStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UniversalTallyConnector",
        "tally-sync.db");

    public static void EnsureCreated(string? databasePath = null)
    {
        var path = databasePath ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(path);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS company(guid TEXT PRIMARY KEY, name TEXT NOT NULL, books_from TEXT, synced_at TEXT, max_alterid INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS ledger(guid TEXT PRIMARY KEY, name TEXT NOT NULL, parent TEXT, alias TEXT, opening REAL, closing REAL, gstin TEXT, pan TEXT, alterid INTEGER);
            CREATE INDEX IF NOT EXISTS ix_ledger_name ON ledger(name);
            CREATE TABLE IF NOT EXISTS voucher(guid TEXT PRIMARY KEY, masterid INTEGER, alterid INTEGER, date TEXT, vtype TEXT, number TEXT, party TEXT, narration TEXT, is_cancelled INTEGER DEFAULT 0, is_optional INTEGER DEFAULT 0, raw_xml TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_v_date ON voucher(date);
            CREATE TABLE IF NOT EXISTS entry(id INTEGER PRIMARY KEY AUTOINCREMENT, voucher_guid TEXT NOT NULL, line_no INTEGER, ledger TEXT, amount REAL, is_party INTEGER DEFAULT 0, FOREIGN KEY(voucher_guid) REFERENCES voucher(guid) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS ix_e_ledger ON entry(ledger);
            CREATE INDEX IF NOT EXISTS ix_e_v ON entry(voucher_guid);
            CREATE TABLE IF NOT EXISTS bill(voucher_guid TEXT NOT NULL, ledger TEXT, bill_name TEXT, bill_type TEXT, amount REAL, FOREIGN KEY(voucher_guid) REFERENCES voucher(guid) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS inventory(voucher_guid TEXT NOT NULL, item TEXT, qty TEXT, rate TEXT, amount REAL, godown TEXT, FOREIGN KEY(voucher_guid) REFERENCES voucher(guid) ON DELETE CASCADE);
            CREATE VIEW IF NOT EXISTS ledger_statement AS
            SELECT e.ledger, v.date, v.vtype, v.number, v.narration,
                   CASE WHEN e.amount < 0 THEN -e.amount ELSE 0 END AS debit,
                   CASE WHEN e.amount > 0 THEN e.amount ELSE 0 END AS credit,
                   e.voucher_guid
            FROM entry e JOIN voucher v ON v.guid = e.voucher_guid
            WHERE v.is_cancelled = 0 AND v.is_optional = 0;
            """;
        cmd.ExecuteNonQuery();
    }

    public static SyncStoreStatus GetStatus(string? databasePath = null)
    {
        var path = databasePath ?? DefaultPath;
        try
        {
            EnsureCreated(path);
            using var db = Open(path);
            return new SyncStoreStatus(path, true,
                ScalarString(db, "SELECT name FROM company ORDER BY synced_at DESC LIMIT 1"),
                ScalarString(db, "SELECT MIN(date) FROM voucher"),
                ScalarString(db, "SELECT MAX(date) FROM voucher"),
                ScalarInt(db, "SELECT COUNT(*) FROM voucher"),
                ScalarInt(db, "SELECT COUNT(*) FROM entry"),
                ScalarInt(db, "SELECT COUNT(*) FROM ledger"), null);
        }
        catch (Exception ex)
        {
            return new SyncStoreStatus(path, false, null, null, null, 0, 0, 0, ex.Message);
        }
    }

    public static SyncSummary UpsertVoucherXml(string xml, string company, string from, string to, string? databasePath = null)
    {
        var path = databasePath ?? DefaultPath;
        EnsureCreated(path);
        var document = XDocument.Parse(CleanXml(xml), LoadOptions.PreserveWhitespace);
        // Tally returns full voucher nodes in some collection responses and
        // display-oriented DSPVOUCHER nodes in others. Both are valid sync input.
        var vouchers = document.Descendants().Where(x =>
            x.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase) ||
            x.Name.LocalName.Equals("DSPVOUCHER", StringComparison.OrdinalIgnoreCase)).ToArray();
        var voucherCount = 0;
        var entryCount = 0;
        var billCount = 0;
        using var db = Open(path);
        using var tx = db.BeginTransaction();
        foreach (var voucher in vouchers)
        {
            var guid = Value(voucher, "GUID");
            if (string.IsNullOrWhiteSpace(guid)) guid = $"master:{Value(voucher, "MASTERID")}|alter:{Value(voucher, "ALTERID")}|date:{Value(voucher, "DATE")}|no:{Value(voucher, "VOUCHERNUMBER")}";
            if (string.IsNullOrWhiteSpace(guid)) continue;
            Exec(db, tx, "DELETE FROM entry WHERE voucher_guid=$g", ("$g", guid));
            Exec(db, tx, "DELETE FROM bill WHERE voucher_guid=$g", ("$g", guid));
            Exec(db, tx, "DELETE FROM inventory WHERE voucher_guid=$g", ("$g", guid));
            Exec(db, tx, "INSERT OR REPLACE INTO voucher(guid,masterid,alterid,date,vtype,number,party,narration,is_cancelled,is_optional,raw_xml) VALUES($g,$m,$a,$d,$t,$n,$p,$nar,$c,$o,$raw)",
                ("$g", guid), ("$m", IntOrNull(Value(voucher, "MASTERID"))), ("$a", IntOrNull(Value(voucher, "ALTERID"))),
                ("$d", IsoDate(Value(voucher, "DATE"))), ("$t", Value(voucher, "VOUCHERTYPENAME")), ("$n", Value(voucher, "VOUCHERNUMBER")),
                ("$p", Value(voucher, "PARTYLEDGERNAME")), ("$nar", Value(voucher, "NARRATION")),
                ("$c", Yes(Value(voucher, "ISCANCELLED")) ? 1 : 0), ("$o", Yes(Value(voucher, "ISOPTIONAL")) ? 1 : 0), ("$raw", voucher.ToString(SaveOptions.DisableFormatting)));
            var line = 0;
            // Direct child entries are preferred. Descendants are used only when the voucher nests entries.
            var entries = voucher.Elements().Where(IsEntry).ToArray();
            if (entries.Length == 0) entries = voucher.Descendants().Where(IsEntry).ToArray();
            foreach (var entry in entries)
            {
                var ledger = Value(entry, "LEDGERNAME");
                if (string.IsNullOrWhiteSpace(ledger)) continue;
                Exec(db, tx, "INSERT INTO entry(voucher_guid,line_no,ledger,amount,is_party) VALUES($g,$l,$n,$a,$p)",
                    ("$g", guid), ("$l", ++line), ("$n", ledger), ("$a", Amount(Value(entry, "AMOUNT"))), ("$p", Yes(Value(entry, "ISPARTYLEDGER")) ? 1 : 0));
                entryCount++;
                foreach (var bill in entry.Elements().Where(x => x.Name.LocalName.Equals("BILLALLOCATIONS.LIST", StringComparison.OrdinalIgnoreCase)))
                {
                    Exec(db, tx, "INSERT INTO bill(voucher_guid,ledger,bill_name,bill_type,amount) VALUES($g,$l,$b,$t,$a)",
                        ("$g", guid), ("$l", ledger), ("$b", Value(bill, "NAME")), ("$t", Value(bill, "BILLTYPE")), ("$a", Amount(Value(bill, "AMOUNT"))));
                    billCount++;
                }
            }
            voucherCount++;
        }
        var maxAlter = ScalarInt(db, "SELECT COALESCE(MAX(alterid),0) FROM voucher", tx);
        Exec(db, tx, "INSERT OR REPLACE INTO company(guid,name,books_from,synced_at,max_alterid) VALUES($g,$n,$f,$s,$a)",
            ("$g", company), ("$n", company), ("$f", from), ("$s", DateTimeOffset.UtcNow.ToString("O")), ("$a", maxAlter));
        tx.Commit();
        return new SyncSummary(company, from, to, voucherCount, entryCount, billCount, path, vouchers.Length == 0 ? "Tally returned no VOUCHER nodes for this period." : null);
    }

    public static TallyXmlDiagnostics DescribeResponse(string xml)
    {
        var cleaned = CleanXml(xml ?? "");
        try
        {
            var document = XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace);
            var root = document.Root?.Name.LocalName ?? "(no root)";
            var voucherNodes = document.Descendants().Count(x => x.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase));
            var displayVoucherNodes = document.Descendants().Count(x => x.Name.LocalName.Equals("DSPVOUCHER", StringComparison.OrdinalIgnoreCase));
            var errors = document.Descendants().Where(x => x.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value.Trim()).Where(x => x.Length > 0).Distinct().Take(5).ToArray();
            return new TallyXmlDiagnostics(root, voucherNodes, displayVoucherNodes, errors, cleaned[..Math.Min(1200, cleaned.Length)]);
        }
        catch (Exception ex)
        {
            return new TallyXmlDiagnostics("(invalid XML)", 0, 0, new[] { ex.Message }, cleaned[..Math.Min(1200, cleaned.Length)]);
        }
    }

    public static SyncCategoryStatus[] GetCategoryStatus(string? databasePath = null)
    {
        var path = databasePath ?? DefaultPath;
        EnsureCreated(path);
        using var db = Open(path);
        var rows = new List<SyncCategoryStatus>();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT CASE
                WHEN lower(vtype) LIKE '%sales%' THEN 'Sales'
                WHEN lower(vtype) LIKE '%purchase%' THEN 'Purchase'
                WHEN lower(vtype) LIKE '%receipt%' THEN 'Receipt'
                WHEN lower(vtype) LIKE '%payment%' THEN 'Payment'
                WHEN lower(vtype) LIKE '%journal%' THEN 'Journal'
                WHEN lower(vtype) LIKE '%contra%' THEN 'Contra'
                ELSE 'Other'
            END AS category, COUNT(DISTINCT v.guid), COUNT(e.id)
            FROM voucher v LEFT JOIN entry e ON e.voucher_guid = v.guid
            WHERE v.is_cancelled = 0 AND v.is_optional = 0
            GROUP BY category ORDER BY category;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new SyncCategoryStatus(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        return rows.ToArray();
    }

    public static int UpsertLedgerMasters(JsonElement rows, string? databasePath = null)
    {
        if (rows.ValueKind != JsonValueKind.Array) return 0;
        var path = databasePath ?? DefaultPath;
        EnsureCreated(path);
        using var db = Open(path);
        using var tx = db.BeginTransaction();
        var count = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var name = JsonText(row, "Name") ?? JsonText(row, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            Exec(db, tx, "INSERT OR REPLACE INTO ledger(guid,name,parent,opening,closing) VALUES($g,$n,$p,$o,$c)",
                ("$g", $"name:{name.Trim().ToUpperInvariant()}"),
                ("$n", name.Trim()),
                ("$p", JsonText(row, "Parent") ?? ""),
                ("$o", Amount(JsonText(row, "OpeningBalance") ?? "")),
                ("$c", Amount(JsonText(row, "ClosingBalance") ?? "")));
            count++;
        }
        tx.Commit();
        return count;
    }

    public static object QueryModel3Summary(string kind, string from, string to, string? databasePath = null)
    {
        var path = databasePath ?? DefaultPath;
        EnsureCreated(path);
        using var db = Open(path);
        var normalizedFrom = IsoDate(from);
        var normalizedTo = IsoDate(to);
        var requested = (kind ?? "").Trim().ToLowerInvariant();
        if (requested is "balance" or "balance_sheet" or "balance-sheet" or "balancesheet")
        {
            var balanceRows = new List<object>();
            var assetTotal = 0m;
            var liabilityTotal = 0m;
            using var balance = db.CreateCommand();
            balance.CommandText = "SELECT name,parent,opening,closing FROM ledger WHERE abs(COALESCE(closing,0)) > 0.000001 ORDER BY name";
            using var balanceReader = balance.ExecuteReader();
            while (balanceReader.Read())
            {
                var name = balanceReader.IsDBNull(0) ? "" : balanceReader.GetString(0);
                var parent = balanceReader.IsDBNull(1) ? "" : balanceReader.GetString(1);
                var closing = balanceReader.IsDBNull(3) ? 0m : balanceReader.GetDecimal(3);
                var group = (parent + " " + name).ToLowerInvariant();
                var liability = group.Contains("liabilit") || group.Contains("capital") || group.Contains("creditor") || group.Contains("loan") || group.Contains("reserve") || group.Contains("provision");
                if (liability) liabilityTotal += Math.Abs(closing); else if (!group.Contains("income") && !group.Contains("expense")) assetTotal += Math.Abs(closing);
                balanceRows.Add(new Dictionary<string, object?> { ["Ledger"] = name, ["Parent"] = parent, ["Closing"] = closing, ["Side"] = liability ? "Liabilities & Capital" : "Assets" });
            }
            return new { kind = "balance-sheet", from, to, rows = balanceRows.ToArray(), rowCount = balanceRows.Count, totalAssets = assetTotal, totalLiabilitiesAndCapital = liabilityTotal, difference = assetTotal - liabilityTotal, source = "normalized-local-ledger-masters", warning = balanceRows.Count == 0 ? "No non-zero ledger closing balances were found in the local master snapshot." : "Balance-sheet totals are derived from synchronized ledger closing balances grouped by Tally parent/name. Review the displayed difference against TallyPrime before filing." };
        }
        if (requested is "bank" or "banks" or "cash" or "cash_position")
        {
            var bankRows = new List<object>();
            using var bank = db.CreateCommand();
            bank.CommandText = "SELECT name,parent,opening,closing FROM ledger WHERE lower(name) LIKE '%bank%' OR lower(parent) LIKE '%bank%' ORDER BY name";
            using var bankReader = bank.ExecuteReader();
            while (bankReader.Read())
            {
                bankRows.Add(new Dictionary<string, object?>
                {
                    ["Ledger"] = bankReader.GetString(0),
                    ["Parent"] = bankReader.IsDBNull(1) ? "" : bankReader.GetString(1),
                    ["Opening"] = bankReader.IsDBNull(2) ? 0m : bankReader.GetDecimal(2),
                    ["Closing"] = bankReader.IsDBNull(3) ? 0m : bankReader.GetDecimal(3),
                });
            }
            return new { kind = "bank", from, to, rows = bankRows.ToArray(), rowCount = bankRows.Count, source = "normalized-local-ledger-masters", warning = bankRows.Count == 0 ? "No ledger master matched Bank by name or parent group." : "Closing balances are the values returned by Tally during master synchronization." };
        }

        var category = requested.Contains("purchase") ? "purchase" : requested.Contains("sales") || requested.Contains("sale") ? "sales" : requested;
        var rows = new List<object>();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT v.date, v.vtype, v.number, COUNT(DISTINCT v.guid),
                   COALESCE(SUM(CASE WHEN e.amount < 0 THEN -e.amount ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN e.amount > 0 THEN e.amount ELSE 0 END),0)
            FROM voucher v LEFT JOIN entry e ON e.voucher_guid = v.guid
            WHERE lower(v.vtype) LIKE $category || '%' AND v.date >= $from AND v.date <= $to
              AND v.is_cancelled = 0 AND v.is_optional = 0
            GROUP BY v.date, v.vtype, v.number ORDER BY v.date, v.number;
            """;
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$from", normalizedFrom);
        command.Parameters.AddWithValue("$to", normalizedTo);
        var totalDebit = 0m;
        var totalCredit = 0m;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var debit = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4);
            var credit = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5);
            totalDebit += debit;
            totalCredit += credit;
            rows.Add(new Dictionary<string, object?>
            {
                ["Date"] = reader.IsDBNull(0) ? "" : reader.GetString(0),
                ["VoucherType"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ["VoucherNumber"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ["Debit"] = debit,
                ["Credit"] = credit,
            });
        }
        return new { kind = category, from, to, rows = rows.ToArray(), rowCount = rows.Count, totalDebit, totalCredit, source = "normalized-local-voucher-store", warning = rows.Count == 0 ? $"No {category} voucher rows were found in the synchronized period." : "Totals are derived deterministically from synchronized voucher entry lines; Gemini is used only to format the answer." };
    }

    public static LocalLedgerStatementResult QueryLedgerStatement(string ledger, string from, string to, string? databasePath = null)
    {
        var path = databasePath ?? DefaultPath;
        EnsureCreated(path);
        using var db = Open(path);
        var rows = new List<object>();
        var warnings = new List<string>();
        var running = 0m;
        var totalDebit = 0m;
        var totalCredit = 0m;
        var vouchersScanned = 0;
        var vouchersMatched = 0;
        var normalizedFrom = IsoDate(from);
        var normalizedTo = IsoDate(to);

        using (var count = db.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM voucher WHERE date >= $from AND date <= $to";
            count.Parameters.AddWithValue("$from", normalizedFrom);
            count.Parameters.AddWithValue("$to", normalizedTo);
            vouchersScanned = Convert.ToInt32(count.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT v.guid, v.date, v.vtype, v.number, v.narration, e.amount
            FROM entry e JOIN voucher v ON v.guid = e.voucher_guid
            WHERE lower(trim(e.ledger)) = lower(trim($ledger))
              AND v.date >= $from AND v.date <= $to
              AND v.is_cancelled = 0 AND v.is_optional = 0
            ORDER BY v.date, v.guid, e.line_no;
            """;
        command.Parameters.AddWithValue("$ledger", ledger.Trim());
        command.Parameters.AddWithValue("$from", normalizedFrom);
        command.Parameters.AddWithValue("$to", normalizedTo);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var voucherGuid = reader.GetString(0);
            var date = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var voucherType = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var number = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var narration = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var amount = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5);
            var debit = amount < 0 ? Math.Abs(amount) : 0m;
            var credit = amount > 0 ? amount : 0m;
            running += debit - credit;
            totalDebit += debit;
            totalCredit += credit;
            vouchersMatched++;

            var particulars = new List<string>();
            using var particularsCommand = db.CreateCommand();
            particularsCommand.CommandText = "SELECT DISTINCT ledger FROM entry WHERE voucher_guid = $guid AND lower(trim(ledger)) <> lower(trim($ledger)) AND trim(ledger) <> '' ORDER BY line_no";
            particularsCommand.Parameters.AddWithValue("$guid", voucherGuid);
            particularsCommand.Parameters.AddWithValue("$ledger", ledger.Trim());
            using var particularsReader = particularsCommand.ExecuteReader();
            while (particularsReader.Read()) particulars.Add(particularsReader.GetString(0));

            rows.Add(new Dictionary<string, object?>
            {
                ["Date"] = date,
                ["VoucherNumber"] = number,
                ["VoucherType"] = voucherType,
                ["Particulars"] = particulars.Count == 0 ? "" : string.Join(" | ", particulars),
                ["Narration"] = narration,
                ["Debit"] = debit,
                ["Credit"] = credit,
                ["RunningBalance"] = Math.Abs(running),
                ["BalanceType"] = running >= 0 ? "Dr" : "Cr",
            });
        }

        if (rows.Count == 0)
            warnings.Add("The local snapshot contains no matching ledger rows for the requested period. Sync this period first, then retry.");
        warnings.Add("Opening balance is not available in the local snapshot yet; running balance starts at zero and is not a Tally closing-balance reconciliation.");
        return new LocalLedgerStatementResult(ledger.Trim(), from, to, rows.ToArray(), totalDebit, totalCredit, running, vouchersScanned, vouchersMatched, warnings.ToArray(), "local-sqlite-sync");
    }

    private static bool IsEntry(XElement x) => x.Name.LocalName.Equals("ALLLEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase) || x.Name.LocalName.Equals("LEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase);
    private static bool Yes(string value) => value.Equals("Yes", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static string Value(XElement node, string name) => node.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
    private static string CleanXml(string xml) => Regex.Replace(xml ?? "", @"&#(?:[0-8]|1[0-1]|1[2-9]|2[0-9]|3[0-1]);|[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
    private static string IsoDate(string value) => value.Length == 8 ? $"{value[..4]}-{value[4..6]}-{value[6..]}" : value;
    private static decimal Amount(string value) => decimal.TryParse(value.Replace(",", "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    private static string? JsonText(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
    private static object IntOrNull(string value) => int.TryParse(value, out var n) ? n : DBNull.Value;
    private static SqliteConnection Open(string path) { var db = new SqliteConnection($"Data Source={path}"); db.Open(); return db; }
    private static void Exec(SqliteConnection db, SqliteTransaction tx, string sql, params (string Key, object Value)[] parameters) { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value); cmd.ExecuteNonQuery(); }
    private static string? ScalarString(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }
    private static int ScalarInt(SqliteConnection db, string sql, SqliteTransaction? tx = null) { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture); }
}
