using Microsoft.Data.SqlClient;

namespace MraReporting.Data;

public enum InvoiceGrouping { Total, Day, Week, Month, Taxpayer, TaxOffice, Site, Terminal, Buyer, PaymentMethod }
public enum InvoiceSort { Vat, Sales, Invoices }
public enum InvoiceKind { All, Domestic, Export, Relief }
public enum CompareGrouping { Total, Taxpayer, TaxOffice }
public enum TaxpayerListMode { List, CountByOffice, CountByMonth }
public enum TerminalGrouping { OperatingSystem, Version, ActivationMonth, Status }

/// <summary>Which invoices to include: some sellers, the sellers of some tax offices, and a kind of sale.</summary>
public sealed record InvoiceFilter(
    IReadOnlyList<string>? Tins = null,
    IReadOnlyList<string>? Offices = null,
    InvoiceKind Kind = InvoiceKind.All,
    string? ScopeLabel = null);

/// <summary>
/// Flexible lookups built from fixed SQL pieces. The model chooses a grouping, a sort, filters and dates
/// from fixed lists; values always go in as parameters, never into the SQL text, so these stay as safe
/// as the single-purpose queries while answering far more kinds of question.
/// </summary>
public sealed partial class EisQueries
{
    private static readonly string TaxpayerNames = """
        SELECT TIN,
               MAX(BusinessName) AS BusinessName,
               MAX(LTRIM(RTRIM(TaxOfficeCode))) AS TaxOffice
        FROM eis_staging.Taxpayers
        WHERE DeletedOn IS NULL
        GROUP BY TIN
        """;

    // ---------------------------------------------------------------- finding taxpayers by name

