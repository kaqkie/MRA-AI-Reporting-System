using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using MraReporting.Data;
using PdfSharp.Fonts;

namespace MraReporting.Reports;

/// <summary>
/// Writes a report to PDF with MigraDoc (PDFsharp, MIT licence). Layout: title band, summary,
/// headline figures with comparisons, numbered sections (explanation, charts, table), then an
/// appendix for long tables and a notes section. Column widths follow their content so names do not
/// wrap needlessly, money always has two decimals, and rows are striped for reading across.
/// </summary>
public static class PdfWriter
{
    static PdfWriter()
    {
        // PDFsharp 6 needs to be told where fonts come from. On Windows, use the installed fonts.
        if (OperatingSystem.IsWindows())
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    private const double ContentWidthCm = 17.0;
    private static readonly Color Brand = new(11, 57, 27);         // MRA green (logo)
    private static readonly Color BrandLight = new(220, 235, 224);  // header rows
    private static readonly Color Stripe = new(246, 250, 247);      // alternate rows
    private static readonly Color Rule = new(210, 216, 222);
    private static readonly Color Muted = new(96, 105, 115);
    private static readonly Color WarnFill = new(255, 244, 229);
    private static readonly Color WarnText = new(138, 75, 0);

    public static byte[] WriteReport(ReportDocument report)
    {
        var doc = new Document();
        doc.Info.Title = $"{report.Title} - {report.Subtitle}";
        doc.Info.Author = "MRA AI Reporting";
        DefineStyles(doc);

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        var logo = report.Branded ? MraLogo.Value : null;
        if (logo is not null)
        {
            // Letterhead on page 1; a slim running header with a small logo on the pages after it.
            section.PageSetup.DifferentFirstPageHeaderFooter = true;
            section.PageSetup.TopMargin = Unit.FromCentimeter(2.2);
            section.PageSetup.HeaderDistance = Unit.FromCentimeter(0.8);
            AddRunningHeader(section.Headers.Primary, logo, report);
            AddFooter(section.Footers.FirstPage, report);
            AddLetterhead(section, logo);
        }
        AddFooter(section.Footers.Primary, report);

        // ---------------- title band
        var title = section.AddParagraph(report.Title);
        title.Format.Font.Size = 18;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Colors.White;
        title.Format.Shading.Color = Brand;
        title.Format.Borders.Distance = Unit.FromPoint(8);
        title.Format.Borders.Color = Brand;

        var subtitle = section.AddParagraph($"Report date: {report.Subtitle}     Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} by {report.GeneratedBy}");
        subtitle.Format.Font.Size = 9.5;
        subtitle.Format.Font.Color = Colors.White;
        subtitle.Format.Shading.Color = Brand;
        subtitle.Format.Borders.Distance = Unit.FromPoint(8);
        subtitle.Format.Borders.Color = Brand;
        subtitle.Format.SpaceAfter = Unit.FromPoint(14);

        if (report.Banner is not null)
        {
            var banner = section.AddParagraph(report.Banner);
            banner.Format.Font.Size = 10;
            banner.Format.Font.Bold = true;
            banner.Format.Font.Color = WarnText;
            banner.Format.Shading.Color = WarnFill;
            banner.Format.Borders.Color = new Color(245, 201, 131);
            banner.Format.Borders.Width = 0.75;
            banner.Format.Borders.Distance = Unit.FromPoint(6);
            banner.Format.SpaceAfter = Unit.FromPoint(14);
        }

        // ---------------- summary and headline figures
        if (!string.IsNullOrWhiteSpace(report.Narrative))
        {
            section.AddParagraph("Summary", "SectionHeading");
            var narrative = section.AddParagraph(report.Narrative);
            narrative.Format.Font.Size = 10.5;
            narrative.Format.LineSpacingRule = LineSpacingRule.OnePtFive;
            narrative.Format.SpaceAfter = Unit.FromPoint(8);
        }

        if (report.Kpis.Count > 0)
        {
            section.AddParagraph("Headline figures", "SectionHeading");
            AddKpiTable(section, report.KpiHeaders, report.Kpis);
        }

        if (report.Findings is { Count: > 0 })
        {
            section.AddParagraph("What the figures show", "SectionHeading");
            foreach (var finding in report.Findings)
            {
                var p = section.AddParagraph();
                p.Format.Font.Size = 10;
                p.Format.LineSpacingRule = LineSpacingRule.Multiple;
                p.Format.LineSpacing = 1.25;
                p.Format.SpaceAfter = Unit.FromPoint(7);
                var lead = p.AddFormattedText(finding.Heading + ". ", TextFormat.Bold);
                lead.Color = Brand;
                p.AddText(finding.Text);
            }
        }

        // ---------------- numbered sections
        var appendix = new List<ReportSection>();
        var number = 1;
        foreach (var part in report.Sections)
        {
            section.AddParagraph(report.Sections.Count > 1 ? $"{number}. {part.Heading}" : part.Heading, "SectionHeading");
            var intro = section.AddParagraph(part.Intro, "Intro");
            intro.Format.KeepWithNext = true;

            if (part.Data.Rows.Count == 0)
            {
                section.AddParagraph("No data for this period.", "Intro");
                number++;
                continue;
            }

            foreach (var chart in part.Charts)
            {
                var holder = section.AddParagraph();
                holder.Format.Alignment = ParagraphAlignment.Center;
                holder.Format.SpaceAfter = Unit.FromPoint(6);
                holder.Format.KeepWithNext = !part.TableInAppendix;
                var image = holder.AddImage("base64:" + Convert.ToBase64String(chart));
                image.Width = Unit.FromCentimeter(ContentWidthCm);
                image.LockAspectRatio = true;
            }

            if (part.TableInAppendix)
            {
                appendix.Add(part);
                section.AddParagraph($"The figures for each day are in Appendix {(char)('A' + appendix.Count - 1)}.", "Intro");
            }
            else
            {
                AddDataTable(section, part.Data, part.MaxPdfRows);
            }
            number++;
        }

        // ---------------- appendix and notes
        for (var i = 0; i < appendix.Count; i++)
        {
            section.AddPageBreak();
            section.AddParagraph($"Appendix {(char)('A' + i)}. {appendix[i].Heading}: figures", "SectionHeading");
            AddDataTable(section, appendix[i].Data, maxRows: 1000);
        }

        section.AddParagraph("Notes", "SectionHeading");
        foreach (var note in report.Notes)
            section.AddParagraph("•  " + note, "Small").Format.SpaceAfter = Unit.FromPoint(3);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }

    // ---------------------------------------------------------------- MRA letterhead (summary reports)

    private static readonly Lazy<byte[]?> MraLogo = new(() =>
    {
        using var stream = typeof(PdfWriter).Assembly.GetManifestResourceStream("MraReporting.mra-logo.png");
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    });

    private static string ImageSource(byte[] png) => "base64:" + Convert.ToBase64String(png);

    private static void AddFooter(HeaderFooter footerArea, ReportDocument report)
    {
        var footer = footerArea.AddParagraph();
        footer.Style = "Small";
        footer.Format.Borders.Top.Width = 0.75;
        footer.Format.Borders.Top.Color = Brand;
        footer.Format.Borders.Distance = Unit.FromPoint(3);
        footer.AddText($"Malawi Revenue Authority  |  {report.Title}  |  {report.Subtitle}  |  Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
    }

    /// <summary>Page 1: the logo on the left, the authority's name and "Internal" on the right, over a green rule.</summary>
    private static void AddLetterhead(Section section, byte[] logo)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.AddColumn(Unit.FromCentimeter(3.4));
        table.AddColumn(Unit.FromCentimeter(ContentWidthCm - 3.4));
        var row = table.AddRow();
        row.VerticalAlignment = VerticalAlignment.Center;

        var image = row.Cells[0].AddParagraph().AddImage(ImageSource(logo));
        image.Height = Unit.FromCentimeter(2.3);
        image.LockAspectRatio = true;

        var name = row.Cells[1].AddParagraph("MALAWI REVENUE AUTHORITY");
        name.Format.Alignment = ParagraphAlignment.Right;
        name.Format.Font.Size = 15;
        name.Format.Font.Bold = true;
        name.Format.Font.Color = Brand;
        var unit = row.Cells[1].AddParagraph("Revenue reporting  |  Internal use");
        unit.Format.Alignment = ParagraphAlignment.Right;
        unit.Format.Font.Size = 9;
        unit.Format.Font.Color = Muted;

        var rule = section.AddParagraph();
        rule.Format.Borders.Bottom.Width = 2.5;
        rule.Format.Borders.Bottom.Color = Brand;
        rule.Format.SpaceAfter = Unit.FromPoint(10);
        rule.Format.Font.Size = 2;
    }

    /// <summary>Pages 2 onwards: a small logo, the report title, and a thin green rule.</summary>
    private static void AddRunningHeader(HeaderFooter header, byte[] logo, ReportDocument report)
    {
        var table = header.AddTable();
        table.Borders.Width = 0;
        table.AddColumn(Unit.FromCentimeter(1.6));
        table.AddColumn(Unit.FromCentimeter(ContentWidthCm - 1.6));
        var row = table.AddRow();
        row.VerticalAlignment = VerticalAlignment.Center;
        row.Borders.Bottom.Width = 1;
        row.Borders.Bottom.Color = Brand;

        var image = row.Cells[0].AddParagraph().AddImage(ImageSource(logo));
        image.Height = Unit.FromCentimeter(0.9);
        image.LockAspectRatio = true;

        var text = row.Cells[1].AddParagraph($"{report.Title}  |  {report.Subtitle}");
        text.Format.Alignment = ParagraphAlignment.Right;
        text.Format.Font.Size = 8;
        text.Format.Font.Color = Brand;
        text.Format.Font.Bold = true;
    }

    private static void DefineStyles(Document doc)
    {
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 9;
        normal.Font.Color = new Color(33, 37, 41);

        var heading = doc.Styles.AddStyle("SectionHeading", StyleNames.Normal);
        heading.Font.Size = 13;
        heading.Font.Bold = true;
        heading.Font.Color = Brand;
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(16);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        heading.ParagraphFormat.KeepWithNext = true;
        heading.ParagraphFormat.Borders.Bottom.Width = 0.75;
        heading.ParagraphFormat.Borders.Bottom.Color = Rule;
        heading.ParagraphFormat.Borders.Distance = Unit.FromPoint(2);

        var intro = doc.Styles.AddStyle("Intro", StyleNames.Normal);
        intro.Font.Size = 9;
        intro.Font.Color = Muted;
        intro.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var small = doc.Styles.AddStyle("Small", StyleNames.Normal);
        small.Font.Size = 8;
        small.Font.Color = Muted;
    }

    private static void AddKpiTable(Section section, IReadOnlyList<string> kpiHeaders, IReadOnlyList<ReportKpi> kpis)
    {
        var withComparison = kpis.Any(k => k.Previous.Length > 0 || k.Average.Length > 0);
        var widths = withComparison ? new[] { 5.0, 3.5, 3.2, 3.2, 2.1 } : new[] { 9.0, 8.0 };
        var table = NewTable(section, widths);

        var header = table.AddRow();
        StyleHeader(header);
        var headers = withComparison ? kpiHeaders.Take(5).ToArray() : kpiHeaders.Take(2).ToArray();
        for (var c = 0; c < headers.Length; c++)
        {
            header.Cells[c].AddParagraph(headers[c]);
            if (c > 0) header.Cells[c].Format.Alignment = ParagraphAlignment.Right;
        }

        var i = 0;
        foreach (var k in kpis)
        {
            var row = table.AddRow();
            if (i++ % 2 == 1) row.Shading.Color = Stripe;
            row.Cells[0].AddParagraph(k.Label);
            var value = row.Cells[1].AddParagraph(k.Value);
            value.Format.Font.Bold = true;
            row.Cells[1].Format.Alignment = ParagraphAlignment.Right;
            if (withComparison)
            {
                row.Cells[2].AddParagraph(k.Previous);
                row.Cells[3].AddParagraph(k.Average);
                var change = row.Cells[4].AddParagraph(k.Change);
                if (k.Change.StartsWith('-')) change.Format.Font.Color = new Color(180, 35, 24);
                else if (k.Change.StartsWith('+')) change.Format.Font.Color = new Color(22, 120, 60);
                for (var c = 2; c <= 4; c++) row.Cells[c].Format.Alignment = ParagraphAlignment.Right;
            }
        }
    }

    private static void AddDataTable(Section section, QueryResult data, int maxRows)
    {
        var columns = data.Columns.Count;
        var shown = data.Rows.Take(maxRows).ToList();
        var numeric = Enumerable.Range(0, columns).Select(c => data.Rows.Any(r => ReportText.IsNumber(r[c]))).ToArray();

        // Width follows content: the longest header word or cell (capped), so names get room and numbers stay compact.
        var weights = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            var header = ReportText.Header(data.Columns[c]);
            var longestHeaderWord = header.Split(' ').Max(w => w.Length);
            var longestCell = shown.Select(r => ReportText.Cell(data.Columns[c], r[c]).Length).DefaultIfEmpty(0).Max();
            weights[c] = Math.Clamp(Math.Max(longestHeaderWord, longestCell), 6, 32);
        }
        var total = weights.Sum();
        var widths = weights.Select(w => Math.Max(1.6, ContentWidthCm * w / total)).ToArray();
        var scale = ContentWidthCm / widths.Sum();
        widths = widths.Select(w => w * scale).ToArray();

        var table = NewTable(section, widths);
        var headerRow = table.AddRow();
        StyleHeader(headerRow);
        for (var c = 0; c < columns; c++)
        {
            headerRow.Cells[c].AddParagraph(ReportText.Header(data.Columns[c]));
            headerRow.Cells[c].Format.Alignment = numeric[c] ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }

        for (var r = 0; r < shown.Count; r++)
        {
            var row = table.AddRow();
            if (r % 2 == 1) row.Shading.Color = Stripe;
            for (var c = 0; c < columns && c < shown[r].Length; c++)
            {
                row.Cells[c].AddParagraph(ReportText.Cell(data.Columns[c], shown[r][c]));
                row.Cells[c].Format.Alignment = numeric[c] ? ParagraphAlignment.Right : ParagraphAlignment.Left;
            }
        }

        if (data.Rows.Count > maxRows || data.Truncated)
            section.AddParagraph($"Showing {shown.Count} of {data.Rows.Count}{(data.Truncated ? "+" : "")} rows. The Excel version has all rows.", "Small");

        var spacer = section.AddParagraph();
        spacer.Format.SpaceAfter = Unit.FromPoint(4);
    }

    private static Table NewTable(Section section, double[] widthsCm)
    {
        var table = section.AddTable();
        table.Format.Font.Size = 8.5;
        table.Borders.Width = 0.5;
        table.Borders.Color = Rule;
        table.LeftPadding = Unit.FromPoint(4);
        table.RightPadding = Unit.FromPoint(4);
        table.TopPadding = Unit.FromPoint(2.5);
        table.BottomPadding = Unit.FromPoint(2.5);
        foreach (var w in widthsCm) table.AddColumn(Unit.FromCentimeter(w));
        return table;
    }

    private static void StyleHeader(Row header)
    {
        header.HeadingFormat = true; // repeats at the top of each page
        header.Format.Font.Bold = true;
        header.Shading.Color = BrandLight;
        header.VerticalAlignment = VerticalAlignment.Center;
    }
}
