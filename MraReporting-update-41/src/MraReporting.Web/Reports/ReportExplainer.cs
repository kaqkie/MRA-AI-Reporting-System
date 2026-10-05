using System.Globalization;
using MraReporting.Data;

namespace MraReporting.Reports;

/// <summary>One explained finding in a report: a short heading and a few plain sentences.</summary>
public sealed record ReportParagraph(string Heading, string Text);

/// <summary>Everything the explainer needs, as already queried for the report.</summary>
public sealed record ReportFigures(
    DateOnly From,
    DateOnly To,
    QueryResult Kpis,
    QueryResult? PreviousKpis,      // same-length previous period (null for a single day)
    QueryResult Trend,              // daily rows (or monthly for long periods); last 30 days for a single day
    bool MonthlyTrend,
    QueryResult VatByRate,
    QueryResult ByOffice,
    QueryResult TopTaxpayers,
    QueryResult? Customs,
    QueryResult Flags,
    QueryResult Failures);

/// <summary>
/// Writes the report's summary and its "What the figures show" findings in plain language.
/// Everything is worked out here in code from the queried figures, so every number is exact and
/// every comparison is arithmetic, not a guess. Each finding says what happened and what it means.
/// </summary>
public static class ReportExplainer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (string Summary, IReadOnlyList<ReportParagraph> Findings) Explain(ReportFigures f)
    {
        var single = f.From == f.To;
        var days = f.To.DayNumber - f.From.DayNumber + 1;
        var when = single ? $"On {f.From.ToString("dddd d MMMM yyyy", Inv)}" : $"Between {Day(f.From)} and {Day(f.To)}";

        var invoices = Value(f.Kpis, "Invoices");
        var gross = Value(f.Kpis, "GrossSales");
        var vat = Value(f.Kpis, "VAT");
        var sellers = Value(f.Kpis, "ActiveSellers");
        var recalled = Value(f.Kpis, "RecalledInvoices");
        var exports = Value(f.Kpis, "ExportSales");
        var relief = Value(f.Kpis, "ReliefSupplySales");

        var findings = new List<ReportParagraph>();
        if (invoices == 0) return ("", findings);

        // ---------------- 1. overall activity
        var overall = $"{when}, {Count(sellers)} sellers issued {Count(invoices)} invoices worth {Money(gross)} in gross sales, " +
                      $"charging {Money(vat)} in VAT.";
        if (!single)
            overall += $" That is an average of {Count(invoices / days)} invoices and {Money(gross / days)} of sales per day.";
        overall += $" The average invoice was worth {Money(gross / invoices)}, and VAT came to {Pct(vat / Safe(gross))} of gross sales.";
        var noVatTaxable = ZeroVatTaxable(f.VatByRate);
        var allTaxable = Sum(f.VatByRate, "TaxableAmount");
        if (noVatTaxable > 0 && allTaxable > 0)
            overall += $" VAT is a smaller share of sales than the standard rate because {Pct(noVatTaxable / allTaxable)} of taxable sales " +
                       "fall under tax codes that carry no VAT (see the tax rate finding below).";
        findings.Add(new("Overall activity", overall));

        // ---------------- 2. comparison
        string comparisonSentence = "";
        if (!single && f.PreviousKpis is not null)
        {
            var pInvoices = Value(f.PreviousKpis, "Invoices");
            var pGross = Value(f.PreviousKpis, "GrossSales");
            var pVat = Value(f.PreviousKpis, "VAT");
            var prevFrom = f.From.AddDays(-days);
            var prevTo = f.From.AddDays(-1);
            if (pInvoices > 0)
            {
                var ci = Change(invoices, pInvoices);
                var cg = Change(gross, pGross);
                var cv = Change(vat, pVat);
                var text = $"Compared with the previous {days} days ({Day(prevFrom)} to {Day(prevTo)}), invoices {Moved(ci)}, " +
                           $"gross sales {Moved(cg)} (from {Money(pGross)} to {Money(gross)}) and VAT {Moved(cv)} (from {Money(pVat)} to {Money(vat)}).";
                var pAvg = pGross / pInvoices;
                var avg = gross / invoices;
                if (ci - cg > 0.05m)
                    text += $" Invoices grew faster than sales, so the average invoice fell from {Money(pAvg)} to {Money(avg)}: " +
                            "more, smaller transactions were recorded. This can mean more businesses or devices are now issuing EIS receipts, rather than higher spending.";
                else if (cg - ci > 0.05m)
                    text += $" Sales grew faster than the number of invoices, so the average invoice rose from {Money(pAvg)} to {Money(avg)}: " +
                            "the extra revenue came from larger transactions rather than more of them.";
                if (cv - cg > 0.03m)
                    text += " VAT grew faster than sales, meaning a larger share of sales was charged VAT at the standard rate.";
                else if (cg - cv > 0.03m)
                    text += " VAT grew more slowly than sales, meaning a larger share of sales carried no VAT (zero-rated or exempt).";
                findings.Add(new("Compared with the previous period", text));
                comparisonSentence = $" Compared with the previous {days} days, gross sales {Moved(cg)} and VAT {Moved(cv)}.";
            }
            else
            {
                findings.Add(new("Compared with the previous period",
                    $"There is no invoice data for the previous {days} days ({Day(prevFrom)} to {Day(prevTo)}), so no comparison can be made."));
            }
        }
        else if (single)
        {
            var others = f.Trend.Rows.Where(r => r.Length > 0 && r[0] is DateOnly d && d != f.From).ToList();
            var gi = Index(f.Trend, "GrossSales");
            var vi = Index(f.Trend, "VAT");
            var prev = f.Trend.Rows.FirstOrDefault(r => r.Length > 0 && r[0] is DateOnly d && d == f.From.AddDays(-1));
            if (others.Count > 0 && gi >= 0 && vi >= 0)
            {
                var avgGross = others.Average(r => Num(r[gi]));
                var avgVat = others.Average(r => Num(r[vi]));
                var text = $"Gross sales were {AboveBelow(gross, avgGross)} the daily average of the previous {others.Count} days ({Money(avgGross)}), " +
                           $"and VAT was {AboveBelow(vat, avgVat)} its daily average ({Money(avgVat)}).";
                if (prev is not null)
                    text += $" Against the day before ({Money(Num(prev[gi]))} of sales), sales {Moved(Change(gross, Num(prev[gi])))}.";
                if (f.From.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    text += " The report date is a weekend day; the day-by-day finding below compares weekday and weekend trading.";
                findings.Add(new("Compared with recent days", text));
                comparisonSentence = $" Sales were {AboveBelow(gross, avgGross)} the recent daily average.";
            }
        }

        // ---------------- 3. pattern over time
        var pattern = PatternText(f, single);
        if (pattern.Length > 0) findings.Add(new(f.MonthlyTrend ? "Month-by-month pattern" : "Day-by-day pattern", pattern));

        // ---------------- 4. tax rates
        var rateText = RateText(f.VatByRate, vat);
        if (rateText.Length > 0) findings.Add(new("VAT by tax rate", rateText));

        // ---------------- 5. tax offices
        var officeText = OfficeText(f.ByOffice);
        if (officeText.Length > 0) findings.Add(new("Tax offices", officeText));

        // ---------------- 6. largest taxpayers
        var (taxpayerText, topName, topShare, top10Share) = TaxpayerText(f.TopTaxpayers, vat);
        if (taxpayerText.Length > 0) findings.Add(new("Largest taxpayers", taxpayerText));

        // ---------------- 7. customs
        var customsText = CustomsText(f.Customs);
        if (customsText.Length > 0) findings.Add(new("Customs payments", customsText));

        // ---------------- 8. risks and data quality
        var riskText = RiskText(f, invoices, recalled, exports, relief, gross);
        if (riskText.Length > 0) findings.Add(new("Risks and points to check", riskText));

        // ---------------- the short summary at the top
        var summary = $"{when}, {Count(invoices)} invoices from {Count(sellers)} sellers recorded {Money(gross)} in gross sales and {Money(vat)} in VAT." +
                      comparisonSentence;
        if (topName is not null)
            summary += $" The largest contributor was {topName}, with {Pct(topShare)} of all VAT" +
                       (top10Share > 0 ? $", and the ten largest taxpayers together accounted for {Pct(top10Share)}." : ".");
        var red = Sum(f.Flags, "RedFlagged");
        if (red > 0) summary += $" {Count(red)} transactions were red-flagged by EIS risk rules and should be reviewed.";
        summary += " The findings below explain each part of the report.";

        return (summary, findings);
    }

    // ---------------------------------------------------------------- findings

    private static string PatternText(ReportFigures f, bool single)
    {
        var gi = Index(f.Trend, "GrossSales");
        if (gi < 0) return "";
        var rows = f.Trend.Rows.Where(r => r.Length > gi && r[0] is DateOnly).ToList();
        if (single) rows = rows.Where(r => r[0] is DateOnly d && d >= f.From.AddDays(-29) && d <= f.To).ToList();
        if (rows.Count < 2) return "";

        var best = rows.OrderByDescending(r => Num(r[gi])).First();
        var worst = rows.OrderBy(r => Num(r[gi])).First();
        var bestDate = (DateOnly)best[0]!;
        var worstDate = (DateOnly)worst[0]!;

        if (f.MonthlyTrend)
        {
            return $"The strongest month was {bestDate.ToString("MMMM yyyy", Inv)} with {Money(Num(best[gi]))} of gross sales, " +
                   $"and the weakest was {worstDate.ToString("MMMM yyyy", Inv)} with {Money(Num(worst[gi]))}. " +
                   "The monthly chart in section 1 shows how sales moved across the period.";
        }

        var scope = single ? "Over the last 30 days" : "Within the period";
        var text = $"{scope}, the busiest day was {bestDate.ToString("dddd d MMMM", Inv)} with {Money(Num(best[gi]))} of gross sales, " +
                   $"and the quietest was {worstDate.ToString("dddd d MMMM", Inv)} with {Money(Num(worst[gi]))}.";

        var weekdays = rows.Where(r => ((DateOnly)r[0]!).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();
        var weekends = rows.Where(r => ((DateOnly)r[0]!).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday).ToList();
        if (weekdays.Count > 0 && weekends.Count > 0)
        {
            var wd = weekdays.Average(r => Num(r[gi]));
            var we = weekends.Average(r => Num(r[gi]));
            if (wd > 0)
                text += $" Weekdays averaged {Money(wd)} of sales against {Money(we)} at weekends, " +
                        (we < wd ? $"so weekend trading was {Pct(1 - we / wd)} lower." : $"so weekend trading was {Pct(we / wd - 1)} higher.");
        }

        if (!single)
        {
            var expected = f.To.DayNumber - f.From.DayNumber + 1;
            if (rows.Count < expected)
                text += $" Invoices were recorded on {rows.Count} of the {expected} days; days with no invoices may point to gaps in the data load.";
        }
        return text;
    }

    private static string RateText(QueryResult rates, decimal totalVat)
    {
        var vi = Index(rates, "VAT");
        var ti = Index(rates, "TaxableAmount");
        var pi = Index(rates, "RateChargedPct");
        if (rates.Rows.Count == 0 || vi < 0 || ti < 0) return "";

        var allVat = Sum(rates, "VAT");
        var allTaxable = Sum(rates, "TaxableAmount");
        var top = rates.Rows.OrderByDescending(r => Num(r[vi])).First();
        var text = $"{Name(rates, top)} produced {Money(Num(top[vi]))} of VAT, {Pct(Num(top[vi]) / Safe(allVat))} of the VAT on the invoice tax lines.";
        if (pi >= 0 && Num(top[pi]) > 0)
            text += $" On those lines, VAT averaged {Num(top[pi]).ToString("0.0", Inv)}% of the taxable amount.";

        var zero = rates.Rows.Where(r => Num(r[vi]) == 0 && Num(r[ti]) > 0).ToList();
        if (zero.Count > 0)
        {
            var zeroTaxable = zero.Sum(r => Num(r[ti]));
            text += $" {string.Join(" and ", zero.Select(r => Name(rates, r)))} carried no VAT and covered {Money(zeroTaxable)} " +
                    $"({Pct(zeroTaxable / Safe(allTaxable))} of taxable sales); these are usually zero-rated or exempt supplies.";
        }
        return text;
    }

    private static string OfficeText(QueryResult offices)
    {
        var vi = Index(offices, "VAT");
        if (offices.Rows.Count == 0 || vi < 0) return "";
        var total = Sum(offices, "VAT");
        if (total <= 0) return "";
        var ordered = offices.Rows.OrderByDescending(r => Num(r[vi])).ToList();
        var top = ordered[0];
        var text = $"{Name(offices, top)} accounted for the most VAT, {Money(Num(top[vi]))} or {Pct(Num(top[vi]) / total)} of the total.";
        if (ordered.Count >= 3)
        {
            var top3 = ordered.Take(3).Sum(r => Num(r[vi]));
            text += $" The three largest offices together held {Pct(top3 / total)}, while the other {ordered.Count - 3} shared the remaining {Pct(1 - top3 / total)}.";
        }
        var unregistered = offices.Rows.FirstOrDefault(r => r.Length > 0 && r[0] is string s && s.StartsWith("(seller not in register", StringComparison.Ordinal));
        if (unregistered is not null && Num(unregistered[vi]) > 0)
            text += $" {Money(Num(unregistered[vi]))} of VAT came from sellers that are not in the taxpayer register, so it cannot be linked to an office; this is worth checking.";
        return text;
    }

    private static (string Text, string? TopName, decimal TopShare, decimal Top10Share) TaxpayerText(QueryResult top, decimal totalVat)
    {
        var vi = Index(top, "VAT");
        if (top.Rows.Count == 0 || vi < 0 || totalVat <= 0) return ("", null, 0, 0);
        var ordered = top.Rows.OrderByDescending(r => Num(r[vi])).ToList();
        var first = ordered[0];
        var firstName = Name(top, first);
        var firstShare = Num(first[vi]) / totalVat;
        var tenShare = ordered.Sum(r => Num(r[vi])) / totalVat;

        var text = $"{firstName} was the largest VAT contributor with {Money(Num(first[vi]))}, {Pct(firstShare)} of all VAT.";
        if (ordered.Count > 1)
            text += $" The {ordered.Count} largest taxpayers together charged {Pct(tenShare)} of all VAT.";
        text += tenShare >= 0.5m
            ? " Revenue is therefore concentrated in a small number of businesses: a change at any one of them would noticeably move the national figure."
            : " Revenue is spread across many businesses, so no single taxpayer dominates the total.";
        return (text, firstName, firstShare, ordered.Count > 1 ? tenShare : 0);
    }

    private static string CustomsText(QueryResult? customs)
    {
        if (customs is not { Rows.Count: > 0 }) return "";
        var assessed = Sum(customs, "AssessedAmount");
        var paid = Sum(customs, "PaidAmount");
        var liabilities = Sum(customs, "Liabilities");
        if (assessed <= 0) return "";
        var pi = Index(customs, "PaidAmount");
        var top = customs.Rows.OrderByDescending(r => Num(r[pi])).First();

        var text = $"{Count(liabilities)} customs liabilities worth {Money(assessed)} were assessed through ePayment, and {Money(paid)} of that " +
                   $"({Pct(paid / assessed)}) has an ASYCUDA receipt, meaning it was paid.";
        text += $" {Name(customs, top)} recorded the highest payments, {Money(Num(top[pi]))} or {Pct(Num(top[pi]) / Safe(paid))} of all paid amounts.";
        if (paid < assessed)
            text += $" The remaining {Money(assessed - paid)} has no receipt yet; it may be unpaid, or paid outside ePayment.";
        return text;
    }

    private static string RiskText(ReportFigures f, decimal invoices, decimal recalled, decimal exports, decimal relief, decimal gross)
    {
        var parts = new List<string>();

        var flagged = Sum(f.Flags, "Transactions");
        var red = Sum(f.Flags, "RedFlagged");
        if (flagged > 0)
        {
            var ri = Index(f.Flags, "RedFlagged");
            var s = $"EIS risk rules flagged {Count(flagged)} transactions, of which {Count(red)} were red-flagged.";
            if (ri >= 0 && red > 0)
            {
                var topFlag = f.Flags.Rows.OrderByDescending(r => Num(r[ri])).First();
                s += $" The most common red flag was \"{Name(f.Flags, topFlag)}\" ({Count(Num(topFlag[ri]))}).";
            }
            parts.Add(s);
        }

        var fi = Index(f.Failures, "Failures");
        if (f.Failures.Rows.Count > 0 && fi >= 0)
        {
            var total = f.Failures.Rows.Sum(r => Num(r[fi]));
            var worst = f.Failures.Rows.OrderByDescending(r => Num(r[fi])).First();
            parts.Add($"The {f.Failures.Rows.Count} taxpayers with the most failed invoice submissions had {Count(total)} failures between them; " +
                      $"{Name(f.Failures, worst)} had the most ({Count(Num(worst[fi]))}). Repeated failures usually mean a device or integration problem that the taxpayer should fix.");
        }

        if (recalled > 0)
            parts.Add($"{Count(recalled)} invoices were recalled ({Pct(recalled / Safe(invoices + recalled))} of all invoices issued) and are left out of the totals above.");
        if (exports > 0)
            parts.Add($"Export sales were {Money(exports)} ({Pct(exports / Safe(gross))} of gross sales).");
        if (relief > 0)
            parts.Add($"Sales marked as relief supplies came to {Money(relief)}.");

        return string.Join(" ", parts);
    }

    // ---------------------------------------------------------------- wording helpers

    /// <summary>MWK 97.33 billion, MWK 245.6 million, MWK 12,345.67 (readable; exact figures are in the tables).</summary>
    public static string Money(decimal v)
    {
        var a = Math.Abs(v);
        if (a >= 1_000_000_000m) return "MWK " + (v / 1_000_000_000m).ToString("#,0.00", Inv) + " billion";
        if (a >= 1_000_000m) return "MWK " + (v / 1_000_000m).ToString("#,0.0", Inv) + " million";
        return "MWK " + v.ToString("#,0.00", Inv);
    }

    private static string Count(decimal v) => Math.Round(v).ToString("#,0", Inv);
    private static string Pct(decimal v) => (v * 100).ToString("0.0", Inv) + "%";
    private static string Day(DateOnly d) => d.ToString("d MMMM yyyy", Inv);
    private static decimal Safe(decimal v) => v == 0 ? 1 : v;
    private static decimal Change(decimal now, decimal before) => before == 0 ? 0 : (now - before) / before;

    private static string Moved(decimal change) => change switch
    {
        > 0.005m => $"rose by {Pct(change)}",
        < -0.005m => $"fell by {Pct(-change)}",
        _ => "were almost unchanged"
    };

    private static string AboveBelow(decimal value, decimal average)
    {
        if (average <= 0) return "close to";
        var c = (value - average) / average;
        return c switch
        {
            > 0.005m => $"{Pct(c)} above",
            < -0.005m => $"{Pct(-c)} below",
            _ => "in line with"
        };
    }

    /// <summary>The display name of a row: business name, office, station, flag type or the first column.</summary>
    private static string Name(QueryResult r, object?[] row)
    {
        foreach (var column in new[] { "BusinessName", "Station", "TaxOffice", "FlagType", "RateID" })
        {
            var i = Index(r, column);
            if (i >= 0 && i < row.Length && row[i] is string s && s.Trim().Length > 0) return s.Trim();
        }
        return row.Length > 0 ? ReportText.Cell(r.Columns[0], row[0]) : "(unnamed)";
    }

    private static decimal ZeroVatTaxable(QueryResult rates)
    {
        var vi = Index(rates, "VAT");
        var ti = Index(rates, "TaxableAmount");
        return vi < 0 || ti < 0 ? 0 : rates.Rows.Where(r => Num(r[vi]) == 0).Sum(r => Num(r[ti]));
    }

    private static int Index(QueryResult r, string column) => r.Columns.ToList().FindIndex(c => c == column);

    private static decimal Value(QueryResult r, string column)
    {
        var i = Index(r, column);
        return i < 0 || r.Rows.Count == 0 ? 0 : Num(r.Rows[0][i]);
    }

    private static decimal Sum(QueryResult r, string column)
    {
        var i = Index(r, column);
        return i < 0 ? 0 : r.Rows.Sum(row => i < row.Length ? Num(row[i]) : 0);
    }

    private static decimal Num(object? v) =>
        v is not null && ReportText.IsNumber(v) ? Convert.ToDecimal(v, Inv) : 0;
}