    /// <summary>LIKE pattern in which every word must appear in order: "portland cement" finds "PORTLAND CEMENT (MALAWI) LTD".</summary>
    public static string NamePattern(string text) =>
        "%" + string.Join("%", text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\["))) + "%";

    /// <summary>Up to 10 registered taxpayers whose TIN equals, or whose business name contains, the text.</summary>
    public async Task<IReadOnlyList<(string Tin, string Name)>> FindTaxpayersAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        var byTin = text.All(char.IsDigit);
        var sql = $"""
            SELECT TOP (10) TIN, MAX(BusinessName) AS BusinessName
            FROM eis_staging.Taxpayers
            WHERE DeletedOn IS NULL AND {(byTin ? "TIN = @key" : "BusinessName LIKE @key ESCAPE '\\'")}
            GROUP BY TIN
            ORDER BY MAX(BusinessName);
            """;
        var key = byTin ? SqlRunner.VarChar("@key", text, 30) : SqlRunner.VarChar("@key", NamePattern(text), 200);
        var r = await _sql.QueryAsync("Taxpayer search", sql, [key], VisualKind.Table, ct);
        return r.Rows.Select(row => (Convert.ToString(row[0]) ?? "", Convert.ToString(row[1]) ?? "")).ToList();
    }

    // ---------------------------------------------------------------- the general sales and VAT lookup

    public async Task<QueryResult> InvoiceAnalysisAsync(DateOnly from, DateOnly to, InvoiceGrouping by, InvoiceSort sort, int top,
        InvoiceFilter filter, CancellationToken ct)
    {
        var parameters = new List<SqlParameter>(Window(from, to)) { SqlRunner.Int("@top", top) };
        var where = $"{InvoiceWindow} AND {NotRecalled}{FilterSql(filter, parameters)}";
        var order = sort switch { InvoiceSort.Sales => "GrossSales", InvoiceSort.Invoices => "Invoices", _ => "VAT" };
        const string measures = """
                   COUNT_BIG(*)                   AS Invoices,
                   SUM(ISNULL(i.InvoiceTotal, 0)) AS GrossSales,
                   SUM(ISNULL(i.TotalVAT, 0))     AS VAT
            """;

        string sql;
        VisualKind visual;
        string label;
        switch (by)
        {
            case InvoiceGrouping.Day:
            case InvoiceGrouping.Week:
            case InvoiceGrouping.Month:
                var (key, alias) = by switch
                {
                    InvoiceGrouping.Day => ("CAST(i.InvoiceDateTime AS date)", "[Date]"),
                    // Monday of the week, whatever the server's first day of the week is.
                    InvoiceGrouping.Week => ("DATEADD(DAY, -((DATEPART(WEEKDAY, i.InvoiceDateTime) + @@DATEFIRST + 5) % 7), CAST(i.InvoiceDateTime AS date))", "WeekStarting"),
                    _ => ("DATEFROMPARTS(YEAR(i.InvoiceDateTime), MONTH(i.InvoiceDateTime), 1)", "[Month]")
                };
                sql = $"""
                    SELECT {key} AS {alias},
                    {measures},
                           COUNT(DISTINCT i.SellerTIN)    AS Sellers
                    FROM eis_staging.Invoices i
                    WHERE {where}
                    GROUP BY {key}
                    ORDER BY {alias};
                    """;
                visual = by == InvoiceGrouping.Month ? VisualKind.Bar : VisualKind.Line;
                label = by switch { InvoiceGrouping.Day => "by day", InvoiceGrouping.Week => "by week", _ => "by month" };
                break;

            case InvoiceGrouping.Taxpayer:
                sql = $"""
                    WITH s AS (
                        SELECT TOP (@top) i.SellerTIN,
                    {measures}
                        FROM eis_staging.Invoices i
                        WHERE {where}
                        GROUP BY i.SellerTIN
                        ORDER BY {order} DESC
                    ), n AS ({TaxpayerNames})
                    SELECT s.SellerTIN AS TIN, n.BusinessName, n.TaxOffice, s.Invoices, s.GrossSales, s.VAT
                    FROM s LEFT JOIN n ON n.TIN = s.SellerTIN
                    ORDER BY s.{order} DESC;
                    """;
                visual = VisualKind.Bar;
                label = $"top {top} taxpayers";
                break;

            case InvoiceGrouping.TaxOffice:
                sql = $"""
                    WITH s AS (
                        SELECT i.SellerTIN,
                    {measures}
                        FROM eis_staging.Invoices i
                        WHERE {where}
                        GROUP BY i.SellerTIN
                    ), n AS ({TaxpayerNames})
                    SELECT ISNULL(n.TaxOffice, '(seller not in register)') AS TaxOffice,
                           COUNT(*) AS Sellers, SUM(s.Invoices) AS Invoices, SUM(s.GrossSales) AS GrossSales, SUM(s.VAT) AS VAT
                    FROM s LEFT JOIN n ON n.TIN = s.SellerTIN
                    GROUP BY ISNULL(n.TaxOffice, '(seller not in register)')
                    ORDER BY {order} DESC;
                    """;
                visual = VisualKind.Bar;
                label = "by tax office";
                break;

            case InvoiceGrouping.Site:
                sql = $"""
                    WITH s AS (
                        SELECT TOP (@top) i.SellerTIN, LTRIM(RTRIM(i.SiteId)) AS SiteId,
                    {measures}
                        FROM eis_staging.Invoices i
                        WHERE {where}
                        GROUP BY i.SellerTIN, LTRIM(RTRIM(i.SiteId))
                        ORDER BY {order} DESC
                    ), n AS ({TaxpayerNames})
                    SELECT s.SiteId, site.SiteName, s.SellerTIN AS TIN, n.BusinessName, s.Invoices, s.GrossSales, s.VAT
                    FROM s
                    LEFT JOIN n ON n.TIN = s.SellerTIN
                    OUTER APPLY (SELECT TOP (1) ts.SiteName FROM eis_staging.TerminalSites ts
                                 WHERE LTRIM(RTRIM(ts.SiteID)) = s.SiteId) site
                    ORDER BY s.{order} DESC;
                    """;
                visual = VisualKind.Bar;
                label = $"top {top} business sites";
                break;

            case InvoiceGrouping.Terminal:
                sql = $"""
                    WITH s AS (
                        SELECT TOP (@top) i.SellerTIN, LTRIM(RTRIM(i.TerminalId)) AS TerminalId,
                    {measures},
                               MAX(i.InvoiceDateTime) AS LastInvoice
                        FROM eis_staging.Invoices i
                        WHERE {where}
                        GROUP BY i.SellerTIN, LTRIM(RTRIM(i.TerminalId))
                        ORDER BY {order} DESC
                    ), n AS ({TaxpayerNames})
                    SELECT s.TerminalId, s.SellerTIN AS TIN, n.BusinessName, s.Invoices, s.GrossSales, s.VAT, s.LastInvoice
                    FROM s LEFT JOIN n ON n.TIN = s.SellerTIN
                    ORDER BY s.{order} DESC;
                    """;
                visual = VisualKind.Bar;
                label = $"top {top} terminals";
                break;

            case InvoiceGrouping.Buyer:
                sql = $"""
                    SELECT TOP (@top) ISNULL(NULLIF(LTRIM(RTRIM(i.BuyerTIN)), ''), '(no buyer TIN)') AS BuyerTIN,
                           MAX(i.BuyerName) AS BuyerName,
                    {measures},
                           COUNT(DISTINCT i.SellerTIN)    AS Sellers
                    FROM eis_staging.Invoices i
                    WHERE {where}
                    GROUP BY ISNULL(NULLIF(LTRIM(RTRIM(i.BuyerTIN)), ''), '(no buyer TIN)')
                    ORDER BY {order} DESC;
                    """;
                visual = VisualKind.Bar;
                label = $"top {top} buyers";
                break;

            case InvoiceGrouping.PaymentMethod:
                sql = $"""
                    SELECT ISNULL(NULLIF(LTRIM(RTRIM(i.PaymentMethod)), ''), '(not recorded)') AS PaymentMethod,
                    {measures}
                    FROM eis_staging.Invoices i
                    WHERE {where}
                    GROUP BY ISNULL(NULLIF(LTRIM(RTRIM(i.PaymentMethod)), ''), '(not recorded)')
                    ORDER BY {order} DESC;
                    """;
                visual = VisualKind.Pie;
                label = "by payment method";
                break;

            default:
                sql = $"""
                    SELECT {measures},
                           COUNT(DISTINCT i.SellerTIN)    AS Sellers
                    FROM eis_staging.Invoices i
                    WHERE {where};
                    """;
                visual = VisualKind.Table;
                label = "total";
                break;
        }

        var result = await _sql.QueryAsync($"Sales and VAT {label}{Scope(filter)}, {Range(from, to)} (MWK)", sql, parameters, visual, ct,
            note: RecalledNote + KindNote(filter.Kind), chartValueColumns: [order == "Invoices" ? "Invoices" : order]);
        var officeColumn = result.Columns.ToList().FindIndex(c => c == "TaxOffice");
        return officeColumn >= 0 ? LabelTaxOffices(result, officeColumn) : result;
    }

    // ---------------------------------------------------------------- two periods side by side

    public async Task<QueryResult> ComparePeriodsAsync(DateOnly from1, DateOnly to1, DateOnly from2, DateOnly to2,
        CompareGrouping by, bool biggestChanges, int top, InvoiceFilter filter, CancellationToken ct)
    {
        var parameters = new List<SqlParameter>
        {
            SqlRunner.Date("@from1", from1), SqlRunner.Date("@to1Ex", to1.AddDays(1)),
            SqlRunner.Date("@from2", from2), SqlRunner.Date("@to2Ex", to2.AddDays(1)),
            SqlRunner.Int("@top", top),
        };
        var filterSql = FilterSql(filter, parameters);
        const string in1 = "i.InvoiceDateTime >= @from1 AND i.InvoiceDateTime < @to1Ex";
        const string in2 = "i.InvoiceDateTime >= @from2 AND i.InvoiceDateTime < @to2Ex";
        var sums = $"""
                   SUM(CASE WHEN {in1} THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END) AS GrossSales1,
                   SUM(CASE WHEN {in2} THEN ISNULL(i.InvoiceTotal, 0) ELSE 0 END) AS GrossSales2,
                   SUM(CASE WHEN {in1} THEN ISNULL(i.TotalVAT, 0) ELSE 0 END)     AS VAT1,
                   SUM(CASE WHEN {in2} THEN ISNULL(i.TotalVAT, 0) ELSE 0 END)     AS VAT2,
                   SUM(CASE WHEN {in1} THEN 1 ELSE 0 END)                         AS Invoices1,
                   SUM(CASE WHEN {in2} THEN 1 ELSE 0 END)                         AS Invoices2
            """;
        var baseWhere = $"(({in1}) OR ({in2})) AND {NotRecalled}{filterSql}";
        const string changes = """
                   CASE WHEN x.GrossSales1 = 0 THEN NULL ELSE CAST((x.GrossSales2 - x.GrossSales1) * 100.0 / x.GrossSales1 AS decimal(18, 1)) END AS TurnoverChangePct,
                   CASE WHEN x.VAT1 = 0 THEN NULL ELSE CAST((x.VAT2 - x.VAT1) * 100.0 / x.VAT1 AS decimal(18, 1)) END AS VatChangePct
            """;
        var order = biggestChanges ? "ABS(x.VAT2 - x.VAT1) DESC" : "x.VAT2 DESC";

        string sql;
        switch (by)
        {
            case CompareGrouping.Taxpayer:
                sql = $"""
                    WITH x AS (
                        SELECT i.SellerTIN,
                    {sums}
                        FROM eis_staging.Invoices i
                        WHERE {baseWhere}
                        GROUP BY i.SellerTIN
                    ), n AS ({TaxpayerNames})
                    SELECT TOP (@top) x.SellerTIN AS TIN, n.BusinessName,
                           x.GrossSales1, x.GrossSales2, x.VAT1, x.VAT2, x.Invoices1, x.Invoices2,
                    {changes}
                    FROM x LEFT JOIN n ON n.TIN = x.SellerTIN
                    ORDER BY {order};
                    """;
                break;
            case CompareGrouping.TaxOffice:
                sql = $"""
                    WITH n AS ({TaxpayerNames}),
                    x AS (
                        SELECT ISNULL(n.TaxOffice, '(seller not in register)') AS TaxOffice,
                    {sums}
                        FROM eis_staging.Invoices i
                        LEFT JOIN n ON n.TIN = i.SellerTIN
                        WHERE {baseWhere}
                        GROUP BY ISNULL(n.TaxOffice, '(seller not in register)')
                    )
                    SELECT x.TaxOffice, x.GrossSales1, x.GrossSales2, x.VAT1, x.VAT2, x.Invoices1, x.Invoices2,
                    {changes}
                    FROM x
                    ORDER BY {order};
                    """;
                break;
            default:
                sql = $"""
                    WITH x AS (
                        SELECT
                    {sums}
                        FROM eis_staging.Invoices i
                        WHERE {baseWhere}
                    )
                    SELECT x.GrossSales1, x.GrossSales2, x.VAT1, x.VAT2, x.Invoices1, x.Invoices2,
                    {changes}
                    FROM x;
                    """;
                break;
        }

        var label = by switch { CompareGrouping.Taxpayer => biggestChanges ? $"taxpayers with the biggest VAT changes (top {top})" : $"top {top} taxpayers", CompareGrouping.TaxOffice => "by tax office", _ => "totals" };
        var result = await _sql.QueryAsync(
            $"Period 1 ({Range(from1, to1)}) against period 2 ({Range(from2, to2)}): {label}{Scope(filter)} (MWK)",
            sql, parameters, by == CompareGrouping.Total ? VisualKind.Table : VisualKind.Bar, ct,
            note: $"Columns ending in 1 are period 1 ({Range(from1, to1)}); columns ending in 2 are period 2 ({Range(from2, to2)}). " +
                  "Change columns are the percentage change from period 1 to period 2 (empty when period 1 is zero). " +
                  (from1.DayNumber - to1.DayNumber != from2.DayNumber - to2.DayNumber ? "The two periods have different lengths, so compare daily averages with care. " : "") +
                  RecalledNote + KindNote(filter.Kind),
            chartValueColumns: ["VAT2", "VAT1"]);
        var officeColumn = result.Columns.ToList().FindIndex(c => c == "TaxOffice");
        return officeColumn >= 0 ? LabelTaxOffices(result, officeColumn) : result;
    }

    // ---------------------------------------------------------------- sellers that stopped issuing invoices

    public async Task<QueryResult> InactiveSellersAsync(DateOnly activeFrom, DateOnly activeTo, DateOnly quietFrom, DateOnly quietTo,
        int top, InvoiceFilter filter, CancellationToken ct)
    {
        var parameters = new List<SqlParameter>
        {
            SqlRunner.Date("@aFrom", activeFrom), SqlRunner.Date("@aToEx", activeTo.AddDays(1)),
            SqlRunner.Date("@qFrom", quietFrom), SqlRunner.Date("@qToEx", quietTo.AddDays(1)),
            SqlRunner.Int("@top", top),
        };
        var filterSql = FilterSql(filter, parameters);
        var sql = $"""
            WITH a AS (
                SELECT i.SellerTIN, COUNT_BIG(*) AS Invoices,
                       SUM(ISNULL(i.InvoiceTotal, 0)) AS GrossSales, SUM(ISNULL(i.TotalVAT, 0)) AS VAT,
                       MAX(i.InvoiceDateTime) AS LastInvoice
                FROM eis_staging.Invoices i
                WHERE i.InvoiceDateTime >= @aFrom AND i.InvoiceDateTime < @aToEx AND {NotRecalled}{filterSql}
                GROUP BY i.SellerTIN
            ),
            q AS (
                SELECT DISTINCT i.SellerTIN
                FROM eis_staging.Invoices i
                WHERE i.InvoiceDateTime >= @qFrom AND i.InvoiceDateTime < @qToEx
            ),
            n AS ({TaxpayerNames})
            SELECT TOP (@top) a.SellerTIN AS TIN, n.BusinessName, n.TaxOffice, a.Invoices, a.GrossSales, a.VAT, a.LastInvoice,
                   COUNT(*) OVER () AS StoppedSellers
            FROM a
            LEFT JOIN n ON n.TIN = a.SellerTIN
            WHERE NOT EXISTS (SELECT 1 FROM q WHERE q.SellerTIN = a.SellerTIN)
            ORDER BY a.VAT DESC;
            """;

        var result = await _sql.QueryAsync(
            $"Sellers that issued invoices from {Range(activeFrom, activeTo)} but none from {Range(quietFrom, quietTo)}{Scope(filter)}",
            sql, parameters, VisualKind.Bar, ct, chartValueColumns: ["VAT"]);

        // Move the total count out of the table and into the title and note.
        var countColumn = result.Columns.ToList().IndexOf("StoppedSellers");
        var total = result.Rows.Count > 0 && countColumn >= 0 ? Convert.ToInt64(result.Rows[0][countColumn]) : 0;
        if (countColumn >= 0)
        {
            result = result with
            {
                Columns = result.Columns.Where((_, k) => k != countColumn).ToList(),
                Rows = result.Rows.Select(r => r.Where((_, k) => k != countColumn).ToArray()).ToList(),
            };
        }
        result = result with
        {
            Title = $"{total:#,0} sellers issued invoices from {Range(activeFrom, activeTo)} but none from {Range(quietFrom, quietTo)}{Scope(filter)}" +
                    (total > result.Rows.Count ? $" (largest {result.Rows.Count} by VAT shown)" : ""),
            Note = "Amounts are for the earlier period. A seller with no invoices in the later period may have closed, paused trading, " +
                   "or stopped issuing receipts through EIS; this is worth following up for the largest ones. " + RecalledNote,
        };
        var officeColumn = result.Columns.ToList().FindIndex(c => c == "TaxOffice");
        return officeColumn >= 0 ? LabelTaxOffices(result, officeColumn) : result;
    }

    // ---------------------------------------------------------------- the taxpayer register

    public async Task<QueryResult> TaxpayerRegisterAsync(TaxpayerListMode mode, string? name, IReadOnlyList<string>? offices,
        bool? vatRegistered, bool? blacklisted, DateOnly? registeredFrom, DateOnly? registeredTo,
        DateOnly activityFrom, DateOnly activityTo, int top, CancellationToken ct)
    {
        var parameters = new List<SqlParameter> { SqlRunner.Int("@top", top) };
        var conditions = new List<string> { "t.DeletedOn IS NULL" };
        var described = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var text = name.Trim();
            if (text.All(char.IsDigit)) { conditions.Add("t.TIN = @name"); parameters.Add(SqlRunner.VarChar("@name", text, 30)); }
            else { conditions.Add("t.BusinessName LIKE @name ESCAPE '\\'"); parameters.Add(SqlRunner.VarChar("@name", NamePattern(text), 200)); }
            described.Add($"matching '{text}'");
        }
        if (offices is { Count: > 0 })
        {
            var names = offices.Select((o, k) => { parameters.Add(SqlRunner.VarChar($"@o{k}", o, 50)); return $"@o{k}"; }).ToList();
            conditions.Add($"LTRIM(RTRIM(t.TaxOfficeCode)) IN ({string.Join(", ", names)})");
            described.Add("at " + string.Join(", ", offices.Select(o => _offices.Label(o))));
        }
        if (vatRegistered is not null) { conditions.Add(vatRegistered.Value ? "t.IsVATRegistered = 1" : "ISNULL(t.IsVATRegistered, 0) = 0"); described.Add(vatRegistered.Value ? "VAT-registered" : "not VAT-registered"); }
        if (blacklisted is not null) { conditions.Add(blacklisted.Value ? "t.IsBlackListed = 1" : "ISNULL(t.IsBlackListed, 0) = 0"); described.Add(blacklisted.Value ? "blacklisted" : "not blacklisted"); }
        if (registeredFrom is not null) { conditions.Add("t.Created_at >= @rFrom"); parameters.Add(SqlRunner.Date("@rFrom", registeredFrom.Value)); }
        if (registeredTo is not null) { conditions.Add("t.Created_at < @rToEx"); parameters.Add(SqlRunner.Date("@rToEx", registeredTo.Value.AddDays(1))); }
        if (registeredFrom is not null || registeredTo is not null)
            described.Add($"registered {(registeredFrom is null ? "up to" : registeredTo is null ? "from" : "")} {Range(registeredFrom ?? registeredTo!.Value, registeredTo ?? registeredFrom!.Value)}".Replace("  ", " "));
        var where = string.Join(" AND ", conditions);
        var scope = described.Count == 0 ? "" : " " + string.Join(", ", described);

        string sql;
        string title;
        VisualKind visual;
        IReadOnlyList<string>? chart;
        switch (mode)
        {
            case TaxpayerListMode.CountByOffice:
                sql = $"""
                    SELECT ISNULL(LTRIM(RTRIM(t.TaxOfficeCode)), '(no office)') AS TaxOffice,
                           COUNT(DISTINCT t.TIN) AS Taxpayers,
                           COUNT(DISTINCT CASE WHEN t.IsVATRegistered = 1 THEN t.TIN END) AS VatRegistered,
                           COUNT(DISTINCT CASE WHEN t.IsBlackListed = 1 THEN t.TIN END) AS Blacklisted
                    FROM eis_staging.Taxpayers t
                    WHERE {where}
                    GROUP BY ISNULL(LTRIM(RTRIM(t.TaxOfficeCode)), '(no office)')
                    ORDER BY Taxpayers DESC;
                    """;
                title = $"Registered taxpayers{scope} by tax office";
                visual = VisualKind.Bar; chart = ["Taxpayers"];
                break;
            case TaxpayerListMode.CountByMonth:
                sql = $"""
                    SELECT DATEFROMPARTS(YEAR(t.Created_at), MONTH(t.Created_at), 1) AS [Month],
                           COUNT(DISTINCT t.TIN) AS NewTaxpayers,
                           COUNT(DISTINCT CASE WHEN t.IsVATRegistered = 1 THEN t.TIN END) AS VatRegistered
                    FROM eis_staging.Taxpayers t
                    WHERE {where} AND t.Created_at IS NOT NULL
                    GROUP BY DATEFROMPARTS(YEAR(t.Created_at), MONTH(t.Created_at), 1)
                    ORDER BY [Month];
                    """;
                title = $"Taxpayers onboarded per month{scope}";
                visual = VisualKind.Bar; chart = ["NewTaxpayers"];
                break;
            default:
                parameters.AddRange(Window(activityFrom, activityTo));
                sql = $"""
                    WITH t1 AS (
                        SELECT t.TIN, MAX(t.BusinessName) AS BusinessName, MAX(LTRIM(RTRIM(t.TaxOfficeCode))) AS TaxOffice,
                               MAX(CAST(ISNULL(t.IsVATRegistered, 0) AS int)) AS IsVATRegistered,
                               MAX(CAST(ISNULL(t.IsBlackListed, 0) AS int)) AS IsBlackListed,
                               MIN(t.Created_at) AS RegisteredOn
                        FROM eis_staging.Taxpayers t
                        WHERE {where}
                        GROUP BY t.TIN
                    ),
                    inv AS (
                        SELECT i.SellerTIN, COUNT_BIG(*) AS Invoices, SUM(ISNULL(i.TotalVAT, 0)) AS VAT, MAX(i.InvoiceDateTime) AS LastInvoice
                        FROM eis_staging.Invoices i
                        WHERE {InvoiceWindow} AND {NotRecalled} AND i.SellerTIN IN (SELECT TIN FROM t1)
                        GROUP BY i.SellerTIN
                    )
                    SELECT TOP (@top) t1.TIN, t1.BusinessName, t1.TaxOffice,
                           CASE WHEN t1.IsVATRegistered = 1 THEN 'yes' ELSE 'no' END AS VatRegistered,
                           CASE WHEN t1.IsBlackListed = 1 THEN 'yes' ELSE 'no' END AS Blacklisted,
                           t1.RegisteredOn,
                           ISNULL(inv.Invoices, 0) AS Invoices, ISNULL(inv.VAT, 0) AS VAT, inv.LastInvoice,
                           COUNT(*) OVER () AS MatchingTaxpayers
                    FROM t1 LEFT JOIN inv ON inv.SellerTIN = t1.TIN
                    ORDER BY ISNULL(inv.VAT, 0) DESC, t1.BusinessName;
                    """;
                title = $"Registered taxpayers{scope}";
                visual = VisualKind.Table; chart = null;
                break;
        }

        var result = await _sql.QueryAsync(title, sql, parameters, visual, ct, chartValueColumns: chart,
            note: mode == TaxpayerListMode.List
                ? $"Invoices, VAT and LastInvoice cover {Range(activityFrom, activityTo)} only (recalled invoices excluded). Deleted taxpayers are left out."
                : "Deleted taxpayers are left out.");

        if (mode == TaxpayerListMode.List)
        {
            var countColumn = result.Columns.ToList().IndexOf("MatchingTaxpayers");
            var total = result.Rows.Count > 0 && countColumn >= 0 ? Convert.ToInt64(result.Rows[0][countColumn]) : 0;
            if (countColumn >= 0)
                result = result with
                {
                    Columns = result.Columns.Where((_, k) => k != countColumn).ToList(),
                    Rows = result.Rows.Select(r => r.Where((_, k) => k != countColumn).ToArray()).ToList(),
                };
            result = result with
            {
                Title = $"{total:#,0} registered taxpayer{(total == 1 ? "" : "s")}{scope}" +
                        (total > result.Rows.Count ? $" (first {result.Rows.Count} shown, largest VAT first)" : "")
            };
        }
        var officeColumn = result.Columns.ToList().FindIndex(c => c == "TaxOffice");
        return officeColumn >= 0 ? LabelTaxOffices(result, officeColumn) : result;
    }

