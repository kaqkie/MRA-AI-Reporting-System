using Microsoft.Data.SqlClient;

namespace MraReporting.Data;

public enum Period { Day, Month, Total }
public enum TaxpayerMetric { Sales, Vat }
public enum FailureGrouping { Taxpayer, Terminal, Day }
public enum FlagGrouping { Type, Status, Day }
public enum RecallGrouping { Type, Day }
public enum CustomsGrouping { Station, Day, Month }

/// <summary>
/// The vetted query library. Every SQL statement the app can run is in this file.
/// Rules applied throughout:
///  - dates are half-open ranges (>= from, &lt; day after to), so an index on the date can be used;
///  - nullable flags are wrapped in ISNULL, so a NULL IsRecalled is treated as "not recalled";
///  - blob columns (Signature, SignedPayload, InvoiceRequest, SupportingDocuments ...) are never selected;
///  - anything put into SQL text (ORDER BY columns, TOP) comes from a fixed list, never from the user.
/// NOTE: none of these queries uses a summary table yet. On the full production data, the totals
/// queries should move to [AI-REPORTING].dbo.DailySales once it is being refreshed.
/// </summary>
public sealed partial class EisQueries
{
    private const string InvoiceWindow = "i.InvoiceDateTime >= @from AND i.InvoiceDateTime < @toEx";
    private const string NotRecalled = "ISNULL(i.IsRecalled, 0) = 0";
    private const string RecalledNote = "Recalled invoices are excluded (a NULL recall flag counts as not recalled).";

    private readonly SqlRunner _sql;
    private readonly StationDirectory _stations;
    private readonly OfficeDirectory _offices;
    private readonly RateDirectory _rates;

    public EisQueries(SqlRunner sql, StationDirectory stations, OfficeDirectory offices, RateDirectory rates)
    {
        _sql = sql;
        _stations = stations;
        _offices = offices;
        _rates = rates;
    }

    /// <summary>Replaces tax rate codes in one column with "Rate name (CODE)" where a name is on file.</summary>
    private QueryResult LabelRates(QueryResult r, int column)
    {
        var rows = r.Rows.Select(row =>
        {
            var copy = (object?[])row.Clone();
            copy[column] = copy[column] is string code && code.Trim().Length > 0 ? _rates.Label(code) : "(no rate code)";
            return copy;
        }).ToList();
        return r with { Rows = rows };
    }

    /// <summary>Replaces tax office codes in one column with "Official Name (CODE)" where a name is on file.</summary>
    private QueryResult LabelTaxOffices(QueryResult r, int column)
    {
        var rows = r.Rows.Select(row =>
        {
            var copy = (object?[])row.Clone();
            if (copy[column] is string code && !code.StartsWith('(')) copy[column] = _offices.Label(code);
            return copy;
        }).ToList();
        return r with { Rows = rows };
    }

    private static SqlParameter[] Window(DateOnly from, DateOnly to) =>
        [SqlRunner.Date("@from", from), SqlRunner.Date("@toEx", to.AddDays(1))];

    private static string Range(DateOnly from, DateOnly to) =>
        from == to ? from.ToString("yyyy-MM-dd") : $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";

    // ---------------------------------------------------------------- sales and VAT

    public Task<QueryResult> SalesTotalsAsync(DateOnly from, DateOnly to, Period period, CancellationToken ct)
    {
        var (select, groupBy, visual, label) = period switch
        {
            Period.Day => ("CAST(i.InvoiceDateTime AS date) AS [Date],",
                           "GROUP BY CAST(i.InvoiceDateTime AS date) ORDER BY [Date]", VisualKind.Line, "by day"),
            Period.Month => ("DATEFROMPARTS(YEAR(i.InvoiceDateTime), MONTH(i.InvoiceDateTime), 1) AS [Month],",
                             "GROUP BY DATEFROMPARTS(YEAR(i.InvoiceDateTime), MONTH(i.InvoiceDateTime), 1) ORDER BY [Month]", VisualKind.Bar, "by month"),
            _ => ("", "", VisualKind.Table, "total")
        };

        var sql = $"""
            SELECT {select}
                   COUNT_BIG(*)                   AS Invoices,
                   SUM(ISNULL(i.InvoiceTotal, 0)) AS GrossSales,
                   SUM(ISNULL(i.TotalVAT, 0))     AS VAT
            FROM eis_staging.Invoices i
            WHERE {InvoiceWindow} AND {NotRecalled}
            {groupBy};
            """;

        IReadOnlyList<string>? chartColumns = period == Period.Total ? null : new[] { "GrossSales", "VAT" };
        return _sql.QueryAsync($"Gross sales and VAT {label}, {Range(from, to)} (MWK)", sql, Window(from, to), visual, ct,
            note: RecalledNote, chartValueColumns: chartColumns);
    }

