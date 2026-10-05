using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MraReporting.Ai;
using MraReporting.Data;
using MraReporting.Infrastructure;

namespace MraReporting.Reports;

/// <summary>
/// Summary report for a day or a period. C# runs a fixed list of queries, ScottPlot draws the charts,
/// and ReportExplainer writes the summary and the explained findings from those figures, so every
/// number in the text is exact and the report never depends on the AI model being available.
/// </summary>
public sealed class DailySummaryReport
{
    private readonly EisQueries _queries;
    private readonly AppClock _clock;
    private readonly ILogger<DailySummaryReport> _logger;

    public DailySummaryReport(EisQueries queries, AppClock clock, ILogger<DailySummaryReport> logger)
    {
        _queries = queries;
        _clock = clock;
        _logger = logger;
    }

    public Task<ReportDocument> BuildAsync(DateOnly date, string requestedBy, CancellationToken ct) =>
        BuildAsync(date, date, requestedBy, ct);

    /// <summary>
    /// A summary for one day (compared with the previous day and the 30-day daily average) or for any
    /// period (compared with the previous period of the same length, plus the daily average).
    /// </summary>
    public async Task<ReportDocument> BuildAsync(DateOnly from, DateOnly to, string requestedBy, CancellationToken ct)
    {
        var single = from == to;
        var days = to.DayNumber - from.DayNumber + 1;
        var period = single ? from.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)
                            : $"{from:d MMMM yyyy} to {to:d MMMM yyyy} ({days} days)";
        var periodWords = single ? "the report date" : "the report period";

        // One query at a time keeps the load on the EIS server predictable.
        var kpis = await _queries.KpisAsync(from, to, ct);
        var trend = single
            ? await _queries.SalesTotalsAsync(from.AddDays(-29), to, Period.Day, ct)
            : await _queries.SalesTotalsAsync(from, to, days <= 62 ? Period.Day : Period.Month, ct);
        var monthlyTrend = !single && days > 62;
        var previousKpis = single ? null : await _queries.KpisAsync(from.AddDays(-days), from.AddDays(-1), ct);
        var vatByRate = await _queries.VatByRateAsync(from, to, ct);
        var byOffice = await _queries.SalesByTaxOfficeAsync(from, to, null, ct);
        var topTaxpayers = await _queries.TopTaxpayersAsync(from, to, 10, TaxpayerMetric.Vat, ct);
        var flags = await _queries.RedFlagsAsync(from, to, FlagGrouping.Type, ct);
        var failures = await _queries.FailedTransactionsAsync(from, to, FailureGrouping.Taxpayer, 10, ct);
        QueryResult? customs = null;
        try
        {
            customs = await _queries.CustomsCollectionsAsync(from, to, [], CustomsGrouping.Station, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Customs section skipped: the ePayment customs table could not be read.");
        }

        // ---------------- headline figures with comparisons
        decimal Kpi(string column) => Value(kpis, 0, column);
        var invoices = Kpi("Invoices");
        var gross = Kpi("GrossSales");
        var vat = Kpi("VAT");

        List<ReportKpi> kpiList;
        IReadOnlyList<string> kpiHeaders;
        decimal averageGross;
        if (single)
        {
            var date = from;
            var previousRow = FindRow(trend, date.AddDays(-1));
            var otherDays = trend.Rows.Where(r => r[0] is not DateOnly d || d != date).ToList();
            decimal Avg(string column)
            {
                var i = Index(trend, column);
                return i < 0 || otherDays.Count == 0 ? 0 : otherDays.Average(r => ToDecimal(r[i]));
            }
            string Prev(string column, bool money)
            {
                var i = Index(trend, column);
                return previousRow is null || i < 0 ? "" : Fmt(ToDecimal(previousRow[i]), money);
            }

            kpiHeaders = ["Measure", "Report date", "Previous day", "30-day daily average", "Change vs average"];
            kpiList =
            [
                Compare("Invoices", invoices, Prev("Invoices", false), Avg("Invoices"), money: false),
                Compare("Gross sales (MWK)", gross, Prev("GrossSales", true), Avg("GrossSales"), money: true),
                Compare("VAT (MWK)", vat, Prev("VAT", true), Avg("VAT"), money: true),
            ];
            averageGross = Avg("GrossSales");
        }
        else
        {
            decimal PrevKpi(string column) => previousKpis is null ? 0 : Value(previousKpis, 0, column);
            ReportKpi PeriodKpi(string label, string column, bool money)
            {
                var value = Kpi(column);
                var previous = PrevKpi(column);
                var change = previous > 0 ? ((value - previous) / previous).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture) : "";
                return new ReportKpi(label, Fmt(value, money), previous > 0 ? Fmt(previous, money) : "", Fmt(value / days, money), change);
            }

            kpiHeaders = ["Measure", "Report period", "Previous period", "Daily average", "Change vs previous"];
            kpiList =
            [
                PeriodKpi("Invoices", "Invoices", money: false),
                PeriodKpi("Gross sales (MWK)", "GrossSales", money: true),
                PeriodKpi("VAT (MWK)", "VAT", money: true),
            ];
            averageGross = PrevKpi("GrossSales");
        }

