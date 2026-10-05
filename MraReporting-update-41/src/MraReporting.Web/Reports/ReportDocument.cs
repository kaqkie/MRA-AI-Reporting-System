using MraReporting.Data;

namespace MraReporting.Reports;

/// <summary>One numbered section: a short explanation, its charts, and its table.</summary>
/// <param name="TableInAppendix">Long tables (e.g. 30 daily rows) go to the appendix so they do not split the report.</param>
public sealed record ReportSection(
    string Heading,
    string Intro,
    QueryResult Data,
    IReadOnlyList<byte[]> Charts,
    bool TableInAppendix = false,
    int MaxPdfRows = 25);

/// <summary>A headline figure with its comparisons ("" where a comparison is not available).</summary>
public sealed record ReportKpi(string Label, string Value, string Previous = "", string Average = "", string Change = "");

/// <summary>A finished report, independent of output format. Writers turn it into PDF or Excel.</summary>
public sealed record ReportDocument(
    string Title,
    string Subtitle,
    DateTime GeneratedAt,
    string GeneratedBy,
    string Narrative,
    string? Banner,
    IReadOnlyList<string> KpiHeaders,   // Measure, value, previous, average, change
    IReadOnlyList<ReportKpi> Kpis,
    IReadOnlyList<ReportSection> Sections,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ReportParagraph>? Findings = null,
    bool Branded = false);   // summary reports: MRA logo and letterhead in the PDF   // "What the figures show": explained, in plain language
