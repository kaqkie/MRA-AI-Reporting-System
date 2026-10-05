using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MraReporting.Infrastructure;

namespace MraReporting.Data;

/// <summary>
/// Runs parameterized, read-only queries against STAGING_SSIS and returns them as QueryResult.
/// This is the only class in the app that talks to the EIS database.
/// </summary>
public sealed class SqlRunner
{
    private readonly string _connectionString;
    private readonly AppOptions _options;
    private readonly AppClock _clock;

    public SqlRunner(IConfiguration configuration, IOptions<AppOptions> options, AppClock clock)
    {
        _connectionString = configuration.GetConnectionString("Staging")
            ?? throw new InvalidOperationException("ConnectionStrings:Staging is not set in appsettings.json.");
        _options = options.Value;
        _clock = clock;
    }

    public async Task<QueryResult> QueryAsync(
        string title,
        string sql,
        IEnumerable<SqlParameter> parameters,
        VisualKind visual,
        CancellationToken ct,
        string? note = null,
        IReadOnlyList<string>? chartValueColumns = null,
        int? maxRows = null)
    {
        var cap = maxRows ?? _options.MaxRows;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = _options.CommandTimeoutSeconds
        };
        foreach (var p in parameters) command.Parameters.Add(p);

        var rows = new List<object?[]>();
        var truncated = false;
        List<string> columns;

        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= cap)
                {
                    truncated = true;
                    command.Cancel(); // stop the server sending the rest instead of draining it
                    break;
                }

                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                    values[i] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i));
                rows.Add(values);
            }
        }

        return new QueryResult(title, columns, rows, visual, _clock.Now, truncated, note, chartValueColumns);
    }

    /// <summary>Makes values serialize cleanly: dates without a time become yyyy-MM-dd, floats are rounded.</summary>
    private static object? Normalize(object value) => value switch
    {
        DateTime d when d.TimeOfDay == TimeSpan.Zero => DateOnly.FromDateTime(d),
        double d => Math.Round(d, 4),
        float f => Math.Round((double)f, 4),
        byte b => (int)b,
        _ => value
    };

    public static SqlParameter Date(string name, DateOnly value) =>
        new(name, SqlDbType.DateTime) { Value = value.ToDateTime(TimeOnly.MinValue) };

    public static SqlParameter Int(string name, int value) =>
        new(name, SqlDbType.Int) { Value = value };

    public static SqlParameter VarChar(string name, string value, int size) =>
        new(name, SqlDbType.VarChar, size) { Value = value };
}