        kpiList.Add(new("Active sellers", Fmt(Kpi("ActiveSellers"), false)));
        kpiList.Add(new("Recalled invoices", Fmt(Kpi("RecalledInvoices"), false)));
        kpiList.Add(new("Export sales (MWK)", Fmt(Kpi("ExportSales"), true)));
        kpiList.Add(new("Relief supply sales (MWK)", Fmt(Kpi("ReliefSupplySales"), true)));
        if (customs is { Rows.Count: > 0 })
        {
            kpiList.Add(new("Customs payments recorded in ePayment, paid (MWK)", Fmt(Sum(customs, "PaidAmount"), true)));
            kpiList.Add(new("Customs liabilities assessed (MWK)", Fmt(Sum(customs, "AssessedAmount"), true)));
        }

        // ---------------- no data for this period: say so plainly, with the dates that do have data
        string? banner = null;
        if (invoices == 0)
        {
            var coverage = await SafeCoverageAsync(ct);
            banner = $"No EIS invoices are recorded for {period}. {coverage} Choose dates inside that range for a full report.";
        }

        // ---------------- summary and findings, worked out in code from the figures above
        var (summaryText, findings) = ReportExplainer.Explain(new ReportFigures(
            from, to, kpis, previousKpis, trend, monthlyTrend, vatByRate, byOffice, topTaxpayers, customs, flags, failures));
        var narrative = invoices == 0
            ? banner!
            : summaryText.Length > 0 ? summaryText : TemplateNarrative(period, gross, vat, averageGross, single);

        // ---------------- sections
        DateOnly? highlight = single ? from : null;
        var sections = new List<ReportSection>
        {
            new(monthlyTrend ? "Monthly trend" : "Daily trend",
                single ? "Gross sales and VAT for each of the last 30 days. The dark green line marks the report date."
                       : monthlyTrend ? "Gross sales and VAT for each month of the report period."
                                      : "Gross sales and VAT for each day of the report period.",
                trend,
                Charts(ChartRenderer.TimeSeries(trend, "GrossSales", monthlyTrend ? "Gross sales per month" : "Gross sales per day", ChartRenderer.Green, asBars: true, highlight: highlight),
                       ChartRenderer.TimeSeries(trend, "VAT", monthlyTrend ? "VAT per month" : "VAT per day", ChartRenderer.Green, asBars: monthlyTrend, highlight: highlight)),
                TableInAppendix: trend.Rows.Count > 12),
            new("VAT by tax rate",
                $"VAT collected in {periodWords}, split by tax rate. Rate charged is the VAT divided by the taxable amount. Codes without a name on MRA's tax rate list are shown as stored in EIS.",
                vatByRate,
                Charts(ChartRenderer.Ranking(vatByRate, "VAT", "Share of VAT by tax rate", showShare: true))),
            new("Sales and VAT by tax office",
                "Each invoice counts at the tax office where the seller is registered. Offices are shown by code where no official name is on file.",
                byOffice,
                Charts(ChartRenderer.Ranking(byOffice, "VAT", "VAT by seller's tax office", showShare: true))),
            new("Top 10 taxpayers by VAT",
                $"The sellers that charged the most VAT in {periodWords}.",
                topTaxpayers,
                Charts(ChartRenderer.Ranking(topTaxpayers, "VAT", "Top 10 taxpayers by VAT", showShare: false))),
        };
        if (customs is not null)
            sections.Add(new("Customs payments by station",
                $"Customs liabilities assessed in {periodWords} through ePayment. Paid means an ASYCUDA receipt was recorded. Payments made outside ePayment are not included.",
                customs,
                Charts(ChartRenderer.Ranking(customs, "PaidAmount", "Customs payments by station (paid)", showShare: false))));
        sections.Add(new("Flagged transactions",
            $"Transactions flagged by EIS risk rules in {periodWords}, by flag type.",
            flags,
            Charts(ChartRenderer.Ranking(flags, "RedFlagged", "Red-flagged transactions by type", showShare: false))));
        sections.Add(new("Failed invoice submissions",
            $"Taxpayers with the most failed submissions in {periodWords}. A failed submission may later be retried successfully.",
            failures,
            Charts(ChartRenderer.Ranking(failures, "Failures", "Failed submissions by taxpayer", showShare: false))));