    // ---------------------------------------------------------------- terminals (devices)

    public Task<QueryResult> TerminalStatisticsAsync(TerminalGrouping by, CancellationToken ct)
    {
        var (key, alias, label) = by switch
        {
            TerminalGrouping.Version => ("ISNULL(NULLIF(LTRIM(RTRIM(t.POSProductVersion)), ''), '(not set)')", "POSVersion", "by POS software version"),
            TerminalGrouping.ActivationMonth => ("DATEFROMPARTS(YEAR(t.ActivationDate), MONTH(t.ActivationDate), 1)", "[Month]", "activated per month"),
            TerminalGrouping.Status => ("CASE WHEN t.IsActive = 1 THEN 'Active' ELSE 'Not active' END", "Status", "by status"),
            _ => ("ISNULL(os.Name, '(not set)')", "OperatingSystem", "by operating system"),
        };
        var extraWhere = by == TerminalGrouping.ActivationMonth ? "WHERE t.ActivationDate IS NOT NULL" : "";
        var sql = $"""
            SELECT {key} AS {alias},
                   COUNT(*) AS Terminals,
                   SUM(CASE WHEN t.IsActive = 1 THEN 1 ELSE 0 END) AS ActiveTerminals,
                   SUM(CASE WHEN ISNULL(t.TamperAttempts, 0) > 0 THEN 1 ELSE 0 END) AS WithTamperAttempts
            FROM eis_staging.Terminal t
            LEFT JOIN eis_staging.TerminalOperatingSystems os ON os.ID = t.PlartformOS
            {extraWhere}
            GROUP BY {key}
            ORDER BY {(by == TerminalGrouping.ActivationMonth ? alias : "Terminals DESC")};
            """;
        return _sql.QueryAsync($"Fiscal terminals (POS devices) {label}", sql, [], VisualKind.Bar, ct, chartValueColumns: ["Terminals"]);
    }

