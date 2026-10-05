using System.Text.Json;
using MraReporting.Data;

namespace MraReporting.Reports;

/// <summary>
/// Turns one chat result (a table the user sees under an answer) into a small report, so it can be
/// downloaded as PDF or Word as well as Excel: a title band, the chart, the full table and a note.
/// </summary>
public static class SingleResultReport
{
    public static ReportDocument Build(QueryResult raw, string userName, DateTime now)
    {
        var result = Normalize(raw);
        var chart = ChartRenderer.Render(result);
        var notes = new List<string> { $"Figures as of {result.AsOf:yyyy-MM-dd HH:mm}, taken directly from the database." };
        if (result.Truncated) notes.Add("Only the first rows were returned for this question; ask a narrower question for the rest.");

        return new ReportDocument(
            Title: result.Title,
            Subtitle: $"{result.AsOf:yyyy-MM-dd}",
            GeneratedAt: now,
            GeneratedBy: userName,
            Narrative: "",
            Banner: null,
            KpiHeaders: Array.Empty<string>(),
            Kpis: Array.Empty<ReportKpi>(),
            Sections: new[]
            {
                new ReportSection("Results", result.Note ?? "", result,
                    chart is null ? Array.Empty<byte[]>() : new[] { chart },
                    TableInAppendix: false, MaxPdfRows: 1000)
            },
            Notes: notes);
    }

    /// <summary>
    /// A result posted back by the browser arrives as JSON values. Turn them into plain numbers, text and
    /// true/false, so the PDF and Word writers format money and align numbers exactly as in the reports.
    /// </summary>
    public static QueryResult Normalize(QueryResult result)
    {
        var rows = result.Rows.Select(row => row.Select(ToPlain).ToArray()).ToList();
        return result with { Rows = rows };
    }

    private static object? ToPlain(object? value) => value switch
    {
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt64(out var l) => l,
            JsonValueKind.Number when e.TryGetDecimal(out var d) => d,
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => e.ToString()
        },
        _ => value
    };
}
