using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MraReporting.Data;
using MraReporting.Infrastructure;

namespace MraReporting.Ai;

public sealed record ChatMessageDto(string Role, string Content);

public sealed record ChatRequest(IReadOnlyList<ChatMessageDto> Messages);

/// <summary>One line of the NDJSON stream sent to the browser.</summary>
public sealed record ChatEvent(
    string Type,
    string? Text = null,
    QueryResult? Result = null,
    string? Tool = null,
    string? Arguments = null,
    string? Message = null,
    long? AuditId = null,
    ReportLink? Report = null)
{
    public static ChatEvent Delta(string text) => new("delta", Text: text);
    public static ChatEvent ResultOf(QueryResult result) => new("result", Result: result);
    public static ChatEvent ToolCall(ToolCallRecord call) => new("tool", Tool: call.Tool, Arguments: call.Arguments, Message: call.Error);
    public static ChatEvent Error(string message) => new("error", Message: message);
    public static ChatEvent Done(long? auditId) => new("done", AuditId: auditId);
    /// <summary>Tells the browser to discard the answer text streamed so far (a retry follows).</summary>
    public static ChatEvent Reset() => new("reset");
    public static ChatEvent ReportReady(ReportLink link) => new("report", Report: link);
    /// <summary>A visible caution shown above the answer.</summary>
    public static ChatEvent Warning(string message) => new("warning", Message: message);
}

public sealed record ChatOutcome(string Answer, IReadOnlyList<ToolCallRecord> Calls, int ResultRows);

/// <summary>
/// Runs one question: builds the prompt, lets the model call tools, and streams text,
/// tool calls and results back through <paramref name="emit"/> as they happen.
/// Conversation history is owned by the browser and sent with each request.
/// </summary>
public sealed class ChatService
{
    private readonly IChatClient _client;
    private readonly EisQueries _queries;
    private readonly StationDirectory _stations;
    private readonly OfficeDirectory _offices;
    private readonly AppClock _clock;
    private readonly AppOptions _appOptions;
    private readonly LlmOptions _llmOptions;
    private readonly ILogger<ReportingTools> _toolLogger;

    public ChatService(IChatClient client, EisQueries queries, StationDirectory stations, OfficeDirectory offices, AppClock clock,
        IOptions<AppOptions> appOptions, IOptions<LlmOptions> llmOptions, ILogger<ReportingTools> toolLogger)
    {
        _client = client;
        _queries = queries;
        _stations = stations;
        _offices = offices;
        _clock = clock;
        _appOptions = appOptions.Value;
        _llmOptions = llmOptions.Value;
        _toolLogger = toolLogger;
    }

