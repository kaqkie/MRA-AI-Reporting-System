using System.Globalization;
using System.Text;

namespace MraReporting.Data;

/// <summary>How a result should be drawn. Chosen by the query function, never by the model.</summary>
public enum VisualKind { None, Table, Line, Bar, Pie }

/// <summary>
/// The shape every query returns. The browser draws the table or chart from Rows;
/// the model only ever sees <see cref="ToModelSummary"/>.
/// </summary>
/// <param name="ChartValueColumns">Numeric columns to plot. The first column is always the label/x-axis.</param>
public sealed record QueryResult(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    VisualKind Visual,
    DateTime AsOf,
    bool Truncated = false,
    string? Note = null,
    IReadOnlyList<string>? ChartValueColumns = null,
    IReadOnlyList<string>? Insights = null)   // key facts worked out in code, for the AI to explain
{
    /// <summary>A compact text version for the model: enough to phrase an answer, small enough to stay fast.</summary>
    public string ToModelSummary(int maxRows = 15)
    {
        var sb = new StringBuilder();
        sb.Append(Title).Append(" - ").Append(Rows.Count).Append(Rows.Count == 1 ? " row" : " rows");
        if (Truncated) sb.Append(" (more rows exist; only the first ").Append(Rows.Count).Append(" were fetched)");
        sb.AppendLine(".");

        if (Rows.Count == 0)
        {
            sb.AppendLine("No matching data.");
        }
        else
        {
            sb.AppendLine(string.Join(" | ", Columns));
            foreach (var row in Rows.Take(maxRows))
                sb.AppendLine(string.Join(" | ", row.Select(Format)));
            if (Rows.Count > maxRows)
                sb.Append("... ").Append(Rows.Count - maxRows).AppendLine(" more rows not shown here.");
        }

        if (!string.IsNullOrWhiteSpace(Note)) sb.Append("Note: ").AppendLine(Note);
        if (Insights is { Count: > 0 })
        {
            sb.AppendLine("Key facts, worked out exactly from the full result (use these instead of calculating):");
            foreach (var fact in Insights) sb.Append("- ").AppendLine(fact);
        }
        sb.Append("The full result is already shown to the user as a ")
          .Append(Visual is VisualKind.None or VisualKind.Table ? "table" : $"{Visual.ToString().ToLowerInvariant()} chart and table")
          .Append(". Do not repeat every row.");
        return sb.ToString();
    }

    public static string Format(object? value) => value switch
    {
        null => "-",
        decimal d => d.ToString("#,0.##", CultureInfo.InvariantCulture),
        double d => d.ToString("#,0.##", CultureInfo.InvariantCulture),
        float f => f.ToString("#,0.##", CultureInfo.InvariantCulture),
        long or int or short => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("#,0", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-"
    };
}
