using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

const int connectorPort = 17891;
const int defaultTallyPort = 9000;
const string connectorVersion = "2.16.3-stable-sync";
var tallyPort = int.TryParse(Environment.GetEnvironmentVariable("TALLY_PORT"), out var configuredPort)
    ? configuredPort
    : defaultTallyPort;
var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
    .Replace("+", "-").Replace("/", "_").TrimEnd('=');

// Tally talks to localhost only, so it bypasses any system proxy. Exports of a full year of
// vouchers can be slow, so the timeout is generous.
using var handler = new SocketsHttpHandler
{
    UseProxy = false,
    ConnectTimeout = TimeSpan.FromSeconds(3),
    PooledConnectionLifetime = TimeSpan.FromSeconds(10),
};
using var tallyClient = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(180),
};

// Gemini lives on the internet: it needs the system proxy and its own timeout/retry handling.
// (Before v2.9.2 Gemini shared tallyClient, so its 12-second timeout and no-proxy setting
// caused "The operation was canceled".) Per-request timeouts are applied with a CancellationToken.
using var geminiHandler = new SocketsHttpHandler
{
    UseProxy = true,
    ConnectTimeout = TimeSpan.FromSeconds(20),
};
using var geminiClient = new HttpClient(geminiHandler)
{
    Timeout = Timeout.InfiniteTimeSpan,
};
var geminiModel = Environment.GetEnvironmentVariable("GEMINI_MODEL") is { Length: > 0 } configuredModel
    ? configuredModel
    : "gemini-3.5-flash-lite";
var optionalCaptureEnabled = string.Equals(Environment.GetEnvironmentVariable("TALLY_ENABLE_OPTIONAL_CAPTURE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("TALLY_ENABLE_OPTIONAL_CAPTURE"), "true", StringComparison.OrdinalIgnoreCase);
var syncProgress = new SyncProgressState();

// Command-line modes run without starting the HTTP listener:
//   --selftest | --capture "<ledger>" [from] [to] | --parse <file.xml> "<ledger>" [openingDrPositive]
if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
{
    Environment.Exit(await RunCliAsync(args));
}

using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{connectorPort}/");
listener.Start();
Console.Title = "Universal Tally Connector";
Console.WriteLine($"Universal Tally Connector {connectorVersion}");
Console.WriteLine($"Local status: http://127.0.0.1:{connectorPort}/health");
Console.WriteLine($"Tally endpoint: http://127.0.0.1:{tallyPort}");
Console.WriteLine($"Connector token: {token}");
Console.WriteLine("Keep this window open while using Tally Chat.");

while (true)
{
    var context = await listener.GetContextAsync();
    _ = Task.Run(() => HandleAsync(context));
}

async Task HandleAsync(HttpListenerContext context)
{
    context.Response.Headers["Access-Control-Allow-Origin"] = "*";
    context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Tally-Connector-Token";
    context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    if (context.Request.HttpMethod == "OPTIONS")
    {
        context.Response.StatusCode = 204;
        context.Response.Close();
        return;
    }

    var path = context.Request.Url?.AbsolutePath ?? "/";
    if (path == "/health")
    {
        await JsonAsync(context, 200, new
        {
            service = "Universal Tally Connector",
            version = connectorVersion,
            port = connectorPort,
            tallyPort,
            readOnly = false,
            protocol = "tally-xml-profiles",
            capabilities = new[] { "read", "local-gemini-planner", "local-gemini-answer", "ledger-statement", "local-sqlite-sync", "local-sqlite-ledger-query", "sync-status", "voucher-sync-preview", "ledger-create", "ledger-alter", "voucher-import-preview", "voucher-import-confirmed" },
        });
        return;
    }

    if (context.Request.Headers["X-Tally-Connector-Token"] != token)
    {
        await JsonAsync(context, 401, new { error = "Connector token required" });
        return;
    }

    if (path == "/api/tally/status")
    {
        await JsonAsync(context, 200, await ProbeTallyAsync());
        return;
    }

    if (path == "/api/ledgers")
    {
        await JsonAsync(context, 200, await GetLedgersAsync());
        return;
    }

    if (path == "/api/ledger-balance")
    {
        var ledger = context.Request.QueryString["ledger"]?.Trim();
        await JsonAsync(context, 200, await GetLedgerBalanceAsync(ledger));
        return;
    }

    if (path == "/api/trial-balance")
    {
        var from = context.Request.QueryString["from"]?.Trim();
        var to = context.Request.QueryString["to"]?.Trim();
        await JsonAsync(context, 200, await GetTrialBalanceAsync(from, to));
        return;
    }

    if (path == "/api/ledger-entries")
    {
        var ledger = context.Request.QueryString["ledger"]?.Trim();
        var from = context.Request.QueryString["from"]?.Trim();
        var to = context.Request.QueryString["to"]?.Trim();
        var strategy = context.Request.QueryString["strategy"]?.Trim();
        await JsonAsync(context, 200, await GetLedgerEntriesAsync(ledger, from, to, strategy));
        return;
    }



    if (path == "/api/ledger-entries-batch" && context.Request.HttpMethod == "POST")
    {
        try
        {
            var request = await ReadJsonAsync<LedgerBatchRequest>(context);
            await JsonAsync(context, 200, await GetLedgerEntriesBatchAsync(request));
        }
        catch (Exception ex) { await JsonAsync(context, 400, new { connected = false, rows = Array.Empty<object>(), error = ex.Message }); }
        return;
    }

    if (path == "/api/report")
    {
        var report = context.Request.QueryString["report"]?.Trim();
        var from = context.Request.QueryString["from"]?.Trim();
        var to = context.Request.QueryString["to"]?.Trim();
        await JsonAsync(context, 200, await GetTallyReportAsync(report, from, to));
        return;
    }

    if (path == "/api/sync/status")
    {
        await JsonAsync(context, 200, SyncStore.GetStatus());
        return;
    }

    if (path == "/api/sync/model3/status")
    {
        await JsonAsync(context, 200, new { connected = true, connectorVersion, progress = syncProgress.Snapshot(), store = SyncStore.GetStatus(), categories = SyncStore.GetCategoryStatus() });
        return;
    }

    if (path == "/api/sync/model3/snapshot")
    {
        var kind = context.Request.QueryString["kind"]?.Trim() ?? "company";
        var offset = int.TryParse(context.Request.QueryString["offset"], out var parsedOffset) ? parsedOffset : 0;
        var limit = int.TryParse(context.Request.QueryString["limit"], out var parsedLimit) ? parsedLimit : 250;
        await JsonAsync(context, 200, SyncStore.QuerySnapshotChunk(kind, offset, limit));
        return;
    }

    if (path == "/api/sync/model3/summary")
    {
        var kind = context.Request.QueryString["kind"]?.Trim() ?? "";
        var from = context.Request.QueryString["from"]?.Trim() ?? "";
        var to = context.Request.QueryString["to"]?.Trim() ?? "";
        await JsonAsync(context, 200, SyncStore.QueryModel3Summary(kind, from, to));
        return;
    }

    if (path == "/api/sync/model3/run" && context.Request.HttpMethod == "POST")
    {
        try
        {
            var request = await ReadJsonAsync<SyncRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Company) || (request.Mode.Equals("period", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(request.From) || string.IsNullOrWhiteSpace(request.To))))
            {
                await JsonAsync(context, 400, new { connected = false, error = "Company is required. Period sync also requires from and to dates; full sync does not." });
                return;
            }
            await JsonAsync(context, 200, await SyncModel3Async(request));
        }
        catch (Exception ex) { await JsonAsync(context, 400, new { connected = false, error = ex.Message }); }
        return;
    }

    if (path == "/api/sync/vouchers" && context.Request.HttpMethod == "POST")
    {
        try
        {
            var request = await ReadJsonAsync<SyncRequest>(context);
            if (request is null || string.IsNullOrWhiteSpace(request.Company) || (request.Mode.Equals("period", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(request.From) || string.IsNullOrWhiteSpace(request.To))))
            {
                await JsonAsync(context, 400, new { connected = false, error = "Company is required. Period sync also requires from and to dates; full sync does not." });
                return;
            }
            await JsonAsync(context, 200, await SyncVouchersAsync(request));
        }
        catch (Exception ex) { await JsonAsync(context, 400, new { connected = false, error = ex.Message }); }
        return;
    }

    if (path == "/api/sync/ledger-statement")
    {
        var ledger = context.Request.QueryString["ledger"]?.Trim();
        var requestedFrom = context.Request.QueryString["from"]?.Trim();
        var requestedTo = context.Request.QueryString["to"]?.Trim();
        var range = ResolveRange(requestedFrom, requestedTo);
        if (string.IsNullOrWhiteSpace(ledger))
        {
            await JsonAsync(context, 400, new { connected = false, error = "Ledger is required for a local statement query." });
            return;
        }
        await JsonAsync(context, 200, SyncStore.QueryLedgerStatement(ledger, range.From, range.To));
        return;
    }

    if (path == "/api/ai-answer" && context.Request.HttpMethod == "POST")
    {
        try
        {
            var request = await ReadJsonAsync<AiAnswerRequest>(context);
            if (request is null)
            {
                await JsonAsync(context, 400, new { configured = false, error = "Invalid Gemini request body." });
                return;
            }
            request = request with { Data = request.Data.Clone() };
            await JsonAsync(context, 200, await GenerateGeminiAnswerAsync(request));
        }
        catch (Exception ex)
        {
            await JsonAsync(context, 400, new { configured = false, error = $"Invalid Gemini request: {ex.Message}" });
        }
        return;
    }

    if (path == "/api/ai-plan" && context.Request.HttpMethod == "POST")
    {
        try
        {
            var request = await ReadJsonAsync<AiPlanRequest>(context);
            if (request is null)
            {
                await JsonAsync(context, 400, new { configured = false, error = "Invalid Gemini planner request body." });
                return;
            }
            await JsonAsync(context, 200, await GenerateGeminiPlanAsync(request));
        }
        catch (Exception ex)
        {
            await JsonAsync(context, 400, new { configured = false, error = $"Invalid Gemini planner request: {ex.Message}" });
        }
        return;
    }

    if (path == "/api/ledger/write" && context.Request.HttpMethod == "POST")
    {
        var request = await ReadJsonAsync<LedgerWriteRequest>(context);
        await JsonAsync(context, 200, await WriteLedgerAsync(request));
        return;
    }

    if (path == "/api/import" && context.Request.HttpMethod == "POST")
    {
        var request = await ReadJsonAsync<ImportRequest>(context);
        await JsonAsync(context, 200, await ImportXmlAsync(request));
        return;
    }

    await JsonAsync(context, 404, new { error = "Not found" });
}

