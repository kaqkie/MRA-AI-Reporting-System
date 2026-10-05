using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MraReporting.Data;

namespace MraReporting.Ai;

/// <summary>
/// Checks a finished answer against the query results of the same question, in code, because a small
/// model does not reliably follow "never invent numbers". Two checks:
///  1. every figure in the answer must match a value (or a column total, or a row count) from the results,
///     or a number the user typed;
///  2. tax office codes must not be "explained" in brackets, since their names are not in the data.
/// </summary>
public static class AnswerGuard
{
    // 1,234,567.89 / 1234.5 / 12345 with an optional "%", "billion", "million", "thousand" after it.
    private static readonly Regex NumberPattern = new(
        @"(?<![\w.])(\d{1,3}(?:,\d{3})+|\d+)(\.\d+)?(\s*%|\s*(?:billion|bn|million|thousand)\b)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A tax office code followed by a bracketed expansion, e.g. "LTO (Lusaka Tax Office)". Allowed only when
    // the bracketed text is exactly the official name from office-names.csv.
    private static readonly Regex CodeExpansionPattern = new(
        @"\b(LTO|LSTO|BSTO|LMTO|BMTO|ZMTO|MZUMTO|BLK|CHI|DWA|GCO|KAS|MAN|MZI|NTC|ZOM)\s*\(([^)]{3,})\)",
        RegexOptions.Compiled);

    public sealed record Finding(IReadOnlyList<string> UnmatchedNumbers, IReadOnlyList<string> CodeExpansions)
    {
        public bool IsClean => UnmatchedNumbers.Count == 0 && CodeExpansions.Count == 0;
    }

    public static Finding Check(string answer, IReadOnlyList<QueryResult> results, string question,
        IReadOnlyDictionary<string, string> officialOfficeNames)
    {
        var known = KnownValues(results, question);
        var unmatched = new List<string>();

        foreach (Match m in NumberPattern.Matches(answer))
        {
            var suffix = m.Groups[3].Value.Trim().ToLowerInvariant();
            if (suffix == "%") continue; // percentages are derived; not checked

            var raw = m.Groups[1].Value.Replace(",", "") + m.Groups[2].Value;
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) continue;

            var hasDecimals = m.Groups[2].Value.Length > 0;
            var scaled = suffix.Length > 0;
            if (!hasDecimals && !scaled)
            {
                if (value < 1000) continue;                     // small counts, "top 10", day numbers
                if (value is >= 1900 and <= 2100) continue;     // years
            }

            value *= suffix switch
            {
                "billion" or "bn" => 1_000_000_000m,
                "million" => 1_000_000m,
                "thousand" => 1_000m,
                _ => 1m
            };

            if (!Matches(value, known, tolerance: scaled ? 0.05m : 0.005m))
                unmatched.Add(m.Value.Trim());
        }

        var expansions = CodeExpansionPattern.Matches(answer)
            .Where(m => !m.Groups[2].Value.Contains("code", StringComparison.OrdinalIgnoreCase))
            .Where(m => !(officialOfficeNames.TryGetValue(m.Groups[1].Value, out var official)
                          && m.Groups[2].Value.Trim().Equals(official, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Value)
            .ToList();

        return new Finding(unmatched.Distinct().ToList(), expansions);
    }

    /// <summary>The instruction sent back to the model when a check fails.</summary>
    public static string CorrectionMessage(Finding finding, IReadOnlyList<QueryResult> results)
    {
        var parts = new List<string> { "Your previous answer did not match the data and was not shown to the user." };
        if (finding.UnmatchedNumbers.Count > 0)
            parts.Add($"These figures are not in the query results: {string.Join(", ", finding.UnmatchedNumbers)}.");
        if (finding.CodeExpansions.Count > 0)
            parts.Add($"You named tax offices ({string.Join("; ", finding.CodeExpansions)}) with names that are not in the results. Use office names exactly as they appear in the results, or the code alone.");

        if (results.Count > 0)
        {
            parts.Add("Answer again in two or three sentences using ONLY figures from these results. If a figure is not here, say it is not available.");
            parts.Add(string.Join("\n\n", results.Select(r => r.ToModelSummary())));
        }
        else
        {
            parts.Add("You gave figures without looking anything up. Call the right tool first, then answer only from its result.");
        }
        return string.Join("\n", parts);
    }

    private static List<decimal> KnownValues(IReadOnlyList<QueryResult> results, string question)
    {
        var values = new List<decimal>();

        foreach (Match m in NumberPattern.Matches(question))
            if (decimal.TryParse(m.Groups[1].Value.Replace(",", "") + m.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var q))
                values.Add(q);

        foreach (var r in results)
        {
            // Figures in the key facts were worked out by the app, so they are allowed in the answer.
            foreach (var fact in r.Insights ?? [])
                foreach (Match m in NumberPattern.Matches(fact))
                    if (decimal.TryParse(m.Groups[1].Value.Replace(",", "") + m.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var f))
                        values.Add(f);

            values.Add(r.Rows.Count);
            var columnTotals = new decimal[r.Columns.Count];
            foreach (var row in r.Rows)
            {
                for (var i = 0; i < row.Length && i < columnTotals.Length; i++)
                {
                    if (ToDecimal(row[i]) is { } v)
                    {
                        values.Add(v);
                        columnTotals[i] += v;
                    }
                }
            }
            values.AddRange(columnTotals.Where(t => t != 0));
        }
        return values;
    }

    private static bool Matches(decimal value, List<decimal> known, decimal tolerance) =>
        known.Any(k => Math.Abs(k - value) <= Math.Max(0.01m, Math.Abs(k) * tolerance));

    private static decimal? ToDecimal(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                decimal d => d,
                long or int or short or byte => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                double d when !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < 7.9e27 => (decimal)d,
                float f => (decimal)f,
                string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
                JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetDecimal(out var j) => j,
                _ => null
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