    public async Task<ChatOutcome> RunAsync(ChatRequest request, Func<ChatEvent, Task> emit, CancellationToken ct)
    {
        var context = new RequestContext();
        var tools = new ReportingTools(_queries, _stations, _offices, context, _clock, _appOptions, _toolLogger, ct);

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt.Build(_clock.Now)) };
        foreach (var m in request.Messages.TakeLast(Math.Max(1, _llmOptions.MaxHistoryMessages)))
        {
            var role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User;
            messages.Add(new ChatMessage(role, m.Content));
        }

        // Offer only the lookups that fit this question (and the one before, for follow-ups): fewer tool
        // descriptions for the model to read means a much faster answer on a CPU.
        var allTools = tools.AsTools();
        var userTurns = request.Messages.Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)).Select(m => m.Content).ToList();
        var options = new ChatOptions
        {
            ModelId = _llmOptions.Model,
            Temperature = _llmOptions.Temperature,
            Tools = ToolRouter.Select(allTools, userTurns[^1], userTurns.Count > 1 ? userTurns[^2] : null),
            ToolMode = ChatToolMode.Auto,
        };
        var toolNames = allTools.Select(t => t.Name).ToArray();
        _toolLogger.LogInformation("[TOOLS] Offering {Count} of {All} lookups: {Names}", options.Tools.Count, allTools.Count, string.Join(", ", options.Tools.Select(t => t.Name)));
        var resultRows = 0;

        var answer = await StreamOnceAsync(messages, options, context, emit, rows => resultRows += rows, ct);

        // A small model sometimes writes "I need to call the X function..." instead of calling it.
        // If no tool ran and the reply talks about tools, throw that reply away and ask again,
        // this time requiring a tool call.
        if (context.AllCalls.Count == 0 && LooksLikeNarratedToolCall(answer, toolNames))
        {
            await emit(ChatEvent.Reset());
            var forced = options.Clone();
            forced.ToolMode = ChatToolMode.RequireAny;
            answer = await StreamOnceAsync(messages, forced, context, emit, rows => resultRows += rows, ct);
        }

        // Check every figure in the answer against the query results, in code. If something does not
        // match (an invented number, or an invented name for a tax office code), discard the answer and
        // ask again with the real results in front of the model. If it still fails, warn the user.
        var question = request.Messages[request.Messages.Count - 1].Content;
        var finding = AnswerGuard.Check(answer, context.AllResults, question, _offices.Names);
        if (!finding.IsClean)
        {
            _toolLogger.LogWarning("[GUARD] Answer rejected. Unmatched figures: {Numbers}. Code expansions: {Codes}",
                string.Join(", ", finding.UnmatchedNumbers), string.Join("; ", finding.CodeExpansions));
            await emit(ChatEvent.Reset());

            var retryMessages = new List<ChatMessage>(messages)
            {
                new(ChatRole.Assistant, answer),
                new(ChatRole.User, AnswerGuard.CorrectionMessage(finding, context.AllResults))
            };
            var retryOptions = options.Clone();
            if (context.AllResults.Count > 0)
            {
                retryOptions.Tools = null;               // answer from the results given, no new lookups
                retryOptions.ToolMode = ChatToolMode.None;
            }
            else
            {
                retryOptions.ToolMode = ChatToolMode.RequireAny;
            }

            answer = await StreamOnceAsync(retryMessages, retryOptions, context, emit, rows => resultRows += rows, ct);

            finding = AnswerGuard.Check(answer, context.AllResults, question, _offices.Names);
            if (!finding.IsClean)
            {
                _toolLogger.LogWarning("[GUARD] Retry still unmatched: {Numbers} {Codes}",
                    string.Join(", ", finding.UnmatchedNumbers), string.Join("; ", finding.CodeExpansions));
                var fallback = FactsAnswer(context.AllResults);
                if (fallback is not null)
                {
                    // Use the app's own exact facts instead of an answer that could not be verified.
                    await emit(ChatEvent.Reset());
                    await emit(ChatEvent.Delta(fallback));
                    answer = fallback;
                }
                else
                {
                    var problems = finding.UnmatchedNumbers.Concat(finding.CodeExpansions);
                    await emit(ChatEvent.Warning(
                        $"Check this answer: {string.Join(", ", problems)} could not be matched to the database results. Rely on the table, not the text."));
                }
            }
        }

        // A reply that names internal tools ("use the GetTaxpayerProfile tool") is not for the user:
        // ask once for a rewrite in plain words, and use the exact facts if it happens again.
        if (context.AllResults.Count > 0 && MentionsTools(answer, toolNames))
        {
            _toolLogger.LogWarning("[ANSWER] Reply mentioned internal tool names; asking for a rewrite.");
            await emit(ChatEvent.Reset());
            var rewrite = new List<ChatMessage>(messages)
            {
                new(ChatRole.Assistant, answer),
                new(ChatRole.User, "Rewrite your answer for an MRA officer. Do not mention tools, functions, code or system names. " +
                    "Explain the result in plain words using only the figures already given, and if something cannot be answered, say what the user can ask instead.")
            };
            var rewriteOptions = options.Clone();
            rewriteOptions.Tools = null;
            rewriteOptions.ToolMode = ChatToolMode.None;
            answer = await StreamOnceAsync(rewrite, rewriteOptions, context, emit, rows => resultRows += rows, ct);
            if (MentionsTools(answer, toolNames) && FactsAnswer(context.AllResults) is { } plain)
            {
                await emit(ChatEvent.Reset());
                await emit(ChatEvent.Delta(plain));
                answer = plain;
            }
        }

        // 3. A reply that stops after a few words is no explanation: use the app's exact facts instead.
        if (context.AllResults.Count > 0 && LooksUnfinished(answer) && FactsAnswer(context.AllResults) is { } facts)
        {
            _toolLogger.LogWarning("[ANSWER] Reply too short or unfinished ({Length} characters); using the key facts.", answer.Trim().Length);
            await emit(ChatEvent.Reset());
            await emit(ChatEvent.Delta(facts));
            answer = facts;
        }

        // Last line of defence: never show the user a sentence that names an internal tool.
        if (MentionsTools(answer, toolNames))
        {
            var cleaned = StripToolSentences(answer, toolNames);
            if (cleaned.Length < 40)
                cleaned = FactsAnswer(context.AllResults)
                          ?? "The result is shown below. Ask a more specific question if you need a figure that is not in it.";
            await emit(ChatEvent.Reset());
            await emit(ChatEvent.Delta(cleaned));
            answer = cleaned;
            var recheck = AnswerGuard.Check(answer, context.AllResults, question, _offices.Names);
            if (!recheck.IsClean)
                await emit(ChatEvent.Warning(
                    $"Check this answer: {string.Join(", ", recheck.UnmatchedNumbers.Concat(recheck.CodeExpansions))} could not be matched to the database results. Rely on the table, not the text."));
        }

        // Never finish with no text: after a discarded draft the user would be left with "Looking that up...".
        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = FactsAnswer(context.AllResults) ?? EmptyAnswer(context.AllResults);
            _toolLogger.LogWarning("[ANSWER] The model gave no text; using a plain description of the results.");
            await emit(ChatEvent.Reset());
            await emit(ChatEvent.Delta(answer));
        }

        return new ChatOutcome(answer, context.AllCalls, resultRows);
    }

    /// <summary>
    /// Sends the full instructions and tool list to the model once, with a one-word question and no
    /// tool use, so llama-server has read them before the first real question. llama-server keeps what
    /// it has read, so the first user then gets the fast path. Run when the app starts.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        var tools = new ReportingTools(_queries, _stations, _offices, new RequestContext(), _clock, _appOptions, _toolLogger, ct);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt.Build(_clock.Now)),
            new(ChatRole.User, "Hello")
        };
        var options = new ChatOptions
        {
            ModelId = _llmOptions.Model,
            Temperature = _llmOptions.Temperature,
            Tools = ToolRouter.Select(tools.AsTools(), "", null),   // the core lookups every question starts with
            ToolMode = ChatToolMode.Auto,
            MaxOutputTokens = 1,
        };
        await _client.GetResponseAsync(messages, options, ct);
    }

    private async Task<string> StreamOnceAsync(List<ChatMessage> messages, ChatOptions options, RequestContext context,
        Func<ChatEvent, Task> emit, Action<int> countRows, CancellationToken ct)
    {
        var filter = new ThinkTagFilter();
        var answer = new StringBuilder();

        async Task FlushToolOutput()
        {
            var looked = false;
            while (context.PendingCalls.TryDequeue(out var call))
            {
                // Text written before a lookup ("Let me retrieve that...") is not the answer: drop it,
                // so the reply that remains is the explanation of the result.
                if (!looked && answer.Length > 0)
                {
                    answer.Clear();
                    await emit(ChatEvent.Reset());
                }
                looked = true;
                await emit(ChatEvent.ToolCall(call));
            }
            while (context.PendingResults.TryDequeue(out var result))
            {
                countRows(result.Rows.Count);
                await emit(ChatEvent.ResultOf(result));
            }
            while (context.PendingReports.TryDequeue(out var link))
                await emit(ChatEvent.ReportReady(link));
        }

        await foreach (var update in _client.GetStreamingResponseAsync(messages, options, ct))
        {
            await FlushToolOutput();
            var text = filter.Process(update.Text ?? "");
            if (text.Length > 0)
            {
                answer.Append(text);
                await emit(ChatEvent.Delta(text));
            }
        }

        await FlushToolOutput();
        var tail = filter.Flush();
        if (tail.Length > 0)
        {
            answer.Append(tail);
            await emit(ChatEvent.Delta(tail));
        }
        return answer.ToString();
    }

    private static readonly Regex NarrationPattern = new(
        @"\b(call|calling|use|using|invoke|run)\b[^.]{0,40}\b(function|tool)\b|<tool_call>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool LooksUnfinished(string answer)
    {
        var text = answer.Trim();
        return text.Length < 90 || text.EndsWith(':') || !(text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') || text.EndsWith(')') || text.EndsWith('*'));
    }

    /// <summary>What to say when there is nothing else: name each result and whether it had data.</summary>
    private static string EmptyAnswer(IReadOnlyList<QueryResult> results)
    {
        if (results.Count == 0)
            return "I could not find an answer to that. Try asking it another way, for example with the business name or TIN and the period you mean.";
        return string.Join("\n\n", results.Select(r => r.Rows.Count == 0
            ? $"{r.Title.TrimEnd('.')}: no data was found. {r.Note}".Trim()
            : $"{r.Title.TrimEnd('.')}: the table below shows the {r.Rows.Count} row{(r.Rows.Count == 1 ? "" : "s")} found."));
    }

    /// <summary>A plain explanation built only from the key facts the app worked out for each result.</summary>
    private static string? FactsAnswer(IReadOnlyList<QueryResult> results)
    {
        var parts = results
            .Where(r => r.Insights is { Count: > 0 })
            .Select(r => r.Title.TrimEnd('.') + ". " + string.Join(" ", r.Insights!))
            .ToList();
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static bool MentionsTools(string text, IEnumerable<string> toolNames) =>
        toolNames.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)) ||
        Regex.IsMatch(text, @"\b(the|this|a)\s+`?\w*`?\s*(tool|function)\b", RegexOptions.IgnoreCase);

    /// <summary>Removes every sentence (and "Next step" line) that names a tool or talks about tools or functions.</summary>
    private static string StripToolSentences(string text, IEnumerable<string> toolNames)
    {
        var names = toolNames.ToArray();
        bool Bad(string sentence) =>
            names.Any(n => sentence.Contains(n, StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(sentence, @"\b(tools?|functions?)\b|`", RegexOptions.IgnoreCase);

        var paragraphs = text.Replace("\r", "").Split('\n')
            .Select(line => string.Concat(Regex.Split(line, @"(?<=[.!?])\s+").Where(sentence => !Bad(sentence)).Select(x => x + " ")).Trim())
            .ToList();
        return Regex.Replace(string.Join("\n", paragraphs), @"\n{3,}", "\n\n").Trim();
    }

    private static bool LooksLikeNarratedToolCall(string text, IEnumerable<string> toolNames) =>
        toolNames.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)) || NarrationPattern.IsMatch(text);
}