async Task<object> GetLedgersAsync()
{
    var diagnostics = new List<object>();
    try
    {
        var body = await PostTallyAsync(LedgerCollectionXml(), Encoding.UTF8, "application/xml");
        var sanitized = SanitizeXml(body);
        var document = XDocument.Parse(sanitized);
        var rows = ExtractLedgerRows(document);
        diagnostics.Add(new { request = "explicit-ledger-collection", connectorVersion, preview = body[..Math.Min(500, body.Length)] });
        if (rows.Length > 0) return new { connected = true, transport = "xml", connectorVersion, rows, diagnostics };
    }
    catch (Exception ex)
    {
        var fallbackBody = await PostTallyAsync(LedgerCollectionXml(), Encoding.UTF8, "application/xml");
        var fallbackRows = ExtractLedgerNamesLoosely(fallbackBody);
        diagnostics.Add(new { request = "explicit-ledger-collection", connectorVersion, error = ex.Message, fallbackRows = fallbackRows.Length });
        if (fallbackRows.Length > 0) return new { connected = true, transport = "xml-tolerant", connectorVersion, rows = fallbackRows, diagnostics };
    }

    try
    {
        var body = await PostTallyAsync(ListOfAccountsXml(), Encoding.UTF8, "application/xml");
        var document = XDocument.Parse(SanitizeXml(body));
        var rows = ExtractLedgerRows(document);
        diagnostics.Add(new { request = "data-list-of-accounts", connectorVersion, preview = body[..Math.Min(500, body.Length)] });
        return new { connected = true, transport = "xml", connectorVersion, rows, diagnostics };
    }
    catch (Exception ex)
    {
        diagnostics.Add(new { request = "data-list-of-accounts", connectorVersion, error = ex.Message });
        return new { connected = false, transport = "xml", connectorVersion, rows = Array.Empty<object>(), error = "Tally responded, but no ledger rows were recognised.", diagnostics };
    }
}

