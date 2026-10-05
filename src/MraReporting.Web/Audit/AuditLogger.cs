using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MraReporting.Infrastructure;

namespace MraReporting.Audit;

public sealed record AuditEntry(
    DateTime AskedAt,
    string UserName,
    string Kind,          // chat | export | report
    string? Question,
    string? ToolCallsJson,
    int ResultRows,
    long DurationMs,
    string? Answer,
    string? Error);

/// <summary>
/// Records every question, query and export. Writes to [AI-REPORTING].dbo.AuditLog when the
/// AiReporting connection string is set; until then (or if that write fails) it appends
/// JSON lines to logs/audit-yyyy-MM-dd.jsonl so nothing is lost.
/// </summary>
public sealed class AuditLogger
{
    private readonly string? _connectionString;
    private readonly string _folder;
    private readonly ILogger<AuditLogger> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public AuditLogger(IConfiguration configuration, IOptions<AppOptions> options, IWebHostEnvironment env, ILogger<AuditLogger> logger)
    {
        var cs = configuration.GetConnectionString("AiReporting");
        _connectionString = string.IsNullOrWhiteSpace(cs) ? null : cs;
        _folder = Path.Combine(env.ContentRootPath, options.Value.AuditFolder);
        _logger = logger;
    }

    public async Task<long?> WriteAsync(AuditEntry entry)
    {
        if (_connectionString is not null)
        {
            try
            {
                return await WriteToDatabaseAsync(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit write to AI-REPORTING failed; writing to file instead.");
            }
        }

        await WriteToFileAsync(entry);
        return null;
    }

    private async Task<long> WriteToDatabaseAsync(AuditEntry e)
    {
        const string sql = """
            INSERT INTO dbo.AuditLog (AskedAt, UserName, Kind, Question, ToolCalls, ResultRows, DurationMs, Answer, Error)
            VALUES (@AskedAt, @UserName, @Kind, @Question, @ToolCalls, @ResultRows, @DurationMs, @Answer, @Error);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@AskedAt", e.AskedAt);
        command.Parameters.AddWithValue("@UserName", e.UserName);
        command.Parameters.AddWithValue("@Kind", e.Kind);
        command.Parameters.AddWithValue("@Question", (object?)Truncate(e.Question, 2000) ?? DBNull.Value);
        command.Parameters.AddWithValue("@ToolCalls", (object?)e.ToolCallsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@ResultRows", e.ResultRows);
        command.Parameters.AddWithValue("@DurationMs", (int)Math.Min(e.DurationMs, int.MaxValue));
        command.Parameters.AddWithValue("@Answer", (object?)e.Answer ?? DBNull.Value);
        command.Parameters.AddWithValue("@Error", (object?)Truncate(e.Error, 2000) ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task WriteToFileAsync(AuditEntry entry)
    {
        await _fileLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, $"audit-{entry.AskedAt:yyyy-MM-dd}.jsonl");
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry, JsonDefaults.Options) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the audit file.");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