    /// <summary>Active terminals that issued no invoice at all in the period.</summary>
    public async Task<QueryResult> QuietTerminalsAsync(DateOnly from, DateOnly to, int top, CancellationToken ct)
    {
        var sql = $"""
            WITH used AS (
                SELECT DISTINCT LTRIM(RTRIM(i.TerminalId)) AS TerminalId
                FROM eis_staging.Invoices i
                WHERE {InvoiceWindow} AND i.TerminalId IS NOT NULL
            )
            SELECT TOP (@top) t.TerminalID, t.TerminalLabel, t.ActivationDate, os.Name AS OperatingSystem, t.POSProductVersion,
                   COUNT(*) OVER () AS QuietTerminals
            FROM eis_staging.Terminal t
            LEFT JOIN eis_staging.TerminalOperatingSystems os ON os.ID = t.PlartformOS
            WHERE t.IsActive = 1
              AND NOT EXISTS (SELECT 1 FROM used u WHERE u.TerminalId = LTRIM(RTRIM(t.TerminalID)))
            ORDER BY t.ActivationDate DESC;
            """;
        SqlParameter[] ps = [.. Window(from, to), SqlRunner.Int("@top", top)];
        var result = await _sql.QueryAsync("Quiet terminals", sql, ps, VisualKind.Table, ct);
        var countColumn = result.Columns.ToList().IndexOf("QuietTerminals");
        var total = result.Rows.Count > 0 && countColumn >= 0 ? Convert.ToInt64(result.Rows[0][countColumn]) : 0;
        if (countColumn >= 0)
            result = result with
            {
                Columns = result.Columns.Where((_, k) => k != countColumn).ToList(),
                Rows = result.Rows.Select(r => r.Where((_, k) => k != countColumn).ToArray()).ToList(),
            };
        return result with
        {
            Title = $"{total:#,0} active terminals issued no invoices from {Range(from, to)}" +
                    (total > result.Rows.Count ? $" (most recently activated {result.Rows.Count} shown)" : ""),
            Note = "Terminals are marked active in the register but no invoice from them was found in the period. " +
                   "The register does not record which taxpayer owns a terminal, so owners cannot be shown.",
        };
    }