async Task<object> GetLedgerBalanceAsync(string? ledger)
{
    if (string.IsNullOrWhiteSpace(ledger))
        return new { connected = false, rows = Array.Empty<object>(), error = "Ledger name is required." };
    try
    {
        var escaped = System.Security.SecurityElement.Escape(ledger) ?? ledger;
        var body = await PostTallyAsync($"""
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Object</TYPE><SUBTYPE>Ledger</SUBTYPE><ID TYPE="Name">{escaped}</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES><FETCHLIST><FETCH>Name</FETCH><FETCH>OpeningBalance</FETCH><FETCH>ClosingBalance</FETCH></FETCHLIST></DESC></BODY></ENVELOPE>
""", Encoding.UTF8, "application/xml");
        var document = XDocument.Parse(SanitizeXml(body));
        var ledgerNode = document.Descendants()
            .Where(node => node.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(node => string.Equals(ValueDeep(node, "NAME")?.Trim(), ledger.Trim(), StringComparison.OrdinalIgnoreCase));
        var row = ledgerNode is null ? null : new
        {
            Name = ValueDeep(ledgerNode, "NAME") ?? ledger,
            OpeningBalance = ValueDeep(ledgerNode, "OPENINGBALANCE") ?? ValueDeep(ledgerNode, "DSPOPBAL") ?? ValueDeep(ledgerNode, "DSPOPDRAMT") ?? ValueDeep(ledgerNode, "DSPOPCRAMT") ?? "not returned",
            ClosingBalance = ValueDeep(ledgerNode, "CLOSINGBALANCE") ?? ValueDeep(ledgerNode, "DSPCLBAL") ?? ValueDeep(ledgerNode, "DSPCLDRAMT") ?? ValueDeep(ledgerNode, "DSPCLCRAMT") ?? "not returned",
        };
        if (row is not null && (row.OpeningBalance != "not returned" || row.ClosingBalance != "not returned"))
            return new { connected = true, transport = "xml", rows = new object[] { row }, diagnostics = new[] { new { request = "ledger-object", preview = body[..Math.Min(500, body.Length)] } } };

        var reportBody = await PostTallyAsync(TrialBalanceXml(), Encoding.UTF8, "application/xml");
        var reportDocument = XDocument.Parse(SanitizeXml(reportBody));
        var reportRows = ExtractLedgerRowsMatching(reportDocument, ledger);
        return new { connected = true, transport = "xml", rows = reportRows, diagnostics = new[] { new { request = "trial-balance-report", preview = reportBody[..Math.Min(700, reportBody.Length)] } } };
    }
    catch (Exception ex)
    {
        return new { connected = false, rows = Array.Empty<object>(), error = ex.Message };
    }
}

async Task<object> SyncVouchersAsync(SyncRequest request)
{
    var full = request.Mode.Equals("full", StringComparison.OrdinalIgnoreCase);
    var from = request.From.Trim();
    var to = request.To.Trim();
    // Start with the collection request already proven against this Tally setup.
    // The company is recorded in SQLite; SVCURRENTCOMPANY remains intentionally
    // out of the first sync request until the real company XML is verified.
    var body = await PostTallyAsync(VoucherCollectionXml(string.IsNullOrWhiteSpace(from) ? null : from, string.IsNullOrWhiteSpace(to) ? null : to), Encoding.UTF8, "application/xml");
    var summary = SyncStore.UpsertVoucherXml(body, request.Company.Trim(), from, to);
    var tallyDiagnostics = SyncStore.DescribeResponse(body);
    return new
    {
        connected = true,
        connectorVersion,
        mode = full ? "full-company-read-only-sync" : "period-read-only-sync",
        summary,
        store = SyncStore.GetStatus(),
        tallyDiagnostics,
        warning = full ? "Full sync requested the complete voucher collection available from the currently open Tally company." : "Period sync fetched only the requested date range."
    };
}

async Task<object> SyncModel3Async(SyncRequest request)
{
    var company = request.Company.Trim();
    var full = request.Mode.Equals("full", StringComparison.OrdinalIgnoreCase);
    var from = request.From.Trim();
    var to = request.To.Trim();
    var stages = new List<object>();
    var warnings = new List<string>();
    var voucherTotal = 0;
    var entryTotal = 0;
    syncProgress.Start(company, full ? "full-company" : "period");
    syncProgress.Update("company-discovery", "Checking the company currently open in TallyPrime");

    var discovery = await DiscoverModel3CompanyAsync(company);
    stages.Add(new { stage = "Company discovery", status = discovery.Matched ? "complete" : "warning", detail = discovery.Detail });
    if (!discovery.Matched) warnings.Add($"The requested company was not returned by the Company collection. The connector continued with the currently open Tally company using {discovery.Strategy}.");

    try
    {
        var ledgerResponse = await GetLedgersAsync();
        var ledgerJson = JsonSerializer.SerializeToElement(ledgerResponse);
        var discoveredCount = ledgerJson.TryGetProperty("rows", out var discoveredRows) && discoveredRows.ValueKind == JsonValueKind.Array ? discoveredRows.GetArrayLength() : 0;
        var ledgerCount = SyncStore.UpsertLedgerMasters(discoveredRows, null);
        syncProgress.Update("masters", $"Staged {ledgerCount} ledger master(s)");
        stages.Add(new { stage = "Master synchronization", status = ledgerCount > 0 ? "complete" : "warning", detail = $"{ledgerCount} of {discoveredCount} ledger master(s) staged locally", rows = ledgerCount });
        if (ledgerCount == 0) warnings.Add("Tally returned no ledger masters during Model 3 discovery.");
    }
    catch (Exception ex)
    {
        stages.Add(new { stage = "Master synchronization", status = "warning", detail = ex.Message, rows = 0 });
        warnings.Add($"Master synchronization warning: {ex.Message}");
    }

    // Optional enrichment makes many individual requests to Tally. The older
    // connector's stable path did not do this, and the attached diagnostics
    // show Tally stopped accepting port 9000 after only three such requests.
    // Keep it opt-in so the primary voucher sync remains Tally-compatible.
    if (optionalCaptureEnabled)
    {
        try
        {
            var enrichment = await LedgerEnricher.RunAsync(xml => PostTallyAsync(xml, Encoding.UTF8, "application/xml"));
            stages.Add(new { stage = "Ledger enrichment (groups, GST/PAN, MSME, opening bills)", status = enrichment.Status, detail = enrichment.Detail, rows = enrichment.Updated });
            if (enrichment.Warning is not null) warnings.Add(enrichment.Warning);
        }
        catch (Exception ex)
        {
            stages.Add(new { stage = "Ledger enrichment (groups, GST/PAN, MSME, opening bills)", status = "warning", detail = ex.Message, rows = 0 });
            warnings.Add($"Ledger enrichment warning: {ex.Message}");
        }
    }
    else
    {
        stages.Add(new { stage = "Ledger enrichment (groups, GST/PAN, MSME, opening bills)", status = "skipped", detail = "Skipped in safe sync mode; enable TALLY_ENABLE_OPTIONAL_CAPTURE only after the primary voucher sync is stable.", rows = 0 });
    }

    // Tally already includes the voucher type on every node. Requesting Sales,
    // Purchase, Receipt, Payment, Journal and Contra separately duplicates the
    // full-company export. For a dated full sync, never send one oversized
    // request first: retrieve bounded windows directly so TallyPrime remains
    // responsive and every successful window is committed before continuing.
    var windows = DateWindows(from, to);
    var useBoundedWindows = full && windows.Count > 1;
    if (useBoundedWindows)
    {
        syncProgress.Update("vouchers-chunked", $"Starting safe bounded synchronization in {windows.Count} date windows");
        for (var windowNumber = 0; windowNumber < windows.Count; windowNumber++)
        {
            var window = windows[windowNumber];
            try
            {
                syncProgress.Update("vouchers-chunked", $"Retrieving date window {windowNumber + 1}/{windows.Count}: {window.From} to {window.To}", voucherTotal, entryTotal);
                var windowBody = await PostTallyAsync(VoucherCollectionXml(window.From, window.To), Encoding.UTF8, "application/xml");
                var windowDiagnostics = SyncStore.DescribeResponse(windowBody);
                if (windowDiagnostics.VoucherNodes + windowDiagnostics.DisplayVoucherNodes == 0)
                {
                    stages.Add(new { stage = $"Voucher window {window.From}–{window.To}", status = "warning", detail = "No voucher nodes returned", rows = 0 });
                    warnings.Add($"Voucher window {window.From}–{window.To} returned no recognised VOUCHER or DSPVOUCHER nodes.");
                    continue;
                }
                var windowSummary = SyncStore.UpsertVoucherXml(windowBody, company, window.From, window.To);
                voucherTotal += windowSummary.VouchersUpserted;
                entryTotal += windowSummary.EntriesUpserted;
                syncProgress.Update("vouchers-chunked", $"Stored window {windowNumber + 1}/{windows.Count}: {window.From} to {window.To}", voucherTotal, entryTotal);
                stages.Add(new { stage = $"Voucher window {window.From}–{window.To}", status = "complete", detail = $"{windowSummary.VouchersUpserted} voucher(s), {windowSummary.EntriesUpserted} entry line(s)", rows = windowSummary.VouchersUpserted });
            }
            catch (Exception windowError)
            {
                stages.Add(new { stage = $"Voucher window {window.From}–{window.To}", status = "failed", detail = windowError.Message, rows = 0 });
                warnings.Add($"Voucher window {window.From}–{window.To} failed: {windowError.Message}");
            }
        }
    }
    else
    {
        try
        {
            syncProgress.Update("vouchers", full ? "Requesting the complete voucher collection from TallyPrime (no date window supplied)" : $"Requesting vouchers from {from} to {to}");
            var body = await PostTallyAsync(VoucherCollectionXml(string.IsNullOrWhiteSpace(from) ? null : from, string.IsNullOrWhiteSpace(to) ? null : to), Encoding.UTF8, "application/xml");
            var diagnostics = SyncStore.DescribeResponse(body);
            if (diagnostics.VoucherNodes + diagnostics.DisplayVoucherNodes > 0)
            {
                var summary = SyncStore.UpsertVoucherXml(body, company, from, to);
                voucherTotal += summary.VouchersUpserted;
                entryTotal += summary.EntriesUpserted;
                syncProgress.Update("vouchers", $"Stored {summary.VouchersUpserted} voucher(s) from the complete collection", voucherTotal, entryTotal);
                stages.Add(new { stage = "Voucher collection (single pass)", status = "complete", detail = $"{summary.VouchersUpserted} voucher(s), {summary.EntriesUpserted} entry line(s)", rows = summary.VouchersUpserted });
            }
            else
            {
                stages.Add(new { stage = "Voucher collection (single pass)", status = "warning", detail = "No voucher nodes returned by the collection", rows = 0 });
                warnings.Add("The voucher collection returned no recognised VOUCHER or DSPVOUCHER nodes.");
            }
        }
        catch (Exception ex)
        {
            stages.Add(new { stage = "Voucher collection (single pass)", status = "failed", detail = ex.Message, rows = 0 });
            warnings.Add($"Voucher collection failed: {ex.Message}");
        }
    }

    // Raw master capture is also opt-in for the same reason: it can generate
    // many sequential Tally requests and must not destabilize the core sync.
    if (optionalCaptureEnabled)
    {
        try
        {
            var objects = await ObjectCapture.RunAsync(xml => PostTallyAsync(xml, Encoding.UTF8, "application/xml"));
            stages.Add(new { stage = "Master capture (all objects)", status = objects.Status, detail = objects.Detail, rows = objects.Updated });
            if (objects.Warning is not null) warnings.Add(objects.Warning);
        }
        catch (Exception ex)
        {
            stages.Add(new { stage = "Master capture (all objects)", status = "warning", detail = ex.Message, rows = 0 });
            warnings.Add($"Master capture warning: {ex.Message}");
        }
    }
    else
    {
        stages.Add(new { stage = "Master capture (all objects)", status = "skipped", detail = "Skipped in safe sync mode to protect TallyPrime availability.", rows = 0 });
    }

    stages.Add(new { stage = "Reconciliation", status = voucherTotal > 0 ? "complete" : "warning", detail = $"Local normalized store contains {voucherTotal} voucher(s) from this run" });
    syncProgress.Complete($"Synchronization completed with {voucherTotal} voucher(s) and {entryTotal} entry line(s)");
    var store = SyncStore.GetStatus();
    return new
    {
        connected = true,
        connectorVersion,
        mode = full ? "nexa-style-full-company-sync" : "nexa-style-period-sync",
        company = new { requested = company, matched = discovery.Matched ? company : "", guid = discovery.Guid, strategy = discovery.Strategy },
        stages,
        categories = SyncStore.GetCategoryStatus(),
        summary = new { vouchersUpserted = voucherTotal, entriesUpserted = entryTotal, databasePath = store.DatabasePath, warning = warnings.Count == 0 ? (string?)null : string.Join(" ", warnings) },
        store,
        warnings = warnings.ToArray(),
        syncMode = full ? "full" : "period",
        handover = new { dataset = "normalized-local-sqlite", completeCompanySnapshot = full, backendReadsVerifiedReports = true },
    };
}

async Task<(bool Matched, string? Guid, string Strategy, string Detail)> DiscoverModel3CompanyAsync(string requested)
{
    try
    {
        var body = await PostTallyAsync(Model3CompanyCollectionXml(), Encoding.UTF8, "application/xml");
        var document = XDocument.Parse(SanitizeXml(body));
        var company = document.Descendants().FirstOrDefault(node =>
            node.Name.LocalName.Equals("COMPANY", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ValueDeep(node, "NAME")?.Trim(), requested, StringComparison.OrdinalIgnoreCase));
        if (company is not null)
            return (true, ValueDeep(company, "GUID"), "company-collection", $"Exact company match: {requested}");
    }
    catch
    {
        // Some Tally builds do not expose Company as an exportable collection.
    }

    var probe = await PostTallyAsync(LedgerCollectionXml(), Encoding.UTF8, "application/xml");
    var probeDocument = XDocument.Parse(SanitizeXml(probe));
    var hasLedgers = probeDocument.Descendants().Any(node => node.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase));
    return (hasLedgers, null, "open-company-ledger-probe", hasLedgers ? $"Tally responded with ledger masters for the open company: {requested}" : "Tally did not return a usable company or ledger collection");
}

async Task<object> GetTrialBalanceAsync(string? from, string? to)
{
    try
    {
        var body = await PostTallyAsync(TrialBalanceXml(from, to), Encoding.UTF8, "application/xml");
        var document = XDocument.Parse(SanitizeXml(body));
        var rows = ExtractLedgerRows(document);
        if (rows.Length > 0)
            return new { connected = true, transport = "xml", connectorVersion, from, to, rows, source = "trial-balance-report", warnings = Array.Empty<string>(), diagnostics = new[] { new { request = "trial-balance-report", preview = body[..Math.Min(700, body.Length)] } } };

        // Some TallyPrime configurations return an empty/display-only Trial Balance
        // export even though the ledger collection is populated. Use the same
        // collection that powers the complete ledger directory as a read-only fallback.
        var collectionBody = await PostTallyAsync(LedgerCollectionXml(), Encoding.UTF8, "application/xml");
        var collectionDocument = XDocument.Parse(SanitizeXml(collectionBody));
        var collectionRows = ExtractLedgerRows(collectionDocument);
        return new
        {
            connected = true,
            transport = "xml",
            connectorVersion,
            from,
            to,
            rows = collectionRows,
            source = "ledger-collection-fallback",
            warnings = collectionRows.Length > 0
                ? new[] { "TallyPrime returned no parseable Trial Balance rows; values are from the ledger collection and may reflect the current company period rather than the requested report period." }
                : new[] { "TallyPrime returned no parseable Trial Balance rows and the ledger collection was also empty." },
            diagnostics = new[]
            {
                new { request = "trial-balance-report", rows = 0, preview = body[..Math.Min(700, body.Length)] },
                new { request = "ledger-collection-fallback", rows = collectionRows.Length, preview = collectionBody[..Math.Min(700, collectionBody.Length)] },
            },
        };
    }
    catch (Exception ex)
    {
        return new { connected = false, transport = "xml", connectorVersion, from, to, rows = Array.Empty<object>(), error = ex.Message };
    }
}

async Task<object> GetLedgerEntriesAsync(string? ledger, string? from, string? to, string? strategy = null)
{
    if (string.IsNullOrWhiteSpace(ledger)) return new { connected = false, rows = Array.Empty<object>(), error = "Ledger name is required." };
    return LedgerStatementJson(await BuildLedgerStatementAsync(ledger.Trim(), from, to, strategy), from, to);
}

