using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using MraReporting.Data;

namespace MraReporting.Reports;

/// <summary>Writes reports and single chat results to .xlsx with ClosedXML.</summary>
public static class ExcelWriter
{
    public static byte[] WriteReport(ReportDocument report)
    {
        using var workbook = new XLWorkbook();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var summary = workbook.Worksheets.Add(UniqueSheetName("Summary", usedNames));
        summary.Cell(1, 1).Value = report.Title;
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 16;
        summary.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#0B391B");
        summary.Cell(2, 1).Value = $"Report date: {report.Subtitle}";
        summary.Cell(3, 1).Value = $"Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} by {report.GeneratedBy}";
        summary.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;

        var row = 5;
        if (report.Banner is not null)
        {
            summary.Cell(row, 1).Value = report.Banner;
            summary.Range(row, 1, row, 5).Merge();
            summary.Cell(row, 1).Style.Font.Bold = true;
            summary.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#8A4B00");
            summary.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4E5");
            summary.Cell(row, 1).Style.Alignment.WrapText = true;
            summary.Row(row).Height = 45;
            row += 2;
        }

        summary.Cell(row, 1).Value = "Summary";
        summary.Cell(row, 1).Style.Font.Bold = true;
        row++;
        summary.Cell(row, 1).Value = report.Narrative;
        summary.Range(row, 1, row, 5).Merge();
        summary.Cell(row, 1).Style.Alignment.WrapText = true;
        summary.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        summary.Row(row).Height = Math.Max(60, 15 * Math.Ceiling(report.Narrative.Length / 110.0));
        row += 2;

        if (report.Findings is { Count: > 0 })
        {
            summary.Cell(row, 1).Value = "What the figures show";
            summary.Cell(row, 1).Style.Font.Bold = true;
            row++;
            foreach (var finding in report.Findings)
            {
                summary.Cell(row, 1).Value = finding.Heading;
                summary.Cell(row, 1).Style.Font.Bold = true;
                summary.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#0B391B");
                summary.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                summary.Cell(row, 2).Value = finding.Text;
                summary.Range(row, 2, row, 5).Merge();
                summary.Cell(row, 2).Style.Alignment.WrapText = true;
                summary.Cell(row, 2).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                summary.Row(row).Height = Math.Max(30, 15 * Math.Ceiling(finding.Text.Length / 95.0));
                row++;
            }
            row++;
        }

        var headers = report.KpiHeaders;
        for (var c = 0; c < headers.Count; c++)
        {
            var cell = summary.Cell(row, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCEBE0");
        }
        foreach (var kpi in report.Kpis)
        {
            row++;
            summary.Cell(row, 1).Value = kpi.Label;
            summary.Cell(row, 2).Value = kpi.Value;
            summary.Cell(row, 2).Style.Font.Bold = true;
            summary.Cell(row, 3).Value = kpi.Previous;
            summary.Cell(row, 4).Value = kpi.Average;
            summary.Cell(row, 5).Value = kpi.Change;
            for (var c = 2; c <= 5; c++) summary.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }

        row += 2;
        summary.Cell(row, 1).Value = "Notes";
        summary.Cell(row, 1).Style.Font.Bold = true;
        foreach (var note in report.Notes)
            summary.Cell(++row, 1).Value = note;

        summary.Column(1).Width = 48;
        for (var c = 2; c <= 5; c++) summary.Column(c).Width = 22;

        var number = 1;
        foreach (var section in report.Sections)
        {
            var sheet = workbook.Worksheets.Add(UniqueSheetName($"{number}. {section.Heading}", usedNames));
            WriteResult(sheet, section.Data, $"{number}. {section.Heading}", section.Charts, section.Intro);
            number++;
        }

        return Save(workbook);
    }

    public static byte[] WriteSingle(QueryResult result)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Result");
        var chart = ChartRenderer.Render(result);
        IReadOnlyList<byte[]> charts = chart is null ? Array.Empty<byte[]>() : new[] { chart };
        WriteResult(sheet, result, result.Title, charts, null);
        return Save(workbook);
    }