    // ---------------------------------------------------------------- recalled invoices by taxpayer

    public Task<QueryResult> RecalledByTaxpayerAsync(DateOnly from, DateOnly to, int top, CancellationToken ct)
    {
        var sql = $"""
            WITH s AS (
                SELECT TOP (@top) i.SellerTIN,
                       COUNT_BIG(*) AS RecalledInvoices,
                       SUM(ISNULL(i.InvoiceTotal, 0)) AS RecalledValue
                FROM eis_staging.Invoices i
                WHERE {InvoiceWindow} AND i.IsRecalled = 1
                GROUP BY i.SellerTIN
                ORDER BY RecalledInvoices DESC
            ), n AS ({TaxpayerNames})
            SELECT s.SellerTIN AS TIN, n.BusinessName, s.RecalledInvoices, s.RecalledValue
            FROM s LEFT JOIN n ON n.TIN = s.SellerTIN
            ORDER BY s.RecalledInvoices DESC;
            """;
        SqlParameter[] ps = [.. Window(from, to), SqlRunner.Int("@top", top)];
        return _sql.QueryAsync($"Taxpayers with the most recalled invoices (top {top}), {Range(from, to)} (MWK)", sql, ps, VisualKind.Bar, ct,
            note: "Filtered on the original invoice date. Frequent recalls are worth checking, because cancelling receipts can hide sales.",
            chartValueColumns: ["RecalledInvoices"]);
    }

