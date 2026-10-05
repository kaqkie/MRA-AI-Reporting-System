using System.Globalization;
using System.Text.Json;
using MraReporting.Data;
using ScottPlot;

namespace MraReporting.Reports;

/// <summary>
/// Chart images (ScottPlot) for PDF and Excel reports. Design rules:
///  - rankings (taxpayers, offices, stations, flag types, tax rates) are HORIZONTAL bars, so long names
///    sit on the left in full and never overlap; each bar carries its value;
///  - time series use strong colours and thick lines, with the axis in MWK millions or billions;
///  - one measure per chart, so a small series (VAT) is never flattened under a large one (sales);
///  - dark blue and orange only, which stay distinct in colour and when printed in grey.
/// </summary>
public static class ChartRenderer
{
    // MRA green, from the logo. Bars and lines in a mid green; the leader and highlights in the logo's own deep green.
    public static readonly Color Green = Color.FromHex("#1E6B3C");
    public static readonly Color Leader = Color.FromHex("#0B391B");
    public static readonly Color Rest = Color.FromHex("#5E9C74");
    private const int Width = 1400;

    /// <summary>Default chart for a query result (used by the Excel export of chat results).</summary>
    public static byte[]? Render(QueryResult result)
    {
        if (result.Rows.Count == 0 || result.ChartValueColumns is not { Count: > 0 }) return null;
        var column = result.ChartValueColumns[0];
        return result.Visual switch
        {
            VisualKind.Line => TimeSeries(result, column, result.Title, Green, asBars: false),
            VisualKind.Bar when IsDateLabelled(result) => TimeSeries(result, column, result.Title, Green, asBars: true),
            VisualKind.Bar => Ranking(result, column, result.Title, showShare: false),
            VisualKind.Pie => Ranking(result, column, result.Title, showShare: true),
            _ => null
        };
    }

    /// <summary>One measure over time, as bars (daily or monthly totals) or a line.</summary>
    public static byte[]? TimeSeries(QueryResult r, string column, string title, Color color, bool asBars,
        DateOnly? highlight = null)
    {
        var valueIndex = IndexOf(r.Columns, column);
        if (valueIndex < 0 || r.Rows.Count == 0) return null;

        var points = r.Rows
            .Select(row => (Date: AsDate(row[0]), Value: AsDouble(row[valueIndex])))
            .Where(p => p.Date is not null)
            .ToList();
        if (points.Count == 0) return null;

        var money = ReportText.IsMoney(column);
        var (scale, unit) = Scale(points.Max(p => Math.Abs(p.Value)), money);
        var xs = points.Select(p => p.Date!.Value.ToOADate()).ToArray();
        var ys = points.Select(p => p.Value / scale).ToArray();

        var plot = new Plot();
        if (asBars)
        {
            var monthly = points.Count > 1 && (points[1].Date!.Value - points[0].Date!.Value).TotalDays > 20;
            var bars = new List<Bar>();
            for (var i = 0; i < xs.Length; i++)
                bars.Add(new Bar { Position = xs[i], Value = ys[i], FillColor = color, Size = monthly ? 20 : 0.7 });
            plot.Add.Bars(bars);
            plot.Axes.Margins(bottom: 0);
        }
        else
        {
            var line = plot.Add.Scatter(xs, ys);
            line.Color = color;
            line.LineWidth = 3;
            line.MarkerSize = 7;
        }

        if (highlight is { } h)
        {
            var marker = plot.Add.VerticalLine(h.ToDateTime(TimeOnly.MinValue).ToOADate());
            marker.Color = Leader;
            marker.LineWidth = 2;
        }

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Bottom.TickLabelStyle.FontSize = 18;
        plot.Axes.Left.TickLabelStyle.FontSize = 18;
        plot.YLabel(money ? $"MWK {unit}" : ReportText.Header(column));
        plot.Axes.Left.Label.FontSize = 20;
        plot.Title(title);
        plot.Axes.Title.Label.FontSize = 24;
        return plot.GetImageBytes(Width, 560, ImageFormat.Png);
    }

