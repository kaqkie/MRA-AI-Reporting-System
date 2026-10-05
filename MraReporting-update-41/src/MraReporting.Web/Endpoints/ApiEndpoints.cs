using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MraReporting.Ai;
using MraReporting.Audit;
using MraReporting.Data;
using MraReporting.Infrastructure;
using MraReporting.Reports;

namespace MraReporting.Endpoints;

public static class ApiEndpoints
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>"pdf", "xlsx" or "docx" from a format name the browser sends; anything else means PDF.</summary>
    private static string NormaliseFormat(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "xlsx" or "excel" => "xlsx",
        "docx" or "word" => "docx",
        _ => "pdf"
    };

    private static string ContentType(string format) => format switch
    {
        "xlsx" => XlsxType,
        "docx" => DocxType,
        _ => "application/pdf"
    };

    public static void MapApiEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat", HandleChatAsync);
        app.MapPost("/api/export/{format}", HandleExportAsync);
        app.MapGet("/api/reports/daily-summary", HandleSummaryAsync);
        app.MapGet("/api/reports/summary", HandleSummaryAsync);

        // ---------------- saved conversations (each user sees only their own)
        app.MapGet("/api/history", (string? q, HttpContext http, ConversationStore store, UserContextAccessor users) =>
            Results.Ok(store.List(users.Get(http).UserName, q is { Length: > 100 } ? q[..100] : q)));

        app.MapGet("/api/history/{id}", (string id, HttpContext http, ConversationStore store, UserContextAccessor users) =>
        {
            if (!ConversationStore.ValidId(id)) return Results.BadRequest("Bad conversation id.");
            var json = store.Get(users.Get(http).UserName, id);
            return json is null ? Results.NotFound() : Results.Content(json, "application/json");
        });

        app.MapPut("/api/history/{id}", async (string id, HttpContext http, ConversationStore store, UserContextAccessor users, AppClock clock) =>
        {
            if (!ConversationStore.ValidId(id)) return Results.BadRequest("Bad conversation id.");
            if (http.Request.ContentLength > ConversationStore.MaxBytes) return Results.BadRequest("Conversation too large to save.");
            System.Text.Json.Nodes.JsonObject? body;
            try
            {
                body = await System.Text.Json.Nodes.JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as System.Text.Json.Nodes.JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body is null) return Results.BadRequest("No conversation to save.");
            store.Save(users.Get(http).UserName, id, body, clock.Now);
            return Results.NoContent();
        });

        app.MapDelete("/api/history/{id}", (string id, HttpContext http, ConversationStore store, UserContextAccessor users) =>
        {
            if (!ConversationStore.ValidId(id)) return Results.BadRequest("Bad conversation id.");
            return store.Delete(users.Get(http).UserName, id) ? Results.NoContent() : Results.NotFound();
        });

        app.MapDelete("/api/history", (HttpContext http, ConversationStore store, UserContextAccessor users) =>
            Results.Ok(new { deleted = store.DeleteAll(users.Get(http).UserName) }));
    }

    // ---------------------------------------------------------------- chat (streams NDJSON)

    private static async Task HandleChatAsync(HttpContext http, ChatService chat, AuditLogger audit,
        UserContextAccessor users, AppClock clock, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("Chat");
        var ct = http.RequestAborted;

        ChatRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<ChatRequest>(JsonDefaults.Options, ct);
        }
        catch (JsonException)
        {
            request = null;
        }

        var question = request?.Messages?.LastOrDefault();
        if (question is null || !string.Equals(question.Role, "user", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(question.Content) || question.Content.Length > 1000)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Send a question of up to 1,000 characters.", ct);
            return;
        }

        var user = users.Get(http);
        var askedAt = clock.Now;
        var watch = Stopwatch.StartNew();

        http.Response.ContentType = "application/x-ndjson; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";

        async Task Emit(ChatEvent e)
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(e, JsonDefaults.Options) + "\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }

        ChatOutcome? outcome = null;
        string? error = null;
        try
        {
            outcome = await chat.RunAsync(request!, Emit, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error = "Cancelled by the user.";
        }
        catch (HttpRequestException ex)
        {
            error = "The AI model server could not be reached. Check that llama-server is running (see README).";
            logger.LogError(ex, "llama-server not reachable");
        }
        catch (ClientResultException ex)
        {
            error = ex.Status == 0
                ? "The AI model server could not be reached. Check that llama-server is running (see README)."
                : $"The AI model server returned an error ({ex.Status}).";
            logger.LogError(ex, "llama-server returned an error");
        }
        catch (Exception ex) when (IsModelUnreachable(ex))
        {
            // The OpenAI client retries and then wraps the failure in an AggregateException.
            error = "The AI model server could not be reached. Check that llama-server is running (see README).";
            logger.LogError(ex, "llama-server not reachable");
        }
        catch (Exception ex)
        {
            error = "Something went wrong while answering. The error has been logged.";
            logger.LogError(ex, "Chat request failed");
        }

        long? auditId = await audit.WriteAsync(new AuditEntry(
            askedAt, user.UserName, "chat", question.Content,
            outcome is null ? null : JsonSerializer.Serialize(outcome.Calls, JsonDefaults.Options),
            outcome?.ResultRows ?? 0, watch.ElapsedMilliseconds, outcome?.Answer, error));

        if (ct.IsCancellationRequested) return;
        if (error is not null) await Emit(ChatEvent.Error(error));
        await Emit(ChatEvent.Done(auditId));
    }

    // ---------------------------------------------------------------- download of one chat result (Excel, PDF or Word)

    private static async Task<IResult> HandleExportAsync(HttpContext http, string format, AuditLogger audit, UserContextAccessor users,
        AppClock clock, ILoggerFactory loggers)
    {
        var kind = NormaliseFormat(format);
        QueryResult? result;
        try
        {
            result = await http.Request.ReadFromJsonAsync<QueryResult>(JsonDefaults.Options, http.RequestAborted);
        }
        catch (JsonException)
        {
            result = null;
        }
        if (result is null || result.Columns is null || result.Rows is null)
            return Results.BadRequest("No result to export.");
        if (result.Rows.Count > 100_000)
            return Results.BadRequest("Result too large to export.");

        var userName = users.Get(http).UserName;
        byte[] bytes;
        try
        {
            bytes = kind switch
            {
                "xlsx" => ExcelWriter.WriteSingle(result),
                "docx" => WordWriter.WriteReport(SingleResultReport.Build(result, userName, clock.Now)),
                _ => PdfWriter.WriteReport(SingleResultReport.Build(result, userName, clock.Now))
            };
        }
        catch (Exception ex)
        {
            loggers.CreateLogger("Export").LogError(ex, "Export to {Format} failed for {Title}", kind, result.Title);
            return Results.Problem("The file could not be created. The error has been logged.");
        }

        await audit.WriteAsync(new AuditEntry(clock.Now, userName, "export", $"{result.Title} ({kind})", null,
            result.Rows.Count, 0, null, null));

        return Results.File(bytes, ContentType(kind), $"{SafeFileName(result.Title)}.{kind}");
    }

    // ---------------------------------------------------------------- summary report (one day or a period)

    /// <summary>
    /// /api/reports/summary?from=2026-01-01&amp;to=2026-01-31&amp;format=pdf, or ?date=2026-01-31 for one day.
    /// With no dates, the report covers yesterday.
    /// </summary>
    private static async Task<IResult> HandleSummaryAsync(HttpContext http, string? date, string? from, string? to, string? format,
        DailySummaryReport report, AuditLogger audit, UserContextAccessor users, AppClock clock,
        Microsoft.Extensions.Options.IOptions<AppOptions> options, ILoggerFactory loggers)
    {
        static bool TryDate(string? text, out DateOnly value) =>
            DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

        var start = clock.Today.AddDays(-1);
        var end = start;
        if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
        {
            if (!TryDate(from, out start) || !TryDate(to ?? from, out end))
                return Results.BadRequest("from and to must be dates in yyyy-MM-dd format.");
        }
        else if (!string.IsNullOrWhiteSpace(date))
        {
            if (!TryDate(date, out start)) return Results.BadRequest("date must be yyyy-MM-dd.");
            end = start;
        }

        if (end < start) (start, end) = (end, start);
        if (start > clock.Today) return Results.BadRequest("The report period cannot start in the future.");
        if (end > clock.Today) end = clock.Today;
        var days = end.DayNumber - start.DayNumber + 1;
        if (days > options.Value.MaxRangeDays)
            return Results.BadRequest($"The report period is {days} days; the maximum is {options.Value.MaxRangeDays}.");

        var kind = NormaliseFormat(format);
        var user = users.Get(http);
        var askedAt = clock.Now;
        var watch = Stopwatch.StartNew();
        var label = start == end ? $"{start:yyyy-MM-dd}" : $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}";

        try
        {
            var doc = await report.BuildAsync(start, end, user.UserName, http.RequestAborted);
            var bytes = kind switch
            {
                "xlsx" => ExcelWriter.WriteReport(doc),
                "docx" => WordWriter.WriteReport(doc),
                _ => PdfWriter.WriteReport(doc)
            };

            await audit.WriteAsync(new AuditEntry(askedAt, user.UserName, "report", $"Summary report {label} ({kind})",
                null, doc.Sections.Sum(s => s.Data.Rows.Count), watch.ElapsedMilliseconds, null, null));

            var fileName = start == end
                ? $"MRA-daily-summary-{start:yyyy-MM-dd}.{kind}"
                : $"MRA-summary-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}.{kind}";
            return Results.File(bytes, ContentType(kind), fileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("Reports").LogError(ex, "Summary report failed for {Period}", label);
            await audit.WriteAsync(new AuditEntry(askedAt, user.UserName, "report", $"Summary report {label}",
                null, 0, watch.ElapsedMilliseconds, null, ex.Message));
            return Results.Problem("The report could not be generated. The error has been logged.");
        }
    }

    private static string SafeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(title.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return clean.Length > 80 ? clean[..80] : clean.Length == 0 ? "result" : clean;
    }

    /// <summary>True when the exception (or anything it wraps) means llama-server could not be reached.</summary>
    private static bool IsModelUnreachable(Exception? ex)
    {
        for (int depth = 0; ex is not null && depth < 10; depth++)
        {
            switch (ex)
            {
                case System.Net.Sockets.SocketException:
                case HttpRequestException:
                case ClientResultException { Status: 0 }:
                    return true;
                case AggregateException agg when agg.InnerExceptions.Any(IsModelUnreachable):
                    return true;
            }
            ex = ex.InnerException;
        }
        return false;
    }
}