    private static void WriteResult(IXLWorksheet sheet, QueryResult result, string heading, IReadOnlyList<byte[]> charts, string? intro)
    {
        sheet.Cell(1, 1).Value = heading;
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 13;
        sheet.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#0B391B");
        sheet.Cell(2, 1).Value = intro ?? "";
        sheet.Cell(3, 1).Value = $"As of {result.AsOf:yyyy-MM-dd HH:mm}" +
                                 (result.Truncated ? $" - first {result.Rows.Count} rows only" : "") +
                                 (string.IsNullOrWhiteSpace(result.Note) ? "" : $" - {result.Note}");
        sheet.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;

        // Charts first, one under another; each image is 1400 px wide, shown at about 60%.
        var chartRow = 5;
        foreach (var png in charts)
        {
            var picture = sheet.AddPicture(new MemoryStream(png)).MoveTo(sheet.Cell(chartRow, 1));
            picture.Scale(0.6);
            chartRow += (int)Math.Ceiling(picture.Height / 20.0) + 2; // default row height is 20 px
        }
        var headerRow = chartRow;

        for (var c = 0; c < result.Columns.Count; c++)
        {
            var cell = sheet.Cell(headerRow, c + 1);
            cell.Value = ReportText.Header(result.Columns[c]);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCEBE0");
        }

        for (var r = 0; r < result.Rows.Count; r++)
        {
            var values = result.Rows[r];
            for (var c = 0; c < values.Length && c < result.Columns.Count; c++)
            {
                var cell = sheet.Cell(headerRow + 1 + r, c + 1);
                var (value, format) = ToCell(values[c]);
                cell.Value = value;
                if (ReportText.IsMoney(result.Columns[c]) && ReportText.IsNumber(values[c])) format = "#,##0.00";
                if (format is not null) cell.Style.NumberFormat.Format = format;
                if (r % 2 == 1) cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F6FAF7");
            }
        }

        if (result.Rows.Count > 0)
            sheet.Range(headerRow, 1, headerRow + result.Rows.Count, Math.Max(1, result.Columns.Count)).SetAutoFilter();
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns(1, Math.Max(1, result.Columns.Count)).AdjustToContents(headerRow, headerRow + Math.Min(result.Rows.Count, 200));
    }

    private static (XLCellValue Value, string? Format) ToCell(object? value) => value switch
    {
        null => (Blank.Value, null),
        string s => (s, null),
        bool b => (b ? "yes" : "no", null),
        DateOnly d => (d.ToDateTime(TimeOnly.MinValue), "yyyy-mm-dd"),
        DateTime dt => (dt, "yyyy-mm-dd hh:mm"),
        long or int or short => (Convert.ToDouble(value, CultureInfo.InvariantCulture), "#,##0"),
        decimal or double or float => (Convert.ToDouble(value, CultureInfo.InvariantCulture), "#,##0.00"),
        JsonElement e => FromJson(e),
        _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", null)
    };

    /// <summary>Values that came back from the browser (Excel export of a chat result) arrive as JSON.</summary>
    private static (XLCellValue, string?) FromJson(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                var n = e.GetDouble();
                return (n, Math.Abs(n % 1) > 0 ? "#,##0.00" : "#,##0");
            case JsonValueKind.True: return ("yes", null);
            case JsonValueKind.False: return ("no", null);
            case JsonValueKind.String:
                var s = e.GetString() ?? "";
                if (s.Length == 10 && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return (d.ToDateTime(TimeOnly.MinValue), "yyyy-mm-dd");
                return (s, null);
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return (Blank.Value, null);
            default:
                return (e.ToString(), null);
        }
    }

    private static string UniqueSheetName(string name, HashSet<string> used)
    {
        var clean = new string(name.Where(ch => ch is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim();
        if (clean.Length == 0) clean = "Sheet";
        if (clean.Length > 31) clean = clean[..31];
        var candidate = clean;
        for (var i = 2; !used.Add(candidate); i++)
        {
            var suffix = $" ({i})";
            candidate = clean[..Math.Min(clean.Length, 31 - suffix.Length)] + suffix;
        }
        return candidate;
    }

    private static byte[] Save(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