object LedgerStatementJson(StatementRun run, string? from, string? to)
{
    if (!run.TallyResponded)
        return new { connected = false, transport = "xml", connectorVersion, ledger = run.Statement.Ledger, from, to, rows = Array.Empty<object>(), error = run.Error ?? "Tally did not return a usable response.", diagnostics = run.Diagnostics };
    var s = run.Statement;
    return new
    {
        connected = true,
        transport = "xml",
        connectorVersion,
        ledger = s.Ledger,
        from = run.From,
        to = run.To,
        strategy = run.Strategy,
        format = s.Format,
        rows = s.Rows,
        openingBalance = s.Opening,
        openingKnown = s.OpeningKnown,
        totals = new { debit = s.TotalDebit, credit = s.TotalCredit, count = s.Count },
        closingBalance = s.Closing,
        reconciliation = s.Reconciliation,
        counts = new { vouchersSeen = s.VouchersSeen, otherLedger = s.SkippedNotInvolved, cancelled = s.SkippedCancelled, optional = s.SkippedOptional },
        warnings = s.Warnings,
        diagnostics = run.Diagnostics,
    };
}

async Task<object> GetLedgerEntriesBatchAsync(LedgerBatchRequest? request)
{
    var ledgers = request?.Ledgers?.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToArray() ?? Array.Empty<string>();
    if (ledgers.Length == 0) return new { connected = false, rows = Array.Empty<object>(), error = "At least one ledger name is required." };
    var results = new List<object>();
    // Sequential on purpose: TallyPrime serves one XML request at a time.
    foreach (var ledger in ledgers)
        results.Add(LedgerStatementJson(await BuildLedgerStatementAsync(ledger, request?.From, request?.To, null), request?.From, request?.To));
    return new { connected = true, connectorVersion, from = request?.From, to = request?.To, ledgers = results };
}

static (string From, string To) ResolveRange(string? requestedFrom, string? requestedTo)
{
    var parsedTo = DateTime.TryParseExact(requestedTo, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var requestedDate) ? requestedDate : DateTime.Now;
    var startYear = parsedTo.Month >= 4 ? parsedTo.Year : parsedTo.Year - 1;
    var from = string.IsNullOrWhiteSpace(requestedFrom) ? $"{startYear:0000}0401" : requestedFrom.Trim();
    var to = string.IsNullOrWhiteSpace(requestedTo) ? $"{startYear + 1:0000}0331" : requestedTo.Trim();
    return (from, to);
}

static string LedgerBalanceXml(string ledger, string from, string to)
{
    var escaped = System.Security.SecurityElement.Escape(ledger.Trim()) ?? ledger.Trim();
    return $"""
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Object</TYPE><SUBTYPE>Ledger</SUBTYPE><ID TYPE="Name">{escaped}</ID></HEADER><BODY><DESC><STATICVARIABLES><SVFROMDATE TYPE="Date">{from}</SVFROMDATE><SVTODATE TYPE="Date">{to}</SVTODATE><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES><FETCHLIST><FETCH>Name</FETCH><FETCH>OpeningBalance</FETCH><FETCH>ClosingBalance</FETCH></FETCHLIST></DESC></BODY></ENVELOPE>
""";
}

/// <summary>Opening/closing balance of one ledger for the period, as DrPositive. Null = not returned.</summary>
async Task<(decimal? OpeningDr, decimal? ClosingDr, string? Error, string Preview)> FetchLedgerBalancesAsync(string ledger, string from, string to)
{
    try
    {
        var body = await PostTallyAsync(LedgerBalanceXml(ledger, from, to), Encoding.UTF8, "application/xml");
        var document = XDocument.Parse(SanitizeXml(body));
        var preview = body[..Math.Min(500, body.Length)];
        var ledgerNodes = document.Descendants().Where(element => element.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase)).ToList();
        var wanted = TallyLedgerParser.NormalizeName(ledger);
        var node = ledgerNodes.FirstOrDefault(candidate => TallyLedgerParser.NormalizeName(ValueDeep(candidate, "NAME")) == wanted)
                   ?? (ledgerNodes.Count == 1 ? ledgerNodes[0] : null);
        if (node is null) return (null, null, "Ledger object not found in Tally response.", preview);
        decimal? Balance(string tag)
        {
            var element = node.Descendants().FirstOrDefault(child => child.Name.LocalName.Equals(tag, StringComparison.OrdinalIgnoreCase));
            return element is null ? null : TallyLedgerParser.ParseBalanceToDrPositive(element.Value);
        }
        decimal? DisplayBalance(string debitTag, string creditTag)
        {
            var debit = node.Descendants().FirstOrDefault(child => child.Name.LocalName.Equals(debitTag, StringComparison.OrdinalIgnoreCase));
            if (debit is not null && debit.Value.Trim().Length > 0)
                return Math.Abs(TallyLedgerParser.ParseAmount(debit.Value));
            var credit = node.Descendants().FirstOrDefault(child => child.Name.LocalName.Equals(creditTag, StringComparison.OrdinalIgnoreCase));
            if (credit is not null && credit.Value.Trim().Length > 0)
                return -Math.Abs(TallyLedgerParser.ParseAmount(credit.Value));
            return null;
        }
        var opening = Balance("OPENINGBALANCE") ?? Balance("DSPOPBAL") ?? DisplayBalance("DSPOPDRAMT", "DSPOPCRAMT");
        var closing = Balance("CLOSINGBALANCE") ?? Balance("DSPCLBAL") ?? DisplayBalance("DSPCLDRAMT", "DSPCLCRAMT");
        return (opening, closing, null, preview);
    }
    catch (Exception ex)
    {
        return (null, null, ex.Message, "");
    }
}

async Task<StatementRun> BuildLedgerStatementAsync(string ledger, string? requestedFrom, string? requestedTo, string? forcedStrategy)
{
    var (from, to) = ResolveRange(requestedFrom, requestedTo);
    var diagnostics = new List<object>();
    var strategies = forcedStrategy?.ToLowerInvariant() switch
    {
        "voucher-collection" => new[] { "voucher-collection" },
        "daybook" => new[] { "daybook" },
        "ledger-report" => new[] { "ledger-report" },
        _ => new[] { "voucher-collection", "ledger-report", "daybook" },
    };

    ParseResult? best = null;
    var used = "";
    var anyResponse = false;
    string? lastError = null;
    foreach (var strategy in strategies)
    {
        try
        {
            var request = strategy switch
            {
                "voucher-collection" => VoucherCollectionXml(from, to),
                "daybook" => TallyReportXml("Day Book", from, to),
                _ => LedgerEntriesXml(ledger, from, to),
            };
            var body = await PostTallyAsync(request, Encoding.UTF8, "application/xml");
            anyResponse = true;
            var parsed = TallyLedgerParser.Parse(XDocument.Parse(SanitizeXml(body)), ledger);
            if (strategy == "voucher-collection")
            {
                var fromDate = TallyLedgerParser.ParseDate(from);
                var toDate = TallyLedgerParser.ParseDate(to);
                parsed.Rows.RemoveAll(row => row.DateValue.HasValue && fromDate.HasValue && toDate.HasValue &&
                    (row.DateValue.Value < fromDate.Value || row.DateValue.Value > toDate.Value));
            }
            diagnostics.Add(new
            {
                strategy,
                bytes = body.Length,
                tallyStatus = DetectTallyStatus(body),
                format = parsed.Format,
                rows = parsed.Rows.Count,
                vouchersSeen = parsed.VouchersSeen,
                otherLedger = parsed.SkippedNotInvolved,
                sampleLedgerNames = parsed.SampleLedgerNames,
                preview = body[..Math.Min(600, body.Length)],
            });
            static int Score(ParseResult candidate) =>
                candidate.Rows.Count * 100 + (candidate.Format == "nested-voucher" ? 10 : 0);
            if (best is null || Score(parsed) > Score(best)) { best = parsed; used = strategy; }
            // A flat display response can contain only the currently highlighted row.
            // Keep searching so the Day Book voucher response can supply the complete ledger.
            if (parsed.Rows.Count > 0 && parsed.Format == "nested-voucher") break;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            diagnostics.Add(new { strategy, error = ex.Message });
        }
    }

    var empty = TallyLedgerParser.BuildStatement(ledger, best ?? new ParseResult(), null, null);
    if (!anyResponse) return new StatementRun(empty, diagnostics, "", false, from, to, lastError);

    var balances = await FetchLedgerBalancesAsync(ledger, from, to);
    diagnostics.Add(new { strategy = "ledger-balance", error = balances.Error, opening = balances.OpeningDr, closing = balances.ClosingDr, preview = balances.Preview });
    var statement = TallyLedgerParser.BuildStatement(ledger, best ?? new ParseResult(), balances.OpeningDr, balances.ClosingDr);
    return new StatementRun(statement, diagnostics, used, true, from, to, null);
}

async Task<object> GetTallyReportAsync(string? report, string? from, string? to)
{
    var key = (report ?? "").Trim().ToLowerInvariant();
    var reportName = key switch
    {
        "receivables" or "outstanding-receivables" or "bills-receivable" => "Bills Receivable",
        "payables" or "outstanding-payables" or "bills-payable" => "Bills Payable",
        "profit-loss" or "profit and loss" or "p&l" => "Profit and Loss",
        "balance-sheet" or "balance sheet" => "Balance Sheet",
        "day-book" or "day book" => "Day Book",
        "sales" or "sales-register" => "Sales Register",
        "purchase" or "purchase-register" => "Purchase Register",
        _ => null,
    };
    if (reportName is null) return new { connected = false, report, rows = Array.Empty<object>(), error = "Unsupported report." };
    try
    {
        var body = await PostTallyAsync(TallyReportXml(reportName, from, to), Encoding.UTF8, "application/xml");
        var document = XDocument.Parse(SanitizeXml(body));
        var rows = reportName is "Day Book" or "Sales Register" or "Purchase Register" ? ExtractTransactionRows(document) : ExtractLedgerRows(document);
        return new { connected = true, transport = "xml", connectorVersion, report = reportName, from, to, rows, diagnostics = new[] { new { request = reportName, preview = body[..Math.Min(1200, body.Length)] } } };
    }
    catch (Exception ex) { return new { connected = false, transport = "xml", connectorVersion, report = reportName, rows = Array.Empty<object>(), error = ex.Message }; }
}