    public async Task<QueryResult> VatByRateAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        // RateChargedPct is VAT / taxable amount for each code, worked out from the invoices themselves,
        // so the rate behind a code can be seen even before its name is filled in.
        var sql = $"""
            SELECT b.RateID,
                   COUNT_BIG(*)                    AS TaxLines,
                   SUM(ISNULL(b.TaxableAmount, 0)) AS TaxableAmount,
                   SUM(ISNULL(b.TaxAmount, 0))     AS VAT,
                   CAST(ROUND(100.0 * SUM(ISNULL(b.TaxAmount, 0)) / NULLIF(SUM(ISNULL(b.TaxableAmount, 0)), 0), 2)
                        AS decimal(9, 2))          AS RateChargedPct
            FROM eis_staging.InvoiceTaxBreakdown b
            JOIN eis_staging.Invoices i ON i.InvoiceNumber = b.InvoiceNumber
            WHERE {InvoiceWindow} AND {NotRecalled}
            GROUP BY b.RateID
            ORDER BY VAT DESC;
            """;

        var result = await _sql.QueryAsync($"VAT by tax rate, {Range(from, to)} (MWK)", sql, Window(from, to), VisualKind.Pie, ct,
            note: RecalledNote + " Rate names come from MRA's tax rate list; codes without a name on the list are shown as stored in EIS. "
                  + "Rate charged is the VAT divided by the taxable amount on the invoices.",
            chartValueColumns: ["VAT"]);
        return LabelRates(result, column: 0);
    }

    public Task<QueryResult> TopTaxpayersAsync(DateOnly from, DateOnly to, int top, TaxpayerMetric metric, CancellationToken ct)
    {
        var orderBy = metric == TaxpayerMetric.Vat ? "VAT" : "GrossSales";
        var sql = $"""
            WITH s AS (
                SELECT TOP (@top)
                       i.SellerTIN,
                       COUNT_BIG(*)                   AS Invoices,
                       SUM(ISNULL(i.InvoiceTotal, 0)) AS GrossSales,
                       SUM(ISNULL(i.TotalVAT, 0))     AS VAT
                FROM eis_staging.Invoices i
                WHERE {InvoiceWindow} AND {NotRecalled}
                GROUP BY i.SellerTIN
                ORDER BY {orderBy} DESC
            )
            SELECT s.SellerTIN AS TIN, tp.BusinessName, s.Invoices, s.GrossSales, s.VAT
            FROM s
            OUTER APPLY (SELECT TOP (1) t.BusinessName
                         FROM eis_staging.Taxpayers t
                         WHERE t.TIN = s.SellerTIN
                         ORDER BY CASE WHEN t.DeletedOn IS NULL THEN 0 ELSE 1 END) tp
            ORDER BY s.{orderBy} DESC;
            """;

        SqlParameter[] ps = [.. Window(from, to), SqlRunner.Int("@top", top)];
        return _sql.QueryAsync($"Top {top} taxpayers by {(metric == TaxpayerMetric.Vat ? "VAT" : "gross sales")}, {Range(from, to)} (MWK)",
            sql, ps, VisualKind.Bar, ct, note: RecalledNote, chartValueColumns: [orderBy]);
    }

    /// <summary>
    /// The single largest invoices in a period. One very large invoice explains most sudden spikes
    /// in daily sales, so the report and the chat use this to say why a day stands out.
    /// </summary>
    public Task<QueryResult> LargestInvoicesAsync(DateOnly from, DateOnly to, int top, CancellationToken ct)
    {
        var sql = $"""
            WITH s AS (
                SELECT TOP (@top)
                       i.SellerTIN,
                       CAST(i.InvoiceDateTime AS date) AS InvoiceDate,
                       LTRIM(RTRIM(i.TerminalId))      AS TerminalId,
                       ISNULL(i.InvoiceTotal, 0)       AS GrossSales,
                       ISNULL(i.TotalVAT, 0)           AS VAT
                FROM eis_staging.Invoices i
                WHERE {InvoiceWindow} AND {NotRecalled}
                ORDER BY i.InvoiceTotal DESC
            )
            SELECT s.SellerTIN AS TIN, tp.BusinessName, s.InvoiceDate, s.TerminalId, s.GrossSales, s.VAT
            FROM s
            OUTER APPLY (SELECT TOP (1) t.BusinessName
                         FROM eis_staging.Taxpayers t
                         WHERE t.TIN = s.SellerTIN
                         ORDER BY CASE WHEN t.DeletedOn IS NULL THEN 0 ELSE 1 END) tp
            ORDER BY s.GrossSales DESC;
            """;

        SqlParameter[] ps = [.. Window(from, to), SqlRunner.Int("@top", top)];
        return _sql.QueryAsync($"{top} largest invoices, {Range(from, to)} (MWK)", sql, ps, VisualKind.Table, ct,
            note: RecalledNote + " Each row is one invoice. Very large single invoices explain most sudden spikes in daily sales; confirm unusual amounts with the taxpayer.",
            chartValueColumns: ["GrossSales"]);
    }

    /// <summary>One-row headline figures for a single day, used by the daily report.</summary>
    public Task<QueryResult> DailyKpisAsync(DateOnly date, CancellationToken ct) => KpisAsync(date, date, ct);

    /// <summary>One-row headline figures for any period, used by the summary report.</summary>
    public Task<QueryResult> KpisAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var sql = $"""
            SELECT SUM(CASE WHEN {NotRecalled} THEN 1 ELSE 0 END)                                   AS Invoices,
                   SUM(CASE WHEN {NotRecalled} THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END)           AS GrossSales,
                   SUM(CASE WHEN {NotRecalled} THEN ISNULL(i.TotalVAT, 0) ELSE 0 END)               AS VAT,
                   COUNT(DISTINCT CASE WHEN {NotRecalled} THEN i.SellerTIN END)                     AS ActiveSellers,
                   SUM(CASE WHEN i.IsRecalled = 1 THEN 1 ELSE 0 END)                               AS RecalledInvoices,
                   SUM(CASE WHEN {NotRecalled} AND i.IsExport = 1 THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END)       AS ExportSales,
                   SUM(CASE WHEN {NotRecalled} AND i.IsReliefSupply = 1 THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END) AS ReliefSupplySales
            FROM eis_staging.Invoices i
            WHERE {InvoiceWindow};
            """;

        return _sql.QueryAsync($"Headline figures, {Range(from, to)} (MWK)", sql, Window(from, to), VisualKind.Table, ct, note: RecalledNote);
    }

    // ---------------------------------------------------------------- data coverage

    /// <summary>Tables whose date range can be reported. Keys are used in code only, never from user input.</summary>
    private static readonly Dictionary<string, (string Table, string Column, string Label)> CoverageSources = new()
    {
        ["invoices"] = ("eis_staging.Invoices", "InvoiceDateTime", "Invoices"),
        ["failed"] = ("eis_staging.FailedTransactions", "DateReceived", "Failed submissions"),
        ["flags"] = ("eis_staging.TransactionsFlags", "TransactionDate", "Flagged transactions"),
        ["voids"] = ("eis_staging.VoidReceiptRequests", "RequestedOn", "Void requests"),
        ["customs"] = ("staging.Epay_CustomsLiability", "AssessmentDate", "Customs liabilities (ePayment)"),
    };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, QueryResult Result)> _coverageCache = new();

    /// <summary>First and last date in one table. Cached for 30 minutes because the Invoices scan is slow.</summary>
    public async Task<QueryResult> CoverageAsync(string key, CancellationToken ct)
    {
        if (_coverageCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(30))
            return cached.Result;

        var source = CoverageSources[key];
        // Overall first/last date, plus the "main" range: days holding at least 0.005% of all rows
        // (minimum 3). A handful of records with wrong dates (device clocks set to 2006 or 2027)
        // should not make the data look as if it covers twenty years.
        var sql = $"""
            WITH d AS (
                SELECT CAST({source.Column} AS date) AS D, COUNT_BIG(*) AS N
                FROM {source.Table}
                WHERE {source.Column} IS NOT NULL
                GROUP BY CAST({source.Column} AS date)
            ),
            tot AS (SELECT SUM(N) AS Total FROM d)
            SELECT '{source.Label}' AS Data,
                   MIN(CASE WHEN d.N >= CASE WHEN tot.Total * 0.00005 > 3 THEN tot.Total * 0.00005 ELSE 3 END THEN d.D END) AS MainFrom,
                   MAX(CASE WHEN d.N >= CASE WHEN tot.Total * 0.00005 > 3 THEN tot.Total * 0.00005 ELSE 3 END THEN d.D END) AS MainTo,
                   MIN(d.D) AS EarliestDate,
                   MAX(d.D) AS LatestDate,
                   tot.Total AS [Rows]
            FROM d CROSS JOIN tot
            GROUP BY tot.Total;
            """;
        var result = await _sql.QueryAsync($"Date range of {source.Label.ToLowerInvariant()} in EIS", sql, [], VisualKind.Table, ct,
            note: "MainFrom/MainTo is where almost all records are. Earliest/Latest include a few records with wrong dates.");
        _coverageCache[key] = (DateTime.UtcNow, result);
        return result;
    }

    public async Task<QueryResult> AllCoverageAsync(CancellationToken ct)
    {
        var parts = new List<QueryResult>();
        foreach (var key in CoverageSources.Keys)
            parts.Add(await CoverageAsync(key, ct));

        return new QueryResult("Date range covered by EIS data", parts[0].Columns,
            parts.SelectMany(p => p.Rows).ToList(), VisualKind.Table, parts.Max(p => p.AsOf),
            Note: "MainFrom/MainTo is where almost all records are; EarliestDate/LatestDate include a few records with wrong dates. Questions outside the main range will return little or no data.");
    }

    /// <summary>One sentence such as "Invoices data runs from 2025-01-02 to 2026-05-27."</summary>
    public async Task<string?> CoverageSentenceAsync(string key, CancellationToken ct)
    {
        var r = await CoverageAsync(key, ct);
        if (r.Rows.Count == 0 || r.Rows[0][3] is null) return $"There is no {CoverageSources[key].Label.ToLowerInvariant()} data at all.";
        var row = r.Rows[0];
        var main = row[1] is null
            ? $"{CoverageSources[key].Label} data runs from {QueryResult.Format(row[3])} to {QueryResult.Format(row[4])}."
            : $"{CoverageSources[key].Label} data is mainly from {QueryResult.Format(row[1])} to {QueryResult.Format(row[2])}.";
        var outliers = row[1] is not null && (!Equals(row[1], row[3]) || !Equals(row[2], row[4]))
            ? $" A few records carry dates from {QueryResult.Format(row[3])} to {QueryResult.Format(row[4])}, probably wrong device clocks."
            : "";
        return main + outliers;
    }

    // ---------------------------------------------------------------- register counts

    /// <summary>How many taxpayers, sites, terminals and tax office codes EIS holds. Small tables only, so this is fast.</summary>
    public Task<QueryResult> RegisterCountsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT 'Registered taxpayers (not deleted)' AS Measure,
                   COUNT_BIG(*) AS [Count]
            FROM eis_staging.Taxpayers WHERE DeletedOn IS NULL
            UNION ALL
            SELECT 'VAT-registered taxpayers', COUNT_BIG(*)
            FROM eis_staging.Taxpayers WHERE DeletedOn IS NULL AND IsVATRegistered = 1
            UNION ALL
            SELECT 'Taxpayer business sites, active', COUNT_BIG(*)
            FROM eis_staging.TerminalSites WHERE ISNULL(IsDeactivated, 0) = 0
            UNION ALL
            SELECT 'Taxpayer business sites, all', COUNT_BIG(*)
            FROM eis_staging.TerminalSites
            UNION ALL
            SELECT 'Fiscal terminals (POS devices), active', COUNT_BIG(*)
            FROM eis_staging.Terminal WHERE IsActive = 1
            UNION ALL
            SELECT 'Fiscal terminals (POS devices), all', COUNT_BIG(*)
            FROM eis_staging.Terminal
            UNION ALL
            SELECT 'Distinct tax office codes on taxpayer records', COUNT_BIG(DISTINCT TaxOfficeCode)
            FROM eis_staging.Taxpayers WHERE TaxOfficeCode IS NOT NULL AND DeletedOn IS NULL
            UNION ALL
            SELECT 'Distinct customs declaration station codes', COUNT_BIG(DISTINCT DeclarationStationCode)
            FROM eis_staging.ImportationDetails WHERE DeclarationStationCode IS NOT NULL;
            """;

        return _sql.QueryAsync("EIS register counts", sql, [], VisualKind.Table, ct,
            note: "Business sites are taxpayers' own premises, not MRA offices. Which of these codes corresponds to an MRA station is not confirmed yet.");
    }

    /// <summary>
    /// Every tax office code and every customs declaration station code, matched on the code itself,
    /// so it is visible whether the two lists are the same set of stations.
    /// </summary>
    public async Task<QueryResult> StationCodesAsync(CancellationToken ct)
    {
        const string sql = """
            WITH t AS (
                SELECT LTRIM(RTRIM(TaxOfficeCode)) AS Code, COUNT_BIG(*) AS Taxpayers
                FROM eis_staging.Taxpayers
                WHERE TaxOfficeCode IS NOT NULL AND DeletedOn IS NULL
                GROUP BY LTRIM(RTRIM(TaxOfficeCode))
            ),
            c AS (
                SELECT LTRIM(RTRIM(DeclarationStationCode)) AS Code, COUNT_BIG(*) AS Declarations
                FROM eis_staging.ImportationDetails
                WHERE DeclarationStationCode IS NOT NULL
                GROUP BY LTRIM(RTRIM(DeclarationStationCode))
            )
            SELECT COALESCE(t.Code, c.Code) AS Code,
                   t.Taxpayers    AS TaxpayersRegistered,
                   c.Declarations AS ImportDeclarations,
                   CASE WHEN t.Code IS NOT NULL AND c.Code IS NOT NULL THEN 'Both'
                        WHEN t.Code IS NOT NULL THEN 'Tax office only'
                        ELSE 'Customs only' END AS FoundIn
            FROM t
            FULL OUTER JOIN c ON c.Code = t.Code
            ORDER BY Code;
            """;

        var result = await _sql.QueryAsync("Station codes in EIS: tax offices and customs stations", sql, [], VisualKind.Table, ct,
            note: "Customs station names come from the ePayment customs data. Tax office names come from the office names list maintained by MRA; codes without a name there are shown as codes only.");
        var withCustoms = AddStationNames(result, codeIndex: 0, header: "CustomsStationName", insertBefore: false, await _stations.CustomsNamesAsync(ct));
        return AddStationNames(withCustoms, codeIndex: 0, header: "TaxOfficeName", insertBefore: false, _offices.Names);
    }

    /// <summary>Adds a column with the station name next to a column holding customs station codes.</summary>
    private static QueryResult AddStationNames(QueryResult r, int codeIndex, string header, bool insertBefore,
        IReadOnlyDictionary<string, string> names)
    {
        var at = insertBefore ? codeIndex : codeIndex + 1;
        var columns = r.Columns.ToList();
        columns.Insert(at, header);
        var rows = r.Rows.Select(row =>
        {
            var values = row.ToList();
            var code = (row[codeIndex] as string)?.Trim();
            values.Insert(at, code is not null && names.TryGetValue(code, out var name) ? name : null);
            return values.ToArray();
        }).ToList();
        return r with { Columns = columns, Rows = rows };
    }

    // ---------------------------------------------------------------- customs (ePayment)

    /// <summary>
    /// Customs liabilities submitted through ePayment, by station, day or month. "Paid" counts liabilities
    /// that have an ASYCUDA receipt. Optionally limited to the given station codes.
    /// </summary>
    public async Task<QueryResult> CustomsCollectionsAsync(DateOnly from, DateOnly to, IReadOnlyList<string> stationCodes,
        CustomsGrouping by, CancellationToken ct)
    {
        var (key, alias, visual, label) = by switch
        {
            CustomsGrouping.Day => ("CAST(c.AssessmentDate AS date)", "[Date]", VisualKind.Line, "by day"),
            CustomsGrouping.Month => ("DATEFROMPARTS(YEAR(c.AssessmentDate), MONTH(c.AssessmentDate), 1)", "[Month]", VisualKind.Bar, "by month"),
            _ => ("LTRIM(RTRIM(c.OfficeCode))", "StationCode", VisualKind.Bar, "by station")
        };

        var parameters = new List<SqlParameter>(Window(from, to));
        var stationFilter = "";
        if (stationCodes.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < stationCodes.Count; i++)
            {
                names.Add($"@s{i}");
                parameters.Add(new SqlParameter($"@s{i}", System.Data.SqlDbType.NVarChar, 50) { Value = stationCodes[i] });
            }
            stationFilter = $"AND LTRIM(RTRIM(c.OfficeCode)) IN ({string.Join(", ", names)})";
        }

        var sql = $"""
            SELECT {key} AS {alias},
                   COUNT_BIG(*)                AS Liabilities,
                   SUM(ISNULL(c.Amount, 0))    AS AssessedAmount,
                   SUM(CASE WHEN NULLIF(LTRIM(RTRIM(c.AsycudaReceipt)), '') IS NOT NULL
                            THEN ISNULL(c.Amount, 0) ELSE 0 END) AS PaidAmount
            FROM staging.Epay_CustomsLiability c
            WHERE c.AssessmentDate >= @from AND c.AssessmentDate < @toEx
              AND c.OfficeCode IS NOT NULL
              {stationFilter}
            GROUP BY {key}
            ORDER BY {(by == CustomsGrouping.Station ? "AssessedAmount DESC" : alias)};
            """;

        var stationNames = await _stations.CustomsNamesAsync(ct);
        var scope = stationCodes.Count == 0
            ? "all customs stations"
            : string.Join(", ", stationCodes.Select(c => stationNames.TryGetValue(c, out var n) ? n : c));

        var result = await _sql.QueryAsync($"Customs payments {label}, {scope}, {Range(from, to)} (MWK)", sql, parameters, visual, ct,
            note: "Source: ePayment customs liabilities, by assessment date. Paid = liabilities with an ASYCUDA receipt. " +
                  "Payments made outside ePayment are not included, so confirm with Customs before quoting these as official collections.",
            chartValueColumns: ["PaidAmount", "AssessedAmount"]);

        return by == CustomsGrouping.Station
            ? AddStationNames(result, codeIndex: 0, header: "Station", insertBefore: true, stationNames)
            : result;
    }

    // ---------------------------------------------------------------- domestic sales by tax office

    /// <summary>
    /// EIS sales and VAT grouped by the tax office where each seller is registered. Invoices are first
    /// totalled per seller (one pass over Invoices), then matched to Taxpayers, which keeps it fast.
    /// </summary>
    public async Task<QueryResult> SalesByTaxOfficeAsync(DateOnly from, DateOnly to, string? taxOffice, CancellationToken ct)
    {
        var officeFilter = taxOffice is null ? "" : "WHERE o.TaxOffice = @office";
        var sql = $"""
            WITH s AS (
                SELECT i.SellerTIN,
                       COUNT_BIG(*)                   AS Invoices,
                       SUM(ISNULL(i.InvoiceTotal, 0)) AS GrossSales,
                       SUM(ISNULL(i.TotalVAT, 0))     AS VAT
                FROM eis_staging.Invoices i
                WHERE {InvoiceWindow} AND {NotRecalled}
                GROUP BY i.SellerTIN
            ),
            o AS (
                SELECT TIN, MAX(LTRIM(RTRIM(TaxOfficeCode))) AS TaxOffice
                FROM eis_staging.Taxpayers
                WHERE DeletedOn IS NULL
                GROUP BY TIN
            )
            SELECT ISNULL(o.TaxOffice, '(seller not in register)') AS TaxOffice,
                   COUNT(*)          AS Sellers,
                   SUM(s.Invoices)   AS Invoices,
                   SUM(s.GrossSales) AS GrossSales,
                   SUM(s.VAT)        AS VAT
            FROM s
            LEFT JOIN o ON o.TIN = s.SellerTIN
            {officeFilter}
            GROUP BY ISNULL(o.TaxOffice, '(seller not in register)')
            ORDER BY VAT DESC;
            """;

        var parameters = new List<SqlParameter>(Window(from, to));
        if (taxOffice is not null) parameters.Add(SqlRunner.VarChar("@office", taxOffice, 50));

        var result = await _sql.QueryAsync($"EIS sales and VAT by seller's tax office{(taxOffice is null ? "" : $" ({_offices.Label(taxOffice)})")}, {Range(from, to)} (MWK)",
            sql, parameters, VisualKind.Bar, ct,
            note: RecalledNote + " Each invoice counts at the tax office where the seller is registered. Office names come from MRA's office names list; offices without a name on the list are shown by code only.",
            chartValueColumns: ["VAT", "GrossSales"]);
        return LabelTaxOffices(result, column: 0);
    }

    // ---------------------------------------------------------------- taxpayers

    public Task<QueryResult> TaxpayerProfileAsync(string tin, DateOnly from, DateOnly to, CancellationToken ct)
    {
        // Contact details (email, phone, address) are deliberately left out.
        // Terminals are not linked to taxpayers in the register, so the terminal count comes from invoices.
        var sql = $"""
            SELECT t.TIN,
                   t.BusinessName,
                   t.TaxOfficeCode AS TaxOffice,
                   t.IsVATRegistered,
                   t.IsBlackListed,
                   t.Created_at AS RegisteredOn,
                   t.DeletedOn,
                   (SELECT COUNT(*) FROM eis_staging.TerminalSites s
                    WHERE s.TIN = t.TIN AND ISNULL(s.IsDeactivated, 0) = 0) AS ActiveSites,
                   ISNULL(u.TerminalsUsed, 0) AS TerminalsUsed,
                   ISNULL(u.Invoices, 0)      AS Invoices,
                   u.LastInvoice
            FROM eis_staging.Taxpayers t
            OUTER APPLY (SELECT COUNT(DISTINCT LTRIM(RTRIM(i.TerminalId))) AS TerminalsUsed,
                                COUNT_BIG(*)                              AS Invoices,
                                MAX(i.InvoiceDateTime)                    AS LastInvoice
                         FROM eis_staging.Invoices i
                         WHERE i.SellerTIN = t.TIN AND {InvoiceWindow} AND {NotRecalled}) u
            WHERE t.TIN = @tin;
            """;

        SqlParameter[] ps = [.. Window(from, to), SqlRunner.VarChar("@tin", tin, 30)];
        return LabelAsync(_sql.QueryAsync($"Taxpayer register entry for TIN {tin}", sql, ps, VisualKind.Table, ct,
            note: "If no row is returned, this TIN is not onboarded. A value in DeletedOn means the taxpayer was removed. " +
                  $"TerminalsUsed is the number of distinct fiscal terminals (POS devices) that issued invoices from {Range(from, to)}, and Invoices the invoices in that period; " +
                  "terminals are not linked to taxpayers in the register, so a registered terminal that issued no invoice is not counted. ActiveSites is business premises, not terminals."));
    }

    private async Task<QueryResult> LabelAsync(Task<QueryResult> query) => LabelTaxOffices(await query, column: 2);

    /// <summary>
    /// How many fiscal terminals one taxpayer (found by TIN or by part of the business name) used
    /// in a period: the distinct terminals that issued at least one invoice. Up to 10 taxpayers
    /// match a name, so similar names can be told apart.
    /// </summary>
    public async Task<QueryResult> TaxpayerTerminalsAsync(string taxpayer, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var text = taxpayer.Trim();
        var byTin = text.All(char.IsDigit);
        string match;
        SqlParameter key;
        if (byTin)
        {
            match = "tp.TIN = @key";
            key = SqlRunner.VarChar("@key", text, 30);
        }
        else
        {
            // Every word must appear, in order, anywhere in the name: "portland cement" finds
            // "PORTLAND CEMENT (MALAWI) LIMITED". LIKE wildcards typed by the user are escaped.
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\["));
            match = "tp.BusinessName LIKE @key ESCAPE '\\'";
            key = SqlRunner.VarChar("@key", "%" + string.Join("%", words) + "%", 200);
        }

        var sql = $"""
            WITH t AS (
                SELECT TOP (10) tp.TIN, tp.BusinessName, tp.TaxOfficeCode
                FROM eis_staging.Taxpayers tp
                WHERE tp.DeletedOn IS NULL AND {match}
                ORDER BY tp.BusinessName
            )
            SELECT t.TIN,
                   t.BusinessName,
                   t.TaxOfficeCode AS TaxOffice,
                   (SELECT COUNT(*) FROM eis_staging.TerminalSites s
                    WHERE s.TIN = t.TIN AND ISNULL(s.IsDeactivated, 0) = 0) AS ActiveSites,
                   ISNULL(u.TerminalsUsed, 0) AS TerminalsUsed,
                   ISNULL(u.Invoices, 0)      AS Invoices,
                   u.LastInvoice
            FROM t
            OUTER APPLY (SELECT COUNT(DISTINCT LTRIM(RTRIM(i.TerminalId))) AS TerminalsUsed,
                                COUNT_BIG(*)                              AS Invoices,
                                MAX(i.InvoiceDateTime)                    AS LastInvoice
                         FROM eis_staging.Invoices i
                         WHERE i.SellerTIN = t.TIN AND {InvoiceWindow} AND {NotRecalled}) u
            ORDER BY ISNULL(u.Invoices, 0) DESC, t.BusinessName;
            """;

        SqlParameter[] ps = [.. Window(from, to), key];
        var result = await _sql.QueryAsync($"Fiscal terminals used by {(byTin ? "TIN " + text : $"taxpayers named like '{text}'")}, {Range(from, to)}",
            sql, ps, VisualKind.Table, ct,
            note: "TerminalsUsed counts the distinct fiscal terminals (POS devices) that issued at least one invoice in this period. " +
                  "A terminal that is registered but issued no invoice in the period is not counted. ActiveSites is the taxpayer's active business premises. " +
                  RecalledNote);
        return LabelTaxOffices(result, column: 2);
    }

    // ---------------------------------------------------------------- exceptions

    public Task<QueryResult> FailedTransactionsAsync(DateOnly from, DateOnly to, FailureGrouping by, int top, CancellationToken ct)
    {
        string sql;
        VisualKind visual;
        string label;
        switch (by)
        {
            case FailureGrouping.Day:
                sql = """
                    SELECT CAST(f.DateReceived AS date) AS [Date], COUNT_BIG(*) AS Failures
                    FROM eis_staging.FailedTransactions f
                    WHERE f.DateReceived >= @from AND f.DateReceived < @toEx
                    GROUP BY CAST(f.DateReceived AS date)
                    ORDER BY [Date];
                    """;
                visual = VisualKind.Line; label = "by day";
                break;
            case FailureGrouping.Terminal:
                sql = """
                    SELECT TOP (@top) f.TerminalId, COUNT_BIG(*) AS Failures,
                           MIN(f.DateReceived) AS FirstFailure, MAX(f.DateReceived) AS LastFailure
                    FROM eis_staging.FailedTransactions f
                    WHERE f.DateReceived >= @from AND f.DateReceived < @toEx
                    GROUP BY f.TerminalId
                    ORDER BY Failures DESC;
                    """;
                visual = VisualKind.Bar; label = $"top {top} terminals";
                break;
            default:
                sql = """
                    WITH s AS (
                        SELECT TOP (@top) f.SellerTIN, COUNT_BIG(*) AS Failures,
                               MIN(f.DateReceived) AS FirstFailure, MAX(f.DateReceived) AS LastFailure
                        FROM eis_staging.FailedTransactions f
                        WHERE f.DateReceived >= @from AND f.DateReceived < @toEx
                        GROUP BY f.SellerTIN
                        ORDER BY Failures DESC
                    )
                    SELECT s.SellerTIN AS TIN, tp.BusinessName, s.Failures, s.FirstFailure, s.LastFailure
                    FROM s
                    OUTER APPLY (SELECT TOP (1) t.BusinessName FROM eis_staging.Taxpayers t
                                 WHERE t.TIN = s.SellerTIN
                                 ORDER BY CASE WHEN t.DeletedOn IS NULL THEN 0 ELSE 1 END) tp
                    ORDER BY s.Failures DESC;
                    """;
                visual = VisualKind.Bar; label = $"top {top} taxpayers";
                break;
        }

        SqlParameter[] ps = [.. Window(from, to), SqlRunner.Int("@top", top)];
        return _sql.QueryAsync($"Failed invoice submissions, {label}, {Range(from, to)}", sql, ps, visual, ct,
            note: "A failed submission may be retried successfully later, so failures are not lost invoices.",
            chartValueColumns: ["Failures"]);
    }

    public Task<QueryResult> RedFlagsAsync(DateOnly from, DateOnly to, FlagGrouping by, CancellationToken ct)
    {
        var (key, alias, visual, label) = by switch
        {
            FlagGrouping.Day => ("CAST(f.TransactionDate AS date)", "[Date]", VisualKind.Line, "by day"),
            FlagGrouping.Status => ("ISNULL(f.InvestigationStatus, '(not set)')", "InvestigationStatus", VisualKind.Bar, "by investigation status"),
            _ => ("ISNULL(f.TransactionFlagType, '(not set)')", "FlagType", VisualKind.Bar, "by flag type")
        };

        var sql = $"""
            SELECT {key} AS {alias},
                   COUNT_BIG(*)                                        AS Transactions,
                   SUM(CASE WHEN f.IsRedFlagged = 1 THEN 1 ELSE 0 END) AS RedFlagged,
                   SUM(ISNULL(f.TransactionAmount, 0))                 AS Amount
            FROM eis_staging.TransactionsFlags f
            WHERE f.TransactionDate >= @from AND f.TransactionDate < @toEx
            GROUP BY {key}
            ORDER BY {(by == FlagGrouping.Day ? alias : "Transactions DESC")};
            """;

        return _sql.QueryAsync($"Flagged transactions {label}, {Range(from, to)}", sql, Window(from, to), visual, ct,
            chartValueColumns: ["RedFlagged"]);
    }

    public Task<QueryResult> VoidRequestsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string sql = """
            SELECT v.Status, COUNT_BIG(*) AS Requests,
                   MIN(v.RequestedOn) AS FirstRequest, MAX(v.RequestedOn) AS LastRequest
            FROM eis_staging.VoidReceiptRequests v
            WHERE v.RequestedOn >= @from AND v.RequestedOn < @toEx
            GROUP BY v.Status
            ORDER BY Requests DESC;
            """;

        return _sql.QueryAsync($"Void receipt requests by status, {Range(from, to)}", sql, Window(from, to), VisualKind.Bar, ct,
            note: "Status is a numeric code; its meaning is not confirmed yet, so describe statuses by number only.",
            chartValueColumns: ["Requests"]);
    }

    public Task<QueryResult> RecalledInvoicesAsync(DateOnly from, DateOnly to, RecallGrouping by, CancellationToken ct)
    {
        var (key, alias, visual) = by == RecallGrouping.Day
            ? ("CAST(i.InvoiceDateTime AS date)", "[Date]", VisualKind.Line)
            : ("ISNULL(i.RecallType, '(not set)')", "RecallType", VisualKind.Bar);

        var sql = $"""
            SELECT {key} AS {alias},
                   COUNT_BIG(*)                   AS RecalledInvoices,
                   SUM(ISNULL(i.InvoiceTotal, 0)) AS RecalledValue
            FROM eis_staging.Invoices i
            WHERE {InvoiceWindow} AND i.IsRecalled = 1
            GROUP BY {key}
            ORDER BY {(by == RecallGrouping.Day ? alias : "RecalledInvoices DESC")};
            """;

        return _sql.QueryAsync($"Recalled invoices {(by == RecallGrouping.Day ? "by day" : "by recall type")}, {Range(from, to)} (MWK)",
            sql, Window(from, to), visual, ct,
            note: "Filtered on the original invoice date. How recalls are recorded in EIS is not confirmed yet.",
            chartValueColumns: ["RecalledInvoices"]);
    }

    // ---------------------------------------------------------------- devices

    public Task<QueryResult> TamperedTerminalsAsync(int top, CancellationToken ct)
    {
        // Note the misspelled column name PlartformOS: it is spelled that way in EIS.
        const string sql = """
            SELECT TOP (@top)
                   t.TerminalID, t.TerminalLabel, t.TamperAttempts, t.LastTamperAttemptAt,
                   t.IsActive, os.Name AS OperatingSystem, t.POSProductVersion
            FROM eis_staging.Terminal t
            LEFT JOIN eis_staging.TerminalOperatingSystems os ON os.ID = t.PlartformOS
            WHERE ISNULL(t.TamperAttempts, 0) > 0
            ORDER BY t.LastTamperAttemptAt DESC;
            """;

        return _sql.QueryAsync($"Terminals with tamper attempts (most recent {top})", sql, [SqlRunner.Int("@top", top)], VisualKind.Table, ct);
    }
}