    // ---------------------------------------------------------------- shared pieces

    private static string FilterSql(InvoiceFilter f, List<SqlParameter> parameters)
    {
        var sql = "";
        if (f.Tins is { Count: > 0 })
        {
            var names = f.Tins.Select((t, k) => { parameters.Add(SqlRunner.VarChar($"@tin{k}", t, 30)); return $"@tin{k}"; });
            sql += $" AND i.SellerTIN IN ({string.Join(", ", names)})";
        }
        if (f.Offices is { Count: > 0 })
        {
            var names = f.Offices.Select((o, k) => { parameters.Add(SqlRunner.VarChar($"@off{k}", o, 50)); return $"@off{k}"; });
            sql += $" AND i.SellerTIN IN (SELECT TIN FROM eis_staging.Taxpayers WHERE DeletedOn IS NULL AND LTRIM(RTRIM(TaxOfficeCode)) IN ({string.Join(", ", names)}))";
        }
        sql += f.Kind switch
        {
            InvoiceKind.Export => " AND i.IsExport = 1",
            InvoiceKind.Relief => " AND i.IsReliefSupply = 1",
            InvoiceKind.Domestic => " AND ISNULL(i.IsExport, 0) = 0",
            _ => ""
        };
        return sql;
    }

    private static string Scope(InvoiceFilter f) =>
        (string.IsNullOrWhiteSpace(f.ScopeLabel) ? "" : $" for {f.ScopeLabel}") +
        f.Kind switch { InvoiceKind.Export => ", export sales only", InvoiceKind.Relief => ", relief supplies only", InvoiceKind.Domestic => ", domestic sales only", _ => "" };

    private static string KindNote(InvoiceKind kind) => kind switch
    {
        InvoiceKind.Export => " Only invoices marked as exports are included.",
        InvoiceKind.Relief => " Only invoices marked as relief supplies are included.",
        InvoiceKind.Domestic => " Invoices marked as exports are left out.",
        _ => ""
    };
}