async Task<string> PostTallyAsync(string payload, Encoding encoding, string mediaType)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{tallyPort}/")
    {
        Content = new ByteArrayContent(encoding.GetBytes(payload)),
    };
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
    request.Content.Headers.ContentType.CharSet = encoding == Encoding.Unicode ? "utf-16" : "utf-8";
    request.Headers.ConnectionClose = true;
    request.Headers.ExpectContinue = false;
    using var response = await tallyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Tally returned HTTP {(int)response.StatusCode}: {body[..Math.Min(240, body.Length)]}");
    return body;
}

async Task<object> WriteLedgerAsync(LedgerWriteRequest? request)
{
    if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Action))
        return new { ok = false, error = "Ledger name and action (Create or Alter) are required." };
    var action = request.Action.Trim().Equals("Alter", StringComparison.OrdinalIgnoreCase) ? "Alter" : "Create";
    if (action == "Create" && string.IsNullOrWhiteSpace(request.Parent))
        return new { ok = false, error = "Parent group is required when creating a ledger." };
    var name = System.Security.SecurityElement.Escape(request.Name.Trim()) ?? request.Name.Trim();
    var parent = System.Security.SecurityElement.Escape(request.Parent?.Trim() ?? "") ?? "";
    var opening = System.Security.SecurityElement.Escape(request.OpeningBalance?.Trim() ?? "0") ?? "0";
    var xml = $"""
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Import Data</TALLYREQUEST></HEADER><BODY><IMPORTDATA><REQUESTDESC><REPORTNAME>All Masters</REPORTNAME></REQUESTDESC><REQUESTDATA><TALLYMESSAGE xmlns:UDF="TallyUDF"><LEDGER NAME="{name}" Action="{action}"><NAME>{name}</NAME>{(action == "Create" ? $"<PARENT>{parent}</PARENT>" : "")}<OPENINGBALANCE>{opening}</OPENINGBALANCE></LEDGER></TALLYMESSAGE></REQUESTDATA></IMPORTDATA></BODY></ENVELOPE>
""";
    if (!request.Confirm)
        return new { ok = true, preview = true, action, name = request.Name.Trim(), parent = request.Parent?.Trim() ?? "", xml };
    return await SendImportAsync(xml, action, request.Name.Trim());
}

async Task<object> ImportXmlAsync(ImportRequest? request)
{
    if (request is null || string.IsNullOrWhiteSpace(request.Xml)) return new { ok = false, error = "XML payload is required." };
    if (!request.Confirm) return new { ok = true, preview = true, xml = request.Xml };
    if (!request.Xml.Contains("<TALLYREQUEST>Import Data</TALLYREQUEST>", StringComparison.OrdinalIgnoreCase))
        return new { ok = false, error = "Only Tally Import Data XML is accepted." };
    return await SendImportAsync(request.Xml, "Import", null);
}

async Task<object> SendImportAsync(string xml, string action, string? name)
{
    try
    {
        var response = await PostTallyAsync(xml, Encoding.UTF8, "application/xml");
        var document = XDocument.Parse(SanitizeXml(response));
        var root = document.Root;
        var created = root is null ? "0" : ValueDeep(root, "CREATED") ?? "0";
        var altered = root is null ? "0" : ValueDeep(root, "ALTERED") ?? "0";
        var errors = root is null ? "0" : ValueDeep(root, "ERRORS") ?? "0";
        return new { ok = errors == "0", preview = false, action, name, created, altered, errors, responsePreview = response[..Math.Min(1200, response.Length)] };
    }
    catch (Exception ex)
    {
        return new { ok = false, action, name, error = ex.Message };
    }
}

static async Task<T?> ReadJsonAsync<T>(HttpListenerContext context)
{
    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}

static string SanitizeXml(string body) => TallyXml.Sanitize(body);

static string? ValueDeep(XElement parent, string name)
{
    var element = parent.DescendantsAndSelf().FirstOrDefault(node => node.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (element is not null && !string.IsNullOrWhiteSpace(element.Value)) return element.Value.Trim();
    var attribute = parent.DescendantsAndSelf().Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
    return attribute?.Value.Trim();
}

async Task<object> ProbeTallyAsync()
{
    var attempts = new[]
    {
        new XmlProfile("application/xml", Encoding.UTF8, LedgerCollectionXml()),
        new XmlProfile("application/xml", Encoding.Unicode, LedgerCollectionXml()),
        new XmlProfile("text/xml", Encoding.UTF8, LedgerCollectionXml()),
    };
    var results = new List<object>();

    foreach (var profile in attempts)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{tallyPort}/")
            {
                Content = new ByteArrayContent(profile.Encoding.GetBytes(profile.Payload)),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(profile.MediaType);
            request.Content.Headers.ContentType.CharSet = profile.Encoding == Encoding.Unicode ? "utf-16" : "utf-8";
            request.Headers.ConnectionClose = true;
            request.Headers.ExpectContinue = false;

            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); // keep the health probe fast
            using var response = await tallyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token);
            var body = await response.Content.ReadAsStringAsync(probeTimeout.Token);
            var status = DetectTallyStatus(body);
            var result = new
            {
                mediaType = profile.MediaType,
                charset = profile.Encoding == Encoding.Unicode ? "utf-16" : "utf-8",
                httpStatus = (int)response.StatusCode,
                tallyStatus = status,
                preview = body[..Math.Min(300, body.Length)],
            };
            results.Add(result);
            if (response.IsSuccessStatusCode || status == "1")
            {
                return new
                {
                    connected = true,
                    transport = "xml",
                    tallyPort,
                    protocol = "collection:List of Ledgers",
                    attempts = results,
                };
            }
        }
        catch (TaskCanceledException)
        {
            results.Add(new { mediaType = profile.MediaType, charset = profile.Encoding == Encoding.Unicode ? "utf-16" : "utf-8", error = "Tally did not respond before the timeout." });
        }
        catch (Exception ex)
        {
            results.Add(new { mediaType = profile.MediaType, charset = profile.Encoding == Encoding.Unicode ? "utf-16" : "utf-8", error = ex.Message });
        }
    }

    return new
    {
        connected = false,
        transport = "xml",
        tallyPort,
        error = "Port is reachable, but Tally did not return a valid XML response to the supported collection request.",
        hint = "Confirm the company is open and HTTP Server is enabled. The attempts below identify the encoding/profile that Tally accepts.",
        attempts = results,
    };
}

static string LedgerCollectionXml() => """
<ENVELOPE>
  <HEADER>
    <VERSION>1</VERSION>
    <TALLYREQUEST>Export</TALLYREQUEST>
    <TYPE>Collection</TYPE>
    <ID>Universal Ledger Collection</ID>
  </HEADER>
  <BODY>
    <DESC>
      <STATICVARIABLES>
        <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
      </STATICVARIABLES>
      <TDL>
        <TDLMESSAGE>
          <COLLECTION NAME="Universal Ledger Collection">
            <TYPE>Ledger</TYPE>
            <FETCH>Name</FETCH>
            <FETCH>Parent</FETCH>
            <FETCH>OpeningBalance</FETCH>
            <FETCH>ClosingBalance</FETCH>
          </COLLECTION>
        </TDLMESSAGE>
      </TDL>
    </DESC>
  </BODY>
</ENVELOPE>
""";

static string ListOfAccountsXml() => """
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>List of Accounts</ID></HEADER><BODY><DESC><STATICVARIABLES><AccountType>Ledgers</AccountType><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES></DESC></BODY></ENVELOPE>
""";

static string Model3CompanyCollectionXml() => """
<ENVELOPE>
  <HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>Model 3 Company Collection</ID></HEADER>
  <BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES><TDL><TDLMESSAGE>
    <COLLECTION NAME="Model 3 Company Collection"><TYPE>Company</TYPE><FETCH>Name</FETCH><FETCH>GUID</FETCH><FETCH>StartingFrom</FETCH></COLLECTION>
  </TDLMESSAGE></TDL></DESC></BODY>
</ENVELOPE>
""";

static string TrialBalanceXml(string? requestedFrom = null, string? requestedTo = null)
{
    var today = DateTime.Now;
    var parsedTo = DateTime.TryParseExact(requestedTo, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var requestedDate) ? requestedDate : today;
    var startYear = parsedTo.Month >= 4 ? parsedTo.Year : parsedTo.Year - 1;
    var from = string.IsNullOrWhiteSpace(requestedFrom) ? $"{startYear:0000}0401" : requestedFrom;
    var to = string.IsNullOrWhiteSpace(requestedTo) ? $"{startYear + 1:0000}0331" : requestedTo;
    return $"""
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>Trial Balance</ID></HEADER><BODY><DESC><STATICVARIABLES><SVFROMDATE TYPE="Date">{from}</SVFROMDATE><SVTODATE TYPE="Date">{to}</SVTODATE><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES></DESC></BODY></ENVELOPE>
""";
}