        return new ReportDocument(
            Title: single ? "MRA EIS Daily Summary" : "MRA EIS Summary Report",
            Subtitle: period,
            GeneratedAt: _clock.Now,
            GeneratedBy: requestedBy,
            Narrative: narrative,
            Banner: banner,
            KpiHeaders: kpiHeaders,
            Kpis: kpiList,
            Sections: sections,
            Notes:
            [
                "Source: STAGING_SSIS (EIS invoices and ePayment customs liabilities). Figures reflect the data as of the last SSIS load.",
                "Recalled invoices are excluded from sales and VAT; a NULL recall flag counts as not recalled.",
                "Tax rate codes and tax office codes without a name on MRA's lists are shown as stored in EIS.",
                "Customs figures cover payments made through ePayment only and are not official customs collections.",
                "The summary and findings are worked out by the app from the figures in this report. Amounts in the text are rounded for reading; the tables give exact figures.",
            ],
            Findings: findings,
            Branded: true);
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<byte[]> Charts(params byte[]?[] charts) => charts.Where(c => c is not null).Select(c => c!).ToList();

    private static ReportKpi Compare(string label, decimal value, string previous, decimal average, bool money)
    {
        var change = average > 0 ? ((value - average) / average).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture) : "";
        return new ReportKpi(label, Fmt(value, money), previous, average > 0 ? Fmt(average, money) : "", change);
    }

    private static string Fmt(decimal value, bool money) =>
        value.ToString(money ? "#,0.00" : "#,0", CultureInfo.InvariantCulture);

    private static int Index(QueryResult r, string column) => r.Columns.ToList().FindIndex(c => c == column);

    private static decimal Value(QueryResult r, int row, string column)
    {
        var i = Index(r, column);
        return i < 0 || r.Rows.Count <= row ? 0 : ToDecimal(r.Rows[row][i]);
    }

    private static decimal Sum(QueryResult r, string column)
    {
        var i = Index(r, column);
        return i < 0 ? 0 : r.Rows.Sum(row => ToDecimal(row[i]));
    }

    private static decimal ToDecimal(object? v) =>
        v is null ? 0 : ReportText.IsNumber(v) ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : 0;

    private static object?[]? FindRow(QueryResult r, DateOnly date) =>
        r.Rows.FirstOrDefault(row => row[0] is DateOnly d && d == date);

    private async Task<string> SafeCoverageAsync(CancellationToken ct)
    {
        try { return await _queries.CoverageSentenceAsync("invoices", ct) ?? ""; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the invoice date range for the report banner.");
            return "";
        }
    }

    private static string TemplateNarrative(string period, decimal gross, decimal vat, decimal comparison, bool single)
    {
        var s = $"For {period}, EIS recorded gross sales of MWK {gross:#,0.00} with VAT of MWK {vat:#,0.00}.";
        if (comparison > 0)
            s += $" Gross sales were {Math.Abs((gross - comparison) / comparison):0.0%} {(gross >= comparison ? "above" : "below")} " +
                 (single ? "the 30-day daily average." : "the previous period of the same length.");
        return s;
    }
}
