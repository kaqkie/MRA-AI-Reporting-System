using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MraReporting.Data;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace MraReporting.Reports;

/// <summary>
/// Writes a report to Word (.docx) with Microsoft's Open XML SDK (MIT licence). Same layout as the PDF:
/// title band, summary, headline figures, numbered sections with charts and tables, appendix, notes.
/// Section headings use Word's own Heading 1 style, so they appear in Word's navigation pane and a
/// table of contents can be added in Word. The document stays fully editable.
/// </summary>
public static class WordWriter
{
    private const string Brand = "0B391B";       // MRA green
    private const string BrandLight = "DCEBE0";  // header rows
    private const string Stripe = "F6FAF7";      // alternate rows
    private const string RuleColour = "D2D8DE";
    private const string Muted = "606973";
    private const string Ink = "212529";
    private const int ContentTwips = 9638;       // 17 cm of A4 between 2 cm margins
    private const long ContentEmu = 6120000;     // 17 cm in EMU

    public static byte[] WriteReport(ReportDocument report)
    {
        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var body = main.Document.Body!;
            AddStyles(main);
            word.PackageProperties.Title = $"{report.Title} - {report.Subtitle}";
            word.PackageProperties.Creator = "MRA AI Reporting";

            // ---------------- title band
            body.Append(Para(report.Title, size: 36, bold: true, colour: "FFFFFF", shade: Brand, after: 0));
            body.Append(Para($"Report date: {report.Subtitle}     Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} by {report.GeneratedBy}",
                size: 19, colour: "FFFFFF", shade: Brand, after: 280));

            if (report.Banner is not null)
                body.Append(Para(report.Banner, size: 20, bold: true, colour: "8A4B00", shade: "FFF4E5", after: 280));

            // ---------------- summary and headline figures
            if (!string.IsNullOrWhiteSpace(report.Narrative))
            {
                body.Append(Heading("Summary"));
                foreach (var line in report.Narrative.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    body.Append(Para(line, size: 21, after: 160));
            }

            if (report.Kpis.Count > 0)
            {
                body.Append(Heading("Headline figures"));
                body.Append(KpiTable(report.KpiHeaders, report.Kpis));
                body.Append(Para("", after: 80));
            }

            if (report.Findings is { Count: > 0 })
            {
                body.Append(Heading("What the figures show"));
                foreach (var finding in report.Findings)
                    body.Append(new Paragraph(
                        new ParagraphProperties(new SpacingBetweenLines { After = "140", Before = "0" }),
                        TextRun(finding.Heading + ". ", 20, bold: true, colour: Brand),
                        TextRun(finding.Text, 20)));
            }

            // ---------------- numbered sections
            var appendix = new List<ReportSection>();
            var number = 1;
            uint imageId = 1;
            foreach (var part in report.Sections)
            {
                body.Append(Heading(report.Sections.Count > 1 ? $"{number}. {part.Heading}" : part.Heading));
                if (!string.IsNullOrWhiteSpace(part.Intro))
                    body.Append(Para(part.Intro, size: 18, colour: Muted, after: 120, keepNext: true));

                if (part.Data.Rows.Count == 0)
                {
                    body.Append(Para("No data for this period.", size: 18, colour: Muted));
                    number++;
                    continue;
                }

                foreach (var chart in part.Charts)
                    body.Append(Picture(main, chart, imageId++));

                if (part.TableInAppendix)
                {
                    appendix.Add(part);
                    body.Append(Para($"The figures for each day are in Appendix {(char)('A' + appendix.Count - 1)}.", size: 18, colour: Muted));
                }
                else
                {
                    AppendDataTable(body, part.Data, part.MaxPdfRows);
                }
                number++;
            }

            // ---------------- appendix and notes
            for (var i = 0; i < appendix.Count; i++)
            {
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                body.Append(Heading($"Appendix {(char)('A' + i)}. {appendix[i].Heading}: figures"));
                AppendDataTable(body, appendix[i].Data, maxRows: 1000);
            }

            if (report.Notes.Count > 0)
            {
                body.Append(Heading("Notes"));
                foreach (var note in report.Notes)
                    body.Append(Para("•  " + note, size: 16, colour: Muted, after: 60));
            }

            // ---------------- page setup and footer with page numbers
            var footerPart = main.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { After = "0" }),
                SmallRun($"{report.Title}  |  {report.Subtitle}  |  Page "),
                new SimpleField(SmallRun("1")) { Instruction = " PAGE " },
                SmallRun(" of "),
                new SimpleField(SmallRun("1")) { Instruction = " NUMPAGES " }));
            footerPart.Footer.Save();

            body.Append(new SectionProperties(
                new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) },
                new PageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 907, Bottom = 1021, Left = 1134U, Right = 1134U, Header = 567U, Footer = 567U, Gutter = 0U }));

            main.Document.Save();
        }
        return stream.ToArray();
    }

    // ---------------------------------------------------------------- building blocks

    private static void AddStyles(MainDocumentPart main)
    {
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial", EastAsia = "Arial" },
                    new Color { Val = Ink },
                    new FontSize { Val = "18" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "80", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(
                new StyleName { Val = "Normal" },
                new PrimaryStyle())
            { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(
                new StyleName { Val = "heading 1" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(
                    new KeepNext(),
                    new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6U, Space = 2U, Color = RuleColour }),
                    new SpacingBetweenLines { Before = "320", After = "100" },
                    new OutlineLevel { Val = 0 }),
                new StyleRunProperties(
                    new Bold(),
                    new Color { Val = Brand },
                    new FontSize { Val = "26" }))
            { Type = StyleValues.Paragraph, StyleId = "Heading1" });
        stylesPart.Styles.Save();
    }

    private static Paragraph Heading(string text) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Para(string text, int size = 18, bool bold = false, string? colour = null,
        string? shade = null, int after = 80, bool keepNext = false, JustificationValues? align = null)
    {
        var pp = new ParagraphProperties();
        if (keepNext) pp.Append(new KeepNext());
        if (shade is not null) pp.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = shade });
        pp.Append(new SpacingBetweenLines { After = after.ToString(), Before = "0" });
        if (shade is not null) pp.Append(new Indentation { Left = "120", Right = "120" });
        if (align is not null) pp.Append(new Justification { Val = align.Value });

        return new Paragraph(pp, TextRun(text, size, bold, colour));
    }

    private static Run TextRun(string text, int size, bool bold = false, string? colour = null)
    {
        var rp = new RunProperties();
        if (bold) rp.Append(new Bold());
        if (colour is not null) rp.Append(new Color { Val = colour });
        rp.Append(new FontSize { Val = size.ToString() });
        return new Run(rp, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Run SmallRun(string text) => TextRun(text, 16, colour: Muted);

    private static Paragraph Picture(MainDocumentPart main, byte[] png, uint id)
    {
        var imagePart = main.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(png)) imagePart.FeedData(ms);
        var relId = main.GetIdOfPart(imagePart);

        // Keep the chart's shape: read its pixel size from the PNG header.
        long cx = ContentEmu, cy = ContentEmu / 2;
        if (png.Length > 24)
        {
            var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            if (w > 0 && h > 0) cy = ContentEmu * h / w;
        }

        var drawing = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = $"Chart {id}" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"chart{id}.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });

        return new Paragraph(
            new ParagraphProperties(
                new SpacingBetweenLines { After = "120" },
                new Justification { Val = JustificationValues.Center }),
            new Run(drawing));
    }

    private static Table NewTable(int[] widths)
    {
        TableBorders Borders() => new(
            new TopBorder { Val = BorderValues.Single, Size = 4U, Color = RuleColour },
            new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = RuleColour },
            new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = RuleColour },
            new RightBorder { Val = BorderValues.Single, Size = 4U, Color = RuleColour },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = RuleColour },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = RuleColour });

        var table = new Table(new TableProperties(
            new TableWidth { Width = widths.Sum().ToString(), Type = TableWidthUnitValues.Dxa },
            Borders(),
            new TableLayout { Type = TableLayoutValues.Fixed }));
        var grid = new TableGrid();
        foreach (var w in widths) grid.Append(new GridColumn { Width = w.ToString() });
        table.Append(grid);
        return table;
    }

    private static TableCell Cell(string text, int width, bool right, bool bold = false, string? fill = null, string? colour = null)
    {
        var props = new TableCellProperties(new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa });
        if (fill is not null) props.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fill });
        return new TableCell(props, new Paragraph(
            new ParagraphProperties(
                new SpacingBetweenLines { After = "0", Before = "0" },
                new Justification { Val = right ? JustificationValues.Right : JustificationValues.Left }),
            TextRun(text, 17, bold, colour)));
    }

    private static TableRow HeaderRow(IReadOnlyList<string> headers, int[] widths, bool[] right)
    {
        var row = new TableRow(new TableRowProperties(new TableHeader()));
        for (var c = 0; c < headers.Count; c++)
            row.Append(Cell(headers[c], widths[c], right[c], bold: true, fill: BrandLight, colour: Brand));
        return row;
    }

    private static Table KpiTable(IReadOnlyList<string> kpiHeaders, IReadOnlyList<ReportKpi> kpis)
    {
        var withComparison = kpis.Any(k => k.Previous.Length > 0 || k.Average.Length > 0);
        var widths = withComparison ? new[] { 2835, 1985, 1814, 1814, 1190 } : new[] { 5103, 4535 };
        var right = withComparison ? new[] { false, true, true, true, true } : new[] { false, true };
        var headers = (withComparison ? kpiHeaders.Take(5) : kpiHeaders.Take(2)).ToList();

        var table = NewTable(widths);
        table.Append(HeaderRow(headers, widths, right));
        var i = 0;
        foreach (var k in kpis)
        {
            var fill = i++ % 2 == 1 ? Stripe : null;
            var row = new TableRow(
                Cell(k.Label, widths[0], false, fill: fill),
                Cell(k.Value, widths[1], true, bold: true, fill: fill));
            if (withComparison)
            {
                var changeColour = k.Change.StartsWith('-') ? "B42318" : k.Change.StartsWith('+') ? "16783C" : null;
                row.Append(Cell(k.Previous, widths[2], true, fill: fill));
                row.Append(Cell(k.Average, widths[3], true, fill: fill));
                row.Append(Cell(k.Change, widths[4], true, fill: fill, colour: changeColour));
            }
            table.Append(row);
        }
        return table;
    }

    private static void AppendDataTable(Body body, QueryResult data, int maxRows)
    {
        var columns = data.Columns.Count;
        if (columns == 0) return;
        var shown = data.Rows.Take(maxRows).ToList();
        var numeric = Enumerable.Range(0, columns).Select(c => data.Rows.Any(r => c < r.Length && ReportText.IsNumber(r[c]))).ToArray();

        // Width follows content, as in the PDF: names get room, numbers stay compact.
        var weights = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            var header = ReportText.Header(data.Columns[c]);
            var longestHeaderWord = header.Split(' ').Max(w => w.Length);
            var longestCell = shown.Select(r => c < r.Length ? ReportText.Cell(data.Columns[c], r[c]).Length : 0).DefaultIfEmpty(0).Max();
            weights[c] = Math.Clamp(Math.Max(longestHeaderWord, longestCell), 6, 32);
        }
        var total = weights.Sum();
        var widths = weights.Select(w => (int)Math.Round(ContentTwips * w / total)).ToArray();

        var table = NewTable(widths);
        table.Append(HeaderRow(data.Columns.Select(ReportText.Header).ToList(), widths, numeric));
        for (var r = 0; r < shown.Count; r++)
        {
            var fill = r % 2 == 1 ? Stripe : null;
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                var text = c < shown[r].Length ? ReportText.Cell(data.Columns[c], shown[r][c]) : "";
                row.Append(Cell(text, widths[c], numeric[c], fill: fill));
            }
            table.Append(row);
        }
        body.Append(table);

        if (data.Rows.Count > maxRows || data.Truncated)
            body.Append(Para($"Showing {shown.Count} of {data.Rows.Count}{(data.Truncated ? "+" : "")} rows. The Excel version has all rows.",
                size: 16, colour: Muted));
        body.Append(Para("", after: 120));
    }
}