static string LedgerEntriesXml(string ledger, string? requestedFrom = null, string? requestedTo = null)
{
    var escapedLedger = System.Security.SecurityElement.Escape(ledger.Trim()) ?? ledger.Trim();
    var today = DateTime.Now;
    var parsedTo = DateTime.TryParseExact(requestedTo, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var requestedDate) ? requestedDate : today;
    var startYear = parsedTo.Month >= 4 ? parsedTo.Year : parsedTo.Year - 1;
    var from = string.IsNullOrWhiteSpace(requestedFrom) ? $"{startYear:0000}0401" : requestedFrom;
    var to = string.IsNullOrWhiteSpace(requestedTo) ? $"{startYear + 1:0000}0331" : requestedTo;
    return $"""
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>Ledger Vouchers</ID></HEADER><BODY><DESC><STATICVARIABLES><LEDGERNAME>{escapedLedger}</LEDGERNAME><SVFROMDATE TYPE="Date">{from}</SVFROMDATE><SVTODATE TYPE="Date">{to}</SVTODATE><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES></DESC></BODY></ENVELOPE>
""";
}

// The built-in Ledger Vouchers and Day Book reports can return a display-oriented
// response (or the currently highlighted row) instead of the full voucher graph.
// A custom Voucher collection asks Tally for the underlying voucher ledger entries.
static string VoucherCollectionXml(string? from, string? to, string? category = null) => $"""
<ENVELOPE>
  <HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>Universal Voucher Collection</ID></HEADER>
  <BODY><DESC>
    <STATICVARIABLES>
      {(string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) ? "" : $"<SVFROMDATE TYPE=\"Date\">{from}</SVFROMDATE><SVTODATE TYPE=\"Date\">{to}</SVTODATE>")}
      <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
    </STATICVARIABLES>
    <TDL><TDLMESSAGE>
          <COLLECTION NAME="Universal Voucher Collection">
            <TYPE>Voucher</TYPE>
            {(string.IsNullOrWhiteSpace(category) ? "" : $"<FILTER>Model3VoucherTypeFilter</FILTER>")}
        <FETCH>DATE</FETCH>
        <FETCH>VOUCHERTYPENAME</FETCH>
        <FETCH>VOUCHERNUMBER</FETCH>
        <FETCH>NARRATION</FETCH>
        <FETCH>PARTYNAME</FETCH>
        <FETCH>ALLLEDGERENTRIES.LIST</FETCH>
        <FETCH>ALLLEDGERENTRIES.LIST.LEDGERNAME</FETCH>
        <FETCH>ALLLEDGERENTRIES.LIST.AMOUNT</FETCH>
        <FETCH>LEDGERENTRIES.LIST</FETCH>
        <FETCH>LEDGERENTRIES.LIST.LEDGERNAME</FETCH>
        <FETCH>LEDGERENTRIES.LIST.AMOUNT</FETCH>
          </COLLECTION>
          {(string.IsNullOrWhiteSpace(category) ? "" : $"<SYSTEM TYPE=\"Formula\" NAME=\"Model3VoucherTypeFilter\">$$IsEqual:$VOUCHERTYPENAME:\"{System.Security.SecurityElement.Escape(category)}\"</SYSTEM>")}
    </TDLMESSAGE></TDL>
  </DESC></BODY>
</ENVELOPE>
""";

static string TallyReportXml(string report, string? requestedFrom = null, string? requestedTo = null)
{
    var today = DateTime.Now;
    var parsedTo = DateTime.TryParseExact(requestedTo, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var requestedDate) ? requestedDate : today;
    var startYear = parsedTo.Month >= 4 ? parsedTo.Year : parsedTo.Year - 1;
    var from = string.IsNullOrWhiteSpace(requestedFrom) ? $"{startYear:0000}0401" : requestedFrom;
    var to = string.IsNullOrWhiteSpace(requestedTo) ? $"{startYear + 1:0000}0331" : requestedTo;
    var escaped = System.Security.SecurityElement.Escape(report) ?? report;
    return $"<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>{escaped}</ID></HEADER><BODY><DESC><STATICVARIABLES><SVFROMDATE TYPE=\"Date\">{from}</SVFROMDATE><SVTODATE TYPE=\"Date\">{to}</SVTODATE><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES></DESC></BODY></ENVELOPE>";
}

static object[] ExtractLedgerRows(XDocument document)
{
    var candidates = document.Descendants().Where(node =>
        node.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase) ||
        node.Name.LocalName.Equals("DSPACCNAME", StringComparison.OrdinalIgnoreCase) ||
        node.Name.LocalName.Equals("ACCOUNT", StringComparison.OrdinalIgnoreCase));
    var rows = candidates.Select(node =>
    {
        var name = ValueDeep(node, "NAME") ?? ValueDeep(node, "LEDGERNAME") ?? ValueDeep(node, "DSPDISPNAME") ?? ValueDeep(node, "DSPACCNAME") ?? "";
        var parent = ValueDeep(node, "PARENT") ?? "";
        var opening = ValueDeep(node, "OPENINGBALANCE") ?? ValueDeep(node, "DSPOPBAL") ?? ValueDeep(node, "DSPOPDRAMT") ?? ValueDeep(node, "DSPOPCRAMT") ?? "";
        var closing = ValueDeep(node, "CLOSINGBALANCE") ?? ValueDeep(node, "DSPCLBAL") ?? ValueDeep(node, "DSPCLDRAMT") ?? ValueDeep(node, "DSPCLCRAMT") ?? "";
        return new { Name = name.Trim(), Parent = parent.Trim(), OpeningBalance = opening.Trim(), ClosingBalance = closing.Trim() };
    })
    .Where(row => row.Name.Length > 0)
    .DistinctBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
    .Cast<object>()
    .ToArray();
    return rows;
}