    /// <summary>Largest first, as horizontal bars with the item names on the left and values on the bars.</summary>
    public static byte[]? Ranking(QueryResult r, string column, string title, bool showShare, int maxItems = 12)
    {
        var valueIndex = IndexOf(r.Columns, column);
        if (valueIndex < 0 || r.Rows.Count == 0) return null;
        var labelIndex = LabelColumn(r);

        var all = r.Rows
            .Select(row => (Label: LabelText(row, labelIndex, r), Value: AsDouble(row[valueIndex])))
            .OrderByDescending(x => x.Value)
            .ToList();
        var items = all.Take(maxItems).ToList();
        if (items.Count == 0 || items.All(i => i.Value == 0)) return null;

        var total = all.Sum(x => x.Value);
        var money = ReportText.IsMoney(column);
        var (scale, unit) = Scale(items.Max(i => Math.Abs(i.Value)), money);

        var plot = new Plot();
        var bars = new List<Bar>();
        var ticks = new ScottPlot.TickGenerators.NumericManual();
        for (var i = 0; i < items.Count; i++)
        {
            var position = items.Count - 1 - i; // largest at the top
            var (label, value) = items[i];
            var valueText = money ? ReportText.Short(value) : value.ToString("#,0", CultureInfo.InvariantCulture);
            if (showShare && total > 0) valueText += $"  ({value / total:0.0%})";

            bars.Add(new Bar
            {
                Position = position,
                Value = value / scale,
                FillColor = i == 0 ? Leader : Rest, // the leader stands out
                Label = valueText,
                Size = 0.7
            });
            ticks.AddMajor(position, ReportText.Trim(label, 36));
        }

        var barPlot = plot.Add.Bars(bars);
        barPlot.Horizontal = true;
        barPlot.ValueLabelStyle.FontSize = 18;
        barPlot.ValueLabelStyle.Bold = true;

        plot.Axes.Left.TickGenerator = ticks;
        plot.Axes.Left.TickLabelStyle.FontSize = 18;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 16;
        plot.Axes.Margins(left: 0, right: 0.25); // room for the value labels
        plot.XLabel(money ? $"MWK {unit}" : ReportText.Header(column));
        plot.Axes.Bottom.Label.FontSize = 20;
        plot.Title(title);
        plot.Axes.Title.Label.FontSize = 24;

        var height = 140 + 62 * items.Count;
        return plot.GetImageBytes(Width, height, ImageFormat.Png);
    }

    // ---------------------------------------------------------------- helpers

    private static (double Scale, string Unit) Scale(double max, bool money) =>
        !money ? (1, "") : max >= 1e9 ? (1e9, "billions") : max >= 1e6 ? (1e6, "millions") : (1, "");

    /// <summary>The column that names each bar: a name column when there is one, otherwise the first column.</summary>
    private static int LabelColumn(QueryResult r)
    {
        foreach (var candidate in new[] { "BusinessName", "Station", "FlagType", "TaxOffice", "InvestigationStatus" })
        {
            var i = IndexOf(r.Columns, candidate);
            if (i >= 0) return i;
        }
        return 0;
    }

    private static string LabelText(object?[] row, int labelIndex, QueryResult r)
    {
        var text = ReportText.Cell(r.Columns[labelIndex], row[labelIndex]);
        if (string.IsNullOrWhiteSpace(text)) text = ReportText.Cell(r.Columns[0], row[0]); // e.g. a taxpayer with no name
        return string.IsNullOrWhiteSpace(text) ? "(not set)" : text;
    }

    private static bool IsDateLabelled(QueryResult r) => r.Rows.Count > 0 && AsDate(r.Rows[0][0]) is not null;

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static DateTime? AsDate(object? value) => value switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTime dt => dt,
        JsonElement { ValueKind: JsonValueKind.String } e when e.GetString() is { Length: 10 } s
            && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p.ToDateTime(TimeOnly.MinValue),
        string s when s.Length == 10
            && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var p2) => p2.ToDateTime(TimeOnly.MinValue),
        _ => null
    };

    internal static double AsDouble(object? value) => value switch
    {
        null => 0,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        JsonElement => 0,
        string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0,
        bool b => b ? 1 : 0,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => 0
    };
}
