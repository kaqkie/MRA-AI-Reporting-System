using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MraReporting.Data;
using MraReporting.Infrastructure;

namespace MraReporting.Ai;

/// <summary>Thrown when the model passes an argument that cannot be used; the message goes back to the model.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>
/// The functions the model may call. Each one validates its arguments, runs one query from
/// <see cref="EisQueries"/>, hands the full result to the browser and returns a short text
/// summary to the model. Descriptions are written for a small model: short and concrete.
/// </summary>
public sealed class ReportingTools
{
    private readonly EisQueries _queries;
    private readonly StationDirectory _stations;
    private readonly OfficeDirectory _offices;
    private readonly RequestContext _context;
    private readonly AppClock _clock;
    private readonly AppOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;

    public ReportingTools(EisQueries queries, StationDirectory stations, OfficeDirectory offices, RequestContext context, AppClock clock, AppOptions options, ILogger logger, CancellationToken ct)
    {
        _queries = queries;
        _stations = stations;
        _offices = offices;
        _context = context;
        _clock = clock;
        _options = options;
        _logger = logger;
        _ct = ct;
    }

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(CreateSummaryReport),
        AIFunctionFactory.Create(GetRegisterCounts),
        AIFunctionFactory.Create(GetDataCoverage),
        AIFunctionFactory.Create(GetStationCodes),
        AIFunctionFactory.Create(GetCustomsCollections),
        AIFunctionFactory.Create(GetSalesByTaxOffice),
        AIFunctionFactory.Create(GetSalesTotals),
        AIFunctionFactory.Create(GetVatByTaxRate),
        AIFunctionFactory.Create(GetTopTaxpayers),
        AIFunctionFactory.Create(GetTaxpayerProfile),
        AIFunctionFactory.Create(GetTaxpayerTerminals),
        AIFunctionFactory.Create(GetFailedTransactions),
        AIFunctionFactory.Create(GetRedFlags),
        AIFunctionFactory.Create(GetVoidRequests),
        AIFunctionFactory.Create(GetRecalledInvoices),
        AIFunctionFactory.Create(GetTamperedTerminals),
    ];

    // ---------------------------------------------------------------- tools

    [Description("Prepares a downloadable summary report (PDF and Excel) for one day or a period: headline figures, trends, VAT by rate, tax offices, top taxpayers, customs payments, flags and failed submissions. Use when the user asks for a report, e.g. 'report for January 2026' or 'give me last week's report'. For one day, use the same date for both.")]
    public Task<string> CreateSummaryReport(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate)
    {
        _logger.LogInformation("[TOOL] CreateSummaryReport called with {From} to {To}", fromDate, toDate);
        var args = JsonSerializer.Serialize(new { fromDate, toDate });
        try
        {
            var (from, to) = DateRange(fromDate, toDate);
            var query = $"from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            var label = from == to
                ? $"Daily summary report, {from:d MMMM yyyy}"
                : $"Summary report, {from:d MMMM yyyy} to {to:d MMMM yyyy}";
            _context.PendingReports.Enqueue(new ReportLink(label,
                $"/api/reports/summary?{query}&format=pdf", $"/api/reports/summary?{query}&format=xlsx",
                $"/api/reports/summary?{query}&format=docx"));
            _context.Record(new ToolCallRecord(nameof(CreateSummaryReport), args, 0, 0, null));
            return Task.FromResult($"Download buttons for the {label.ToLowerInvariant()} are now shown to the user. " +
                "Tell the user the report is ready to download as PDF, Word or Excel, and that it can take a minute or two to build. Do not describe its contents.");
        }
        catch (ToolArgumentException ex)
        {
            _context.Record(new ToolCallRecord(nameof(CreateSummaryReport), args, 0, 0, ex.Message));
            return Task.FromResult($"The report was not prepared: {ex.Message}");
        }
    }

    [Description("Counts of what is registered in EIS: taxpayers, VAT-registered taxpayers, taxpayer business sites, fiscal terminals, tax office codes and customs station codes. National totals only. Use for 'how many taxpayers / sites / terminals / stations / offices are there' in all of Malawi. For one taxpayer's terminals, use the taxpayer terminals lookup instead.")]
    public Task<string> GetRegisterCounts()
        => Run(nameof(GetRegisterCounts), new { }, () => _queries.RegisterCountsAsync(_ct));

    [Description("Lists every station code in EIS: tax office codes (with how many taxpayers are registered at each) and customs declaration station codes (with how many import declarations), side by side. Use for 'list the stations', 'which stations or offices are there', 'show the station codes'.")]
    public Task<string> GetStationCodes()
        => Run(nameof(GetStationCodes), new { }, () => _queries.StationCodesAsync(_ct));

    [Description("Customs payments (ePayment) at customs stations, border posts and airports such as Songwe Border, Mwanza Border, Mchinji Border, Dedza Border, Kamuzu International Airport, Chileka Airport or Blantyre Port, for a date range. Use for 'how much did Songwe collect', 'customs collections by station', 'compare border posts'.")]
    public Task<string> GetCustomsCollections(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("Station name or code, e.g. 'Songwe' or 'SWE'. Leave empty for all stations.")] string station = "",
        [Description("'station', 'day' or 'month'")] string groupBy = "station")
        => Run(nameof(GetCustomsCollections), new { fromDate, toDate, station, groupBy }, async () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            var grouping = ParseEnum<CustomsGrouping>(groupBy, nameof(groupBy));
            IReadOnlyList<string> codes = [];
            if (!string.IsNullOrWhiteSpace(station))
            {
                codes = await _stations.ResolveCustomsAsync(station, _ct);
                if (codes.Count == 0)
                {
                    // A town without a customs station may still have domestic tax offices (e.g. Zomba).
                    var offices = OfficesMatching(station);
                    if (offices.Count > 0)
                        throw new ToolArgumentException($"'{station}' is not a customs station, but it has domestic tax offices: " +
                            string.Join(", ", offices.Select(c => _offices.Label(c))) +
                            $". For sales or VAT there, use the sales-by-tax-office lookup with taxOffice '{station}'.");
                    var known = await _stations.CustomsNamesAsync(_ct);
                    throw new ToolArgumentException($"No customs station matches '{station}'. Known stations: " +
                        string.Join(", ", known.Select(kv => $"{kv.Value} ({kv.Key})")) + ". Ask the user which one they mean.");
                }
            }
            return await _queries.CustomsCollectionsAsync(from, to, codes, grouping, _ct);
        }, coverageKey: "customs");

    [Description("Domestic sales and VAT grouped by the tax office where each seller is registered, for a date range. Use for 'VAT by tax office', 'which office collected the most', and for sales or VAT in a town (e.g. 'VAT in Zomba'), passing the town name or an office code such as LSTO or ZMTO.")]
    public Task<string> GetSalesByTaxOffice(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("Tax office code (e.g. 'LSTO'), its official name, or a town name (e.g. 'Zomba'). Leave empty for all offices.")] string taxOffice = "")
        => Run(nameof(GetSalesByTaxOffice), new { fromDate, toDate, taxOffice }, async () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            string? office = null;
            if (!string.IsNullOrWhiteSpace(taxOffice))
            {
                var text = taxOffice.Trim();
                // Accept an official name or a town from the office names list as well as a code.
                var byName = OfficesMatching(text);
                if (byName.Count == 1) office = byName[0];
                else if (byName.Count > 1)
                {
                    // A town with several offices (Zomba: ZOM and ZMTO): show just those offices.
                    var all = await _queries.SalesByTaxOfficeAsync(from, to, null, _ct);
                    var labels = byName.Select(c => _offices.Label(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var rows = all.Rows.Where(r => r.Length > 0 && r[0] is string l && labels.Contains(l)).ToList();
                    return all with
                    {
                        Title = all.Title.Replace("by seller's tax office", $"for tax offices in {text}", StringComparison.Ordinal),
                        Rows = rows,
                        Note = (all.Note + $" Offices included: {string.Join(", ", labels)}.").Trim()
                    };
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(text.ToUpperInvariant(), "^[A-Z0-9_.]{2,10}$")) office = text.ToUpperInvariant();
                else
                {
                    var known = _offices.Names.Count == 0
                        ? "No office names are on file yet, so use a code such as LSTO or BMTO."
                        : "Known offices: " + string.Join(", ", _offices.Names.Select(kv => $"{kv.Value} ({kv.Key})")) + ".";
                    throw new ToolArgumentException($"'{taxOffice}' does not match one tax office. {known} Ask the user which office they mean.");
                }
            }
            return await _queries.SalesByTaxOfficeAsync(from, to, office, _ct);
        }, coverageKey: "invoices");

    /// <summary>Tax office codes whose code equals, or whose official name contains, the text (e.g. "Zomba" gives ZOM and ZMTO).</summary>
    private List<string> OfficesMatching(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        var exact = _offices.Names.Keys.Where(k => k.TrimEnd('.').Equals(text.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return _offices.Names.Where(kv => kv.Value.Contains(text, StringComparison.OrdinalIgnoreCase)
                                          || text.Contains(kv.Value, StringComparison.OrdinalIgnoreCase))
                             .Select(kv => kv.Key).ToList();
    }

    [Description("The first and last dates of data available in EIS for invoices, failed submissions, flagged transactions and void requests. Use for 'what period does the data cover', 'what is the latest data'.")]
    public Task<string> GetDataCoverage()
        => Run(nameof(GetDataCoverage), new { }, () => _queries.AllCoverageAsync(_ct));

    [Description("NATIONAL totals for all of Malawi: gross sales, VAT and invoice counts for a date range, per day, per month or as one total. Never use for a single station, tax office, border post or town. Use for 'how much VAT was collected', 'total sales last month', 'sales trend this week'.")]
    public Task<string> GetSalesTotals(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("'day', 'month' or 'total'")] string period = "day")
        => Run(nameof(GetSalesTotals), new { fromDate, toDate, period }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.SalesTotalsAsync(from, to, ParseEnum<Period>(period, nameof(period)), _ct);
        }, coverageKey: "invoices");

    [Description("NATIONAL VAT for all of Malawi split by tax rate code, for a date range. Never use for a single station, tax office, border post or town. Use for 'VAT by rate'.")]
    public Task<string> GetVatByTaxRate(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate)
        => Run(nameof(GetVatByTaxRate), new { fromDate, toDate }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.VatByRateAsync(from, to, _ct);
        }, coverageKey: "invoices");

    [Description("Largest taxpayers (sellers) in all of Malawi ranked by gross sales or by VAT for a date range.")]
    public Task<string> GetTopTaxpayers(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("How many taxpayers, 1 to 50")] int top = 10,
        [Description("'sales' or 'vat'")] string rankBy = "sales")
        => Run(nameof(GetTopTaxpayers), new { fromDate, toDate, top, rankBy }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.TopTaxpayersAsync(from, to, Clamp(top, 1, 50), ParseEnum<TaxpayerMetric>(rankBy, nameof(rankBy)), _ct);
        }, coverageKey: "invoices");

    [Description("Looks up one taxpayer by TIN in the EIS register: business name, tax office, VAT registration, blacklist status, active sites. Use for 'is TIN X registered/onboarded in EIS' or 'who is TIN X'.")]
    public Task<string> GetTaxpayerProfile(
        [Description("Taxpayer Identification Number, digits only")] string tin)
        => Run(nameof(GetTaxpayerProfile), new { tin }, () =>
        {
            var clean = new string((tin ?? "").Where(char.IsDigit).ToArray());
            if (clean.Length is < 5 or > 30)
                throw new ToolArgumentException("A TIN must be 5 to 30 digits. Ask the user for the TIN.");
            return _queries.TaxpayerProfileAsync(clean, _ct);
        });

    [Description("How many fiscal terminals (POS devices) one taxpayer used: finds the taxpayer by business name (e.g. 'Portland Cement') or TIN and counts the distinct terminals that issued invoices in a date range, with invoice count and active business sites. Use for 'how many terminals does X have', 'terminals for X', 'devices used by TIN X'. If the user gives no period, use the last 30 days.")]
    public Task<string> GetTaxpayerTerminals(
        [Description("Business name or part of it, or the TIN")] string taxpayer,
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate)
        => Run(nameof(GetTaxpayerTerminals), new { taxpayer, fromDate, toDate }, async () =>
        {
            var text = (taxpayer ?? "").Trim();
            if (text.Length < 3 || text.Length > 100)
                throw new ToolArgumentException("Give a business name (at least 3 letters) or a TIN. Ask the user which taxpayer they mean.");
            var (from, to) = DateRange(fromDate, toDate);
            var result = await _queries.TaxpayerTerminalsAsync(text, from, to, _ct);
            if (result.Rows.Count == 0)
                throw new ToolArgumentException($"No registered taxpayer matches '{text}'. Tell the user, and ask them to check the spelling or give the TIN.");
            return result;
        }, coverageKey: "invoices");

    [Description("Invoice submissions that failed, for a date range, grouped by taxpayer, by terminal or by day.")]
    public Task<string> GetFailedTransactions(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("'taxpayer', 'terminal' or 'day'")] string groupBy = "taxpayer",
        [Description("How many taxpayers or terminals, 1 to 50")] int top = 10)
        => Run(nameof(GetFailedTransactions), new { fromDate, toDate, groupBy, top }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.FailedTransactionsAsync(from, to, ParseEnum<FailureGrouping>(groupBy, nameof(groupBy)), Clamp(top, 1, 50), _ct);
        }, coverageKey: "failed");

    [Description("Transactions flagged by EIS risk rules for a date range, grouped by flag type, by investigation status or by day.")]
    public Task<string> GetRedFlags(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("'type', 'status' or 'day'")] string groupBy = "type")
        => Run(nameof(GetRedFlags), new { fromDate, toDate, groupBy }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.RedFlagsAsync(from, to, ParseEnum<FlagGrouping>(groupBy, nameof(groupBy)), _ct);
        }, coverageKey: "flags");

    [Description("Requests to void receipts for a date range, counted by status.")]
    public Task<string> GetVoidRequests(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate)
        => Run(nameof(GetVoidRequests), new { fromDate, toDate }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.VoidRequestsAsync(from, to, _ct);
        }, coverageKey: "voids");

    [Description("Recalled (cancelled) invoices for a date range, grouped by recall type or by day.")]
    public Task<string> GetRecalledInvoices(
        [Description("Start date, yyyy-MM-dd")] string fromDate,
        [Description("End date (inclusive), yyyy-MM-dd")] string toDate,
        [Description("'type' or 'day'")] string groupBy = "type")
        => Run(nameof(GetRecalledInvoices), new { fromDate, toDate, groupBy }, () =>
        {
            var (from, to) = DateRange(fromDate, toDate);
            return _queries.RecalledInvoicesAsync(from, to, ParseEnum<RecallGrouping>(groupBy, nameof(groupBy)), _ct);
        }, coverageKey: "invoices");

    [Description("Fiscal terminals (POS devices) that recorded tamper attempts, most recent first.")]
    public Task<string> GetTamperedTerminals(
        [Description("How many terminals, 1 to 100")] int top = 20)
        => Run(nameof(GetTamperedTerminals), new { top }, () => _queries.TamperedTerminalsAsync(Clamp(top, 1, 100), _ct));

    // ---------------------------------------------------------------- plumbing

    private async Task<string> Run(string tool, object args, Func<Task<QueryResult>> query, string? coverageKey = null)
    {
        var watch = Stopwatch.StartNew();
        var argsJson = JsonSerializer.Serialize(args);
        _logger.LogInformation("[TOOL] {Tool} called with {Args}", tool, argsJson);

        try
        {
            var result = await query();

            // An empty result usually means the period is outside the data, so say which dates exist.
            if (coverageKey is not null && IsEmpty(result))
            {
                try
                {
                    var coverage = await _queries.CoverageSentenceAsync(coverageKey, _ct);
                    result = result with { Note = string.Join(" ", new[] { result.Note, "No data for this period.", coverage }.Where(s => !string.IsNullOrWhiteSpace(s))) };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[TOOL] Could not read the data date range");
                }
            }

            try
            {
                result = result with { Insights = ResultInsights.For(result) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[TOOL] Could not work out key facts for {Tool}", tool);
            }

            _context.AddResult(result);
            _context.Record(new ToolCallRecord(tool, argsJson, result.Rows.Count, watch.ElapsedMilliseconds, null));
            _logger.LogInformation("[TOOL] {Tool} returned {Rows} rows in {Ms} ms", tool, result.Rows.Count, watch.ElapsedMilliseconds);
            return result.ToModelSummary();
        }
        catch (ToolArgumentException ex)
        {
            _context.Record(new ToolCallRecord(tool, argsJson, 0, watch.ElapsedMilliseconds, ex.Message));
            _logger.LogWarning("[TOOL] {Tool} rejected arguments: {Message}", tool, ex.Message);
            return $"The query was not run: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            throw; // the user closed the page or the request timed out
        }
        catch (Exception ex)
        {
            _context.Record(new ToolCallRecord(tool, argsJson, 0, watch.ElapsedMilliseconds, ex.Message));
            _logger.LogError(ex, "[TOOL] {Tool} failed", tool);
            return "The database query failed, so no figures are available. Tell the user the data could not be retrieved right now. Do not guess any numbers.";
        }
    }

    /// <summary>No rows, or a single totals row whose invoice count is zero.</summary>
    private static bool IsEmpty(QueryResult r)
    {
        if (r.Rows.Count == 0) return true;
        if (r.Rows.Count > 1) return false;
        var i = r.Columns.ToList().FindIndex(c => c == "Invoices");
        return i >= 0 && (r.Rows[0][i] is null || Convert.ToDecimal(r.Rows[0][i], CultureInfo.InvariantCulture) == 0);
    }

    private (DateOnly From, DateOnly To) DateRange(string fromDate, string toDate)
    {
        var from = ParseDate(fromDate, "fromDate");
        var to = ParseDate(toDate, "toDate");
        if (to < from) (from, to) = (to, from);

        var today = _clock.Today;
        if (from > today)
            throw new ToolArgumentException($"The start date {from:yyyy-MM-dd} is in the future; today is {today:yyyy-MM-dd}. If the user named a month without a year, use its most recent past occurrence and call the tool again.");
        if (to > today) to = today;

        var days = to.DayNumber - from.DayNumber + 1;
        if (days > _options.MaxRangeDays)
            throw new ToolArgumentException($"The range is {days} days; the maximum is {_options.MaxRangeDays}. Ask the user for a shorter period.");

        return (from, to);
    }

    private static DateOnly ParseDate(string? value, string name)
    {
        if (DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new ToolArgumentException($"{name} must be a date in yyyy-MM-dd format, but was '{value}'. Work out the date from today's date in the instructions.");
    }

    private static T ParseEnum<T>(string? value, string name) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value?.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        var allowed = string.Join(", ", Enum.GetNames<T>().Select(n => $"'{n.ToLowerInvariant()}'"));
        throw new ToolArgumentException($"{name} must be one of {allowed}, but was '{value}'.");
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}