static object[] ExtractTransactionRows(XDocument document, string? requestedLedger = null)
{
    var candidates = document.Descendants().Where(node =>
        node.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase) ||
        node.Name.LocalName.Equals("DSPVOUCHER", StringComparison.OrdinalIgnoreCase));
    var rows = new List<object>();
    foreach (var node in candidates)
    {
        var date = ValueDeep(node, "DATE") ?? ValueDeep(node, "DSPDATE") ?? "";
        var voucherType = ValueDeep(node, "VOUCHERTYPENAME") ?? ValueDeep(node, "VOUCHERTYPE") ?? "";
        var voucherNumber = ValueDeep(node, "VOUCHERNUMBER") ?? ValueDeep(node, "VOUCHERNO") ?? "";
        var narration = ValueDeep(node, "NARRATION") ?? "";
        var entries = node.Descendants().Where(entry =>
            entry.Name.LocalName.Equals("ALLLEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase) ||
            entry.Name.LocalName.Equals("LEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length > 0)
        {
            var target = entries.FirstOrDefault(entry => string.Equals(ValueDeep(entry, "LEDGERNAME")?.Trim(), requestedLedger?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? entries.FirstOrDefault();
            var targetAmount = ValueDeep(target ?? node, "AMOUNT") ?? ValueDeep(node, "AMOUNT") ?? ValueDeep(node, "DSPCLAMT") ?? ValueDeep(node, "BILLEDAMOUNT") ?? "";
            var particulars = entries.Select(entry => ValueDeep(entry, "LEDGERNAME")?.Trim()).Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, requestedLedger?.Trim(), StringComparison.OrdinalIgnoreCase)).FirstOrDefault() ?? ValueDeep(node, "PARTYLEDGERNAME") ?? ValueDeep(node, "PARTICULARS") ?? "";
            var numeric = ParseTallyAmount(targetAmount);
            rows.Add(new { Date = date, VoucherType = voucherType, VoucherNumber = voucherNumber, Particulars = particulars, Narration = narration, Amount = targetAmount, Debit = numeric < 0 ? Math.Abs(numeric).ToString("0.##") : "", Credit = numeric > 0 ? numeric.ToString("0.##") : "" });
        }
        else
        {
            var amount = ValueDeep(node, "AMOUNT") ?? ValueDeep(node, "DSPCLAMT") ?? ValueDeep(node, "BILLEDAMOUNT") ?? "";
            rows.Add(new { Date = date, VoucherType = voucherType, VoucherNumber = voucherNumber, Particulars = ValueDeep(node, "PARTYLEDGERNAME") ?? ValueDeep(node, "LEDGERNAME") ?? ValueDeep(node, "PARTICULARS") ?? "", Narration = narration, Amount = amount, Debit = "", Credit = "" });
        }
    }
    return rows.Where(row => row.ToString()?.Length > 0).ToArray();
}

static decimal ParseTallyAmount(string value)
{
    var cleaned = System.Text.RegularExpressions.Regex.Replace(value ?? "", "[^0-9.\\-]", "");
    return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amount) ? amount : 0m;
}

static object[] ExtractLedgerRowsMatching(XDocument document, string requestedLedger)
{
    var requested = requestedLedger.Trim();
    var candidates = document.Descendants().Where(node =>
        node.Name.LocalName.Equals("LEDGER", StringComparison.OrdinalIgnoreCase) ||
        node.Name.LocalName.Equals("DSPACCNAME", StringComparison.OrdinalIgnoreCase) ||
        node.Name.LocalName.Equals("ACCOUNT", StringComparison.OrdinalIgnoreCase));
    return candidates.Select(node =>
        {
            var name = (ValueDeep(node, "NAME") ?? ValueDeep(node, "LEDGERNAME") ?? ValueDeep(node, "DSPDISPNAME") ?? ValueDeep(node, "DSPACCNAME") ?? "").Trim();
            var parent = (ValueDeep(node, "PARENT") ?? "").Trim();
            var opening = (ValueDeep(node, "OPENINGBALANCE") ?? ValueDeep(node, "DSPOPBAL") ?? ValueDeep(node, "DSPOPDRAMT") ?? ValueDeep(node, "DSPOPCRAMT") ?? "").Trim();
            var closing = (ValueDeep(node, "CLOSINGBALANCE") ?? ValueDeep(node, "DSPCLBAL") ?? ValueDeep(node, "DSPCLDRAMT") ?? ValueDeep(node, "DSPCLCRAMT") ?? "").Trim();
            return new { Name = name, Parent = parent, OpeningBalance = opening, ClosingBalance = closing };
        })
        .Where(row => row.Name.Length > 0 && string.Equals(row.Name, requested, StringComparison.OrdinalIgnoreCase))
        .DistinctBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
        .Cast<object>()
        .ToArray();
}

static object[] ExtractLedgerNamesLoosely(string body)
{
    var cleaned = SanitizeXml(body);
    var matches = System.Text.RegularExpressions.Regex.Matches(cleaned, "<NAME(?:\\s[^>]*)?>(.*?)</NAME>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    return matches
        .Select(match => WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, "<[^>]+>", "")).Trim())
        .Where(name => name.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(name => (object)new { Name = name, Parent = "", OpeningBalance = "", ClosingBalance = "" })
        .ToArray();
}

static string DetectTallyStatus(string body)
{
    var match = System.Text.RegularExpressions.Regex.Match(body, "<STATUS>\\s*([^<]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value.Trim() : "not-returned";
}

async Task<object> GenerateGeminiAnswerAsync(AiAnswerRequest request)
{
    if (string.IsNullOrWhiteSpace(request.ApiKey))
        return new { configured = false, errorType = "missing_key", error = "Enter a Google AI Studio Gemini API key in Tally Chat." };

    var prompt = $"Question: {request.Question}\nIntent: {request.Intent}\nLedger: {request.LedgerName}\nDate range: {request.FromDate} to {request.ToDate}\nRequested format: {request.RequestedFormat}\nRetrieved TallyPrime data (use only this for accounting facts): {request.Data.GetRawText()}\n\nReturn JSON with exactly these keys: answer (string), title (string), insights (array of short strings). Answer naturally like a helpful finance copilot. Never invent facts, and say when the retrieved data is insufficient. For greetings or identity questions, answer naturally from your role. Use Indian accounting language and ₹ where appropriate.";
    var payload = new
    {
        systemInstruction = new { parts = new[] { new { text = "You are Tally Assistant, a precise and friendly TallyPrime finance copilot. You were created by Sangram Rodge. The current assistant product version is 2.3. This request is local-only. Never claim that Tally was changed." } } },
        contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
        generationConfig = new { temperature = 0.2, maxOutputTokens = 4096, responseMimeType = "application/json" },
    };
    var payloadJson = JsonSerializer.Serialize(payload);
    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(geminiModel)}:generateContent";

    const int maxAttempts = 3;
    var responseBody = "";
    var status = 0;
    var timedOut = false;
    string? networkError = null;
    var succeeded = false;
    var attempts = 0;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        attempts = attempt;
        timedOut = false;
        networkError = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
            };
            message.Headers.Add("x-goog-api-key", request.ApiKey.Trim()); // header, so the key never appears in a URL
            using var response = await geminiClient.SendAsync(message, timeout.Token);
            status = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode) { succeeded = true; break; }
            var retryable = status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
            if (!retryable || attempt == maxAttempts) break;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            if (attempt == maxAttempts) break;
        }
        catch (HttpRequestException ex)
        {
            networkError = ex.Message;
            if (attempt == maxAttempts) break;
        }
        await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
    }

    if (!succeeded)
    {
        var detail = "";
        try { using var errorDocument = JsonDocument.Parse(responseBody); detail = errorDocument.RootElement.GetProperty("error").GetProperty("message").GetString() ?? ""; } catch { }
        string errorType;
        string message;
        if (timedOut) { errorType = "timeout"; message = $"Gemini did not answer within 90 seconds (tried {attempts} time(s)). Try again, or ask a narrower question."; }
        else if (networkError is not null) { errorType = "network"; message = $"Could not reach Gemini: {networkError}"; }
        else if (status == 401 || status == 403 || detail.Contains("API key", StringComparison.OrdinalIgnoreCase)) { errorType = "invalid_key"; message = "Gemini rejected the API key. Check the key in Google AI Studio."; }
        else if (status == 429) { errorType = "quota"; message = "Gemini quota or rate limit reached (429). Wait a minute or check your AI Studio quota."; }
        else if (status == 404) { errorType = "model_not_found"; message = $"Gemini model '{geminiModel}' was not found. Set the GEMINI_MODEL environment variable to a model your key can use."; }
        else if (status == 400) { errorType = "bad_request"; message = $"Gemini rejected the request (400){(detail.Length > 0 ? ": " + detail : "")}."; }
        else if (status >= 500) { errorType = "server_unavailable"; message = $"Gemini is temporarily unavailable ({status}) after {attempts} attempt(s)."; }
        else { errorType = "unknown"; message = $"Gemini request failed ({status}){(detail.Length > 0 ? ": " + detail : "")}."; }
        return new { configured = false, errorType, httpStatus = status, attempts, error = message };
    }

    try
    {
        using var document = JsonDocument.Parse(responseBody);
        var text = document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
        try
        {
            using var answerDocument = JsonDocument.Parse(text);
            var root = answerDocument.RootElement;
            var insightsValue = root.TryGetProperty("insights", out var insights) ? insights.Clone() : JsonDocument.Parse("[]").RootElement.Clone();
            return new { configured = true, answer = root.TryGetProperty("answer", out var answer) ? answer.GetString() : text, title = root.TryGetProperty("title", out var title) ? title.GetString() : "Tally result", insights = insightsValue };
        }
        catch
        {
            return new { configured = true, answer = text, title = "Tally result", insights = Array.Empty<string>() };
        }
    }
    catch (Exception ex)
    {
        return new { configured = false, errorType = "bad_response", error = $"Gemini returned a response that could not be read: {ex.Message}" };
    }
}

static string[] LedgerWords(string value) => value.ToLowerInvariant().Split(new[] { ' ', '_', '-', '/', '.', ',', ':', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length > 1 && word is not "the" and not "of" and not "what" and not "balance" and not "ledger" and not "give" and not "show" and not "account" and not "me").ToArray();

static string? FindFuzzyLedger(string question, IReadOnlyList<string> ledgers)
{
    var questionWords = LedgerWords(question).ToHashSet();
    if (questionWords.Count == 0) return null;
    var scored = ledgers.Select(ledger =>
    {
        var words = LedgerWords(ledger);
        var overlap = words.Length == 0 ? 0d : words.Count(word => questionWords.Contains(word)) / (double)words.Length;
        var meaningful = words.Where(word => word.Length >= 3).Count(word => questionWords.Any(item => item == word || (item.Length >= 4 && word.Length >= 4 && (item.Contains(word) || word.Contains(item)))));
        return new { ledger, score = overlap + Math.Min(meaningful, 2) * 0.12, overlap };
    }).OrderByDescending(item => item.score).ToArray();
    var best = scored.FirstOrDefault();
    var second = scored.Skip(1).FirstOrDefault();
    if (best is null || best.overlap < 0.5 || best.score < 0.62) return null;
    if (second is not null && best.score - second.score < 0.08) return null;
    return best.ledger;
}

async Task<object> GenerateGeminiPlanAsync(AiPlanRequest request)
{
    if (string.IsNullOrWhiteSpace(request.ApiKey))
        return new { configured = false, errorType = "missing_key", error = "Enter a Google AI Studio Gemini API key in Tally Chat." };

    var catalog = request.AvailableLedgers.Count == 0 ? "No ledger catalog supplied." : string.Join("\n", request.AvailableLedgers.Take(250));
    var prompt = $"User request: {request.Question}\nAvailable exact TallyPrime ledger names:\n{catalog}\n\nYou are the planning layer between an auditor and TallyPrime. Convert the request into a safe read-only plan. Choose an exact ledger only from the catalog and only when that ledger name is explicitly present in the user's request (case-insensitive). Never select the first or most likely ledger for a broad question. For a broad question such as 'what is my bank balance', choose intent cash_position and operation report with ledgerName empty; this means total all synchronized bank ledgers, not an arbitrary ledger, and does not need clarification. For sales or purchase totals, choose tally_report with report sales-register or purchase-register and ledgerName empty; do not convert those questions into ledger_balance. Convert dates to YYYYMMDD. intent must be one of ledger_transactions, ledger_balance, trial_balance, cash_position, financial_ratios, tally_report, ledger_list, greeting, clarification. operation must be one of ledger_entries, ledger_balance, trial_balance, report, ledger_directory, clarification, conversation. For tally_report, report must be one of receivables, payables, profit-loss, balance-sheet, day-book, sales-register, purchase-register. requestedColumns must be an array of the columns the user asked to see. requestedFormat describes the requested output shape. If the request is ambiguous or asks for unsupported data, set needsClarification true and ask one precise question. Never invent ledger names or numbers. Return JSON only with exactly: intent, operation, ledgerName, fromDate, toDate, report, requestedColumns, requestedFormat, confidence, needsClarification, clarificationQuestion, tallyOperation.";
    var payload = new
    {
        systemInstruction = new { parts = new[] { new { text = "You are a precise TallyPrime query planner. Output machine-readable JSON only. Do not answer accounting questions and do not fabricate data." } } },
        contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
        generationConfig = new { temperature = 0.0, maxOutputTokens = 1200, responseMimeType = "application/json" },
    };
    var payloadJson = JsonSerializer.Serialize(payload);
    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(geminiModel)}:generateContent";
    var responseBody = "";
    var status = 0;
    var attempts = 0;
    for (var attempt = 1; attempt <= 3; attempt++)
    {
        attempts = attempt;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payloadJson, Encoding.UTF8, "application/json") };
            message.Headers.Add("x-goog-api-key", request.ApiKey.Trim());
            using var response = await geminiClient.SendAsync(message, timeout.Token);
            status = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(responseBody);
                var text = document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
                using var plan = JsonDocument.Parse(text);
                var root = plan.RootElement;
                var rawIntent = root.TryGetProperty("intent", out var intent) ? intent.GetString() ?? "clarification" : "clarification";
                var rawOperation = root.TryGetProperty("operation", out var operation) ? operation.GetString() ?? "clarification" : "clarification";
                var rawLedgerName = root.TryGetProperty("ledgerName", out var ledgerName) ? ledgerName.GetString() ?? "" : "";
                var rawReport = root.TryGetProperty("report", out var report) ? report.GetString() ?? "" : "";
                var exactLedgerMentioned = request.AvailableLedgers.FirstOrDefault(name => request.Question.Contains(name, StringComparison.OrdinalIgnoreCase));
                var fuzzyLedgerMentioned = exactLedgerMentioned ?? FindFuzzyLedger(request.Question, request.AvailableLedgers);
                var questionLower = request.Question.ToLowerInvariant();
                var broadBankQuestion = string.IsNullOrWhiteSpace(fuzzyLedgerMentioned)
                    && (questionLower.Contains("bank") || questionLower.Contains("cash"))
                    && (questionLower.Contains("balance") || questionLower.Contains("position") || questionLower.Contains("total"));
                var broadSalesQuestion = string.IsNullOrWhiteSpace(fuzzyLedgerMentioned)
                    && (questionLower.Contains("sale") || questionLower.Contains("sales"))
                    && (questionLower.Contains("total") || questionLower.Contains("summary") || questionLower.Contains("report"));
                var broadPurchaseQuestion = string.IsNullOrWhiteSpace(fuzzyLedgerMentioned)
                    && questionLower.Contains("purchase")
                    && (questionLower.Contains("total") || questionLower.Contains("summary") || questionLower.Contains("report"));
                var normalizedIntent = broadBankQuestion ? "cash_position" : broadSalesQuestion || broadPurchaseQuestion ? "tally_report" : rawIntent;
                var normalizedOperation = broadBankQuestion || broadSalesQuestion || broadPurchaseQuestion ? "report" : rawOperation;
                var normalizedLedger = broadBankQuestion || broadSalesQuestion || broadPurchaseQuestion ? "" : (string.IsNullOrWhiteSpace(fuzzyLedgerMentioned) ? rawLedgerName : fuzzyLedgerMentioned);
                var normalizedReport = broadBankQuestion ? "" : broadSalesQuestion ? "sales-register" : broadPurchaseQuestion ? "purchase-register" : rawReport;
                return new
                {
                    configured = true,
                    intent = normalizedIntent,
                    operation = normalizedOperation,
                    ledgerName = normalizedLedger,
                    fromDate = root.TryGetProperty("fromDate", out var fromDate) ? fromDate.GetString() : "",
                    toDate = root.TryGetProperty("toDate", out var toDate) ? toDate.GetString() : "",
                    report = normalizedReport,
                    requestedColumns = root.TryGetProperty("requestedColumns", out var requestedColumns) && requestedColumns.ValueKind == JsonValueKind.Array
                        ? requestedColumns.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).Take(20).ToArray()
                        : Array.Empty<string>(),
                    requestedFormat = root.TryGetProperty("requestedFormat", out var requestedFormat) ? requestedFormat.GetString() : "conversation",
                    confidence = root.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                    needsClarification = root.TryGetProperty("needsClarification", out var needsClarification) && needsClarification.GetBoolean(),
                    clarificationQuestion = root.TryGetProperty("clarificationQuestion", out var clarificationQuestion) ? clarificationQuestion.GetString() : "",
                    tallyOperation = root.TryGetProperty("tallyOperation", out var tallyOperation) ? tallyOperation.GetString() : "read-only query",
                };
            }
            if (status is not (429 or 500 or 502 or 503 or 504)) break;
        }
        catch (OperationCanceledException) when (attempt < 3) { }
        catch (HttpRequestException) when (attempt < 3) { }
        if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
    }
    var errorType = status == 429 ? "quota" : status is 401 or 403 ? "invalid_key" : status >= 500 ? "server_unavailable" : "planner_failed";
    return new { configured = false, errorType, httpStatus = status, attempts, error = status >= 500 ? $"Gemini planner unavailable ({status}) after {attempts} attempt(s)." : $"Gemini planner failed ({status})." };
}

