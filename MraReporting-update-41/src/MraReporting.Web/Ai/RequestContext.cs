using System.Collections.Concurrent;
using MraReporting.Data;

namespace MraReporting.Ai;

public sealed record ToolCallRecord(string Tool, string Arguments, int Rows, long DurationMs, string? Error);

/// <summary>A report the user can download: the browser shows it as buttons, one per format.</summary>
public sealed record ReportLink(string Label, string PdfUrl, string ExcelUrl, string WordUrl);

/// <summary>
/// Everything the tools produce during one chat request. The full QueryResults go to the
/// browser; the model only receives their text summaries. Created fresh for every request,
/// so nothing leaks between users.
/// </summary>
public sealed class RequestContext
{
    private readonly ConcurrentQueue<ToolCallRecord> _allCalls = new();

    public ConcurrentQueue<QueryResult> PendingResults { get; } = new();
    public ConcurrentQueue<ToolCallRecord> PendingCalls { get; } = new();
    public ConcurrentQueue<ReportLink> PendingReports { get; } = new();

    public void Record(ToolCallRecord call)
    {
        _allCalls.Enqueue(call);
        PendingCalls.Enqueue(call);
    }

    public IReadOnlyList<ToolCallRecord> AllCalls => _allCalls.ToArray();

    private readonly ConcurrentQueue<QueryResult> _allResults = new();

    /// <summary>Every result produced during this request, kept for the answer check.</summary>
    public IReadOnlyList<QueryResult> AllResults => _allResults.ToArray();

    public void AddResult(QueryResult result)
    {
        _allResults.Enqueue(result);
        PendingResults.Enqueue(result);
    }
}
