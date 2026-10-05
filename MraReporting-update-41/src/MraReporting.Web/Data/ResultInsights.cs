using System.Globalization;
using MraReporting.Reports;

namespace MraReporting.Data;

/// <summary>
/// Works out the key facts of a query result in code (totals, shares, gaps between the leaders,
/// highs and lows, averages), so the AI can explain a result in detail without doing arithmetic
/// itself. A 4B model is unreliable at sums and percentages; these facts are exact.
/// </summary>
public static class ResultInsights
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] PreferredValues = ["TerminalsUsed", "VAT", "GrossSales", "PaidAmount", "AssessedAmount", "Amount", "RedFlagged", "Failures", "Invoices", "Transactions", "Requests"];
    private static readonly string[] PreferredLabels = ["BusinessName", "Station", "TaxOffice", "FlagType", "RateID", "RecallType", "InvestigationStatus", "Status", "TerminalId"];

    public static IReadOnlyList<string> For(QueryResult r)
    {
        var facts = new List<string>();
        if (r.Rows.Count == 0 || r.Columns.Count == 0) return facts;

        var value = ValueColumn(r);
        if (value < 0) return facts;
        var vName = ReportText.Header(r.Columns[value]);
        var money = ReportText.IsMoney(r.Columns[value]);

        if (r.Rows.Count == 1)
        {
            // One row (a total or a profile): nothing to rank; derived ratios only.
            AddRatios(r, r.Rows[0], facts, "");
            return facts;
        }

        if (IsDate(r.Rows[0][0]))
        {
            // ---------------- a series over time
            var rows = r.Rows.Where(x => IsDate(x[0])).ToList();
            var total = rows.Sum(x => Num(x[value]));
            var monthly = r.Columns[0].Equals("Month", StringComparison.OrdinalIgnoreCase);
            var unit = monthly ? "month" : "day";
            facts.Add($"Total {vName} over the {rows.Count} {unit}s: {Fmt(total, money)}; average per {unit}: {Fmt(total / rows.Count, money)}.");
            var high = rows.OrderByDescending(x => Num(x[value])).First();
            var low = rows.OrderBy(x => Num(x[value])).First();
            facts.Add($"Highest {unit}: {When(high[0], monthly)} with {Fmt(Num(high[value]), money)}. Lowest {unit}: {When(low[0], monthly)} with {Fmt(Num(low[value]), money)}.");
            if (rows.Count >= 4)
            {
                var half = rows.Count / 2;
                var firstHalf = rows.Take(half).Average(x => Num(x[value]));
                var secondHalf = rows.Skip(rows.Count - half).Average(x => Num(x[value]));
                if (firstHalf > 0)
                    facts.Add($"Average per {unit} in the second half of the period was {Change(secondHalf, firstHalf)} the first half ({Fmt(secondHalf, money)} against {Fmt(firstHalf, money)}).");
            }
            if (!monthly && rows.Count >= 7)
            {
                var weekend = rows.Where(x => Day(x[0]) is DayOfWeek.Saturday or DayOfWeek.Sunday).ToList();
                var weekday = rows.Except(weekend).ToList();
                if (weekend.Count > 0 && weekday.Count > 0)
                {
                    var we = weekend.Average(x => Num(x[value]));
                    var wd = weekday.Average(x => Num(x[value]));
                    facts.Add($"Weekdays averaged {Fmt(wd, money)} and weekend days {Fmt(we, money)}.");
                }
            }
            AddOverallRatio(r, rows, facts);
            return facts;
        }

        // ---------------- a ranking (taxpayers, offices, stations, flag types, tax rates ...)
        var label = LabelColumn(r);
        var ordered = r.Rows.OrderByDescending(x => Num(x[value])).ToList();
        var sum = ordered.Sum(x => Num(x[value]));
        var shownWord = r.Truncated ? "the rows returned" : $"the {ordered.Count} rows shown";
        facts.Add($"Total {vName} of {shownWord}: {Fmt(sum, money)} (this is the total of these rows only, not a national total).");

        var top = ordered[0];
        if (sum > 0)
            facts.Add($"Largest: {Name(top, label)} with {Fmt(Num(top[value]), money)}, {Pct(Num(top[value]) / sum)} of the total of these {ordered.Count} rows.");

        var second = ordered[1];
        if (Num(second[value]) > 0)
        {
            var ratio = Num(top[value]) / Num(second[value]);
            facts.Add($"Second: {Name(second, label)} with {Fmt(Num(second[value]), money)}; the largest is " +
                      (ratio >= 2 ? $"{ratio.ToString("0.0", Inv)} times as large." : $"{Pct(ratio - 1)} larger."));
        }
        if (ordered.Count >= 4 && sum > 0)
        {
            var top3 = ordered.Take(3).Sum(x => Num(x[value]));
            facts.Add($"The top 3 together: {Fmt(top3, money)}, {Pct(top3 / sum)} of the total of these rows; the other {ordered.Count - 3} share {Pct(1 - top3 / sum)}.");
        }
        var last = ordered[^1];
        facts.Add($"Smallest: {Name(last, label)} with {Fmt(Num(last[value]), money)}.");

        var zero = ordered.Count(x => Num(x[value]) == 0);
        if (zero > 0 && zero < ordered.Count) facts.Add($"{zero} of the {ordered.Count} rows have a {vName} of zero.");

        AddRatios(r, top, facts, $"For {Name(top, label)}: ");
        AddOverallRatio(r, ordered, facts);
        return facts;
    }

    // ---------------------------------------------------------------- derived ratios

    private static void AddRatios(QueryResult r, object?[] row, List<string> facts, string prefix)
    {
        var gross = Index(r, "GrossSales");
        var vat = Index(r, "VAT");
        var invoices = Index(r, "Invoices");
        var assessed = Index(r, "AssessedAmount");
        var paid = Index(r, "PaidAmount");

        if (gross >= 0 && invoices >= 0 && Num(row[invoices]) > 0)
            facts.Add($"{prefix}average invoice value {Fmt(Num(row[gross]) / Num(row[invoices]), true)}.");
        if (gross >= 0 && vat >= 0 && Num(row[gross]) > 0)
            facts.Add($"{prefix}VAT is {Pct(Num(row[vat]) / Num(row[gross]))} of gross sales.");
        if (assessed >= 0 && paid >= 0 && Num(row[assessed]) > 0)
            facts.Add($"{prefix}{Pct(Num(row[paid]) / Num(row[assessed]))} of the assessed amount is paid; {Fmt(Num(row[assessed]) - Num(row[paid]), true)} has no receipt yet.");
    }

    private static void AddOverallRatio(QueryResult r, List<object?[]> rows, List<string> facts)
    {
        var gross = Index(r, "GrossSales");
        var vat = Index(r, "VAT");
        var assessed = Index(r, "AssessedAmount");
        var paid = Index(r, "PaidAmount");
        if (gross >= 0 && vat >= 0)
        {
            var g = rows.Sum(x => Num(x[gross]));
            if (g > 0) facts.Add($"Across these rows, VAT is {Pct(rows.Sum(x => Num(x[vat])) / g)} of gross sales.");
        }
        if (assessed >= 0 && paid >= 0)
        {
            var a = rows.Sum(x => Num(x[assessed]));
            if (a > 0) facts.Add($"Across these rows, {Pct(rows.Sum(x => Num(x[paid])) / a)} of the assessed amount is paid.");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static int ValueColumn(QueryResult r)
    {
        if (r.ChartValueColumns is { Count: > 0 })
        {
            var i = Index(r, r.ChartValueColumns[0]);
            if (i >= 0) return i;
        }
        foreach (var name in PreferredValues)
        {
            var i = Index(r, name);
            if (i >= 0 && r.Rows.Any(x => IsNum(x[i]))) return i;
        }
        return -1;
    }

    private static int LabelColumn(QueryResult r)
    {
        foreach (var name in PreferredLabels)
        {
            var i = Index(r, name);
            if (i >= 0) return i;
        }
        return 0;
    }

    private static string Name(object?[] row, int label)
    {
        var text = label < row.Length ? Convert.ToString(row[label], Inv) : null;
        if (string.IsNullOrWhiteSpace(text)) text = Convert.ToString(row[0], Inv);
        return string.IsNullOrWhiteSpace(text) ? "(not set)" : text.Trim();
    }

    private static int Index(QueryResult r, string column)
    {
        for (var i = 0; i < r.Columns.Count; i++)
            if (string.Equals(r.Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static bool IsNum(object? v) => ReportText.IsNumber(v);
    private static decimal Num(object? v) => v is not null && IsNum(v) ? Convert.ToDecimal(v, Inv) : 0;
    private static bool IsDate(object? v) => v is DateOnly or DateTime;
    private static DayOfWeek Day(object? v) => v switch { DateOnly d => d.DayOfWeek, DateTime dt => dt.DayOfWeek, _ => DayOfWeek.Monday };

    private static string When(object? v, bool monthly) => v switch
    {
        DateOnly d => monthly ? d.ToString("MMMM yyyy", Inv) : d.ToString("dddd yyyy-MM-dd", Inv),
        DateTime dt => monthly ? dt.ToString("MMMM yyyy", Inv) : dt.ToString("dddd yyyy-MM-dd", Inv),
        _ => Convert.ToString(v, Inv) ?? ""
    };

    private static string Fmt(decimal v, bool money) =>
        money ? "MWK " + v.ToString("#,0.00", Inv) : Math.Round(v).ToString("#,0", Inv);

    private static string Pct(decimal v) => (v * 100).ToString("0.0", Inv) + "%";

    private static string Change(decimal now, decimal before)
    {
        var c = (now - before) / before;
        return c switch
        {
            > 0.005m => $"{Pct(c)} higher than",
            < -0.005m => $"{Pct(-c)} lower than",
            _ => "about the same as"
        };
    }
}