async Task<int> RunCliAsync(string[] cliArgs)
{
    var mode = cliArgs[0].ToLowerInvariant();
    if (mode == "--selftest") return SelfTest.Run();

    if (mode == "--parse")
    {
        if (cliArgs.Length < 3)
        {
            Console.WriteLine("Usage: UniversalTallyConnector.exe --parse <file.xml> \"<ledger name>\" [openingBalanceDrPositive]");
            return 2;
        }
        var xml = await File.ReadAllTextAsync(cliArgs[1]);
        decimal? opening = null;
        if (cliArgs.Length > 3 && decimal.TryParse(cliArgs[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedOpening))
            opening = parsedOpening;
        var parsed = TallyLedgerParser.Parse(XDocument.Parse(SanitizeXml(xml)), cliArgs[2]);
        StatementPrinter.Print(TallyLedgerParser.BuildStatement(cliArgs[2], parsed, opening, null));
        return 0;
    }

    if (mode == "--capture")
    {
        if (cliArgs.Length < 2)
        {
            Console.WriteLine("Usage: UniversalTallyConnector.exe --capture \"<ledger name>\" [fromYYYYMMDD] [toYYYYMMDD]");
            return 2;
        }
        var ledger = cliArgs[1];
        var (from, to) = ResolveRange(cliArgs.Length > 2 ? cliArgs[2] : null, cliArgs.Length > 3 ? cliArgs[3] : null);
        var folder = Path.Combine(Directory.GetCurrentDirectory(), $"tally-capture-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);
        Console.WriteLine($"Ledger '{ledger}', {from} to {to}. Tally port {tallyPort}. Saving raw Tally responses to: {folder}");
        var requests = new (string File, string Xml)[]
        {
            ("voucher-collection.xml", VoucherCollectionXml(from, to)),
            ("ledger-report.xml", LedgerEntriesXml(ledger, from, to)),
            ("daybook.xml", TallyReportXml("Day Book", from, to)),
            ("ledger-balance.xml", LedgerBalanceXml(ledger, from, to)),
        };
        foreach (var (file, xml) in requests)
        {
            try
            {
                var body = await PostTallyAsync(xml, Encoding.UTF8, "application/xml");
                await File.WriteAllTextAsync(Path.Combine(folder, file), body);
                Console.WriteLine($"  saved {file}: {body.Length:N0} characters");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {file} FAILED: {ex.Message}");
            }
        }
        var run = await BuildLedgerStatementAsync(ledger, from, to, null);
        Console.WriteLine($"Strategy that produced rows: {(run.Strategy.Length > 0 ? run.Strategy : "none")}");
        StatementPrinter.Print(run.Statement);
        Console.WriteLine();
        Console.WriteLine("The saved .xml files are raw Tally output and contain your company's data. Redact before sharing if needed.");
        return run.TallyResponded ? 0 : 1;
    }

    Console.WriteLine("Unknown option. Use --selftest, --capture \"<ledger>\" [from] [to], or --parse <file.xml> \"<ledger>\" [opening].");
    return 2;
}

static async Task JsonAsync(HttpListenerContext context, int status, object value)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json; charset=utf-8";
    context.Response.ContentLength64 = bytes.Length;
    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}

static List<(string From, string To)> DateWindows(string from, string to)
{
    if (!DateTime.TryParseExact(from, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start) ||
        !DateTime.TryParseExact(to, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var end) || start > end)
        return new List<(string From, string To)> { (from, to) };
    var windows = new List<(string From, string To)>();
    var cursor = start;
    while (cursor <= end)
    {
        var windowEnd = cursor.AddMonths(3).AddDays(-1);
        if (windowEnd > end) windowEnd = end;
        windows.Add((cursor.ToString("yyyyMMdd"), windowEnd.ToString("yyyyMMdd")));
        cursor = windowEnd.AddDays(1);
    }
    return windows;
}

record XmlProfile(string MediaType, Encoding Encoding, string Payload);
record LedgerWriteRequest(string Action, string Name, string? Parent, string? OpeningBalance, bool Confirm = false);
record ImportRequest(string Xml, bool Confirm = false);
record AiAnswerRequest(string Question, string Intent, string LedgerName, string FromDate, string ToDate, string RequestedFormat, double Confidence, JsonElement Data, string ApiKey);
record AiPlanRequest(string Question, List<string> AvailableLedgers, string ApiKey);
record LedgerBatchRequest(string[] Ledgers, string? From = null, string? To = null);
record SyncRequest(string Company, string From = "", string To = "", string Mode = "period");

sealed class SyncProgressState
{
    private readonly object gate = new();
    private string status = "idle";
    private string stage = "idle";
    private string detail = "";
    private string company = "";
    private int vouchers;
    private int entries;
    private DateTimeOffset startedAt;
    private DateTimeOffset updatedAt;

    public void Start(string requestedCompany, string mode)
    {
        lock (gate)
        {
            status = "running";
            stage = "company-discovery";
            detail = $"Starting {mode} synchronization";
            company = requestedCompany;
            vouchers = 0;
            entries = 0;
            startedAt = updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Update(string nextStage, string nextDetail, int? voucherCount = null, int? entryCount = null)
    {
        lock (gate)
        {
            stage = nextStage;
            detail = nextDetail;
            if (voucherCount is not null) vouchers = voucherCount.Value;
            if (entryCount is not null) entries = entryCount.Value;
            updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Complete(string completionDetail)
    {
        lock (gate)
        {
            status = "complete";
            stage = "complete";
            detail = completionDetail;
            updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(string error)
    {
        lock (gate)
        {
            status = "error";
            stage = "error";
            detail = error;
            updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            return new { status, stage, detail, company, vouchers, entries, startedAt, updatedAt, elapsedSeconds = startedAt == default ? 0 : (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds };
        }
    }
}
