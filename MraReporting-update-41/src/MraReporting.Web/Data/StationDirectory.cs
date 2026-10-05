using System.Globalization;

namespace MraReporting.Data;

/// <summary>
/// Customs station names, read from the ePayment customs liabilities table (staging.Epay_CustomsLiability).
/// That table has variant spellings and blanks for some codes, so for each code the most common real name is
/// used (ignoring NULLs and names that just repeat the code), and codes with fewer than 5 rows (test entries
/// such as DED_C) are dropped. Cached for 6 hours.
/// </summary>
public sealed class StationDirectory
{
    private const string NamesSql = """
        WITH n AS (
            SELECT LTRIM(RTRIM(OfficeCode)) AS Code, LTRIM(RTRIM(OfficeName)) AS Name, COUNT_BIG(*) AS Cnt
            FROM staging.Epay_CustomsLiability
            WHERE OfficeCode IS NOT NULL AND OfficeName IS NOT NULL
              AND LTRIM(RTRIM(OfficeName)) <> ''
              AND LTRIM(RTRIM(OfficeName)) <> LTRIM(RTRIM(OfficeCode))
            GROUP BY LTRIM(RTRIM(OfficeCode)), LTRIM(RTRIM(OfficeName))
        ), r AS (
            SELECT Code, Name,
                   ROW_NUMBER() OVER (PARTITION BY Code ORDER BY Cnt DESC) AS rn,
                   SUM(Cnt) OVER (PARTITION BY Code) AS CodeRows
            FROM n
        )
        SELECT Code, Name FROM r WHERE rn = 1 AND CodeRows >= 5 ORDER BY Code;
        """;

    // Words people add that are not part of what identifies the station.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
        { "border", "station", "post", "customs", "office", "the", "at", "port", "international" };

    private readonly SqlRunner _sql;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyDictionary<string, string>? _customs;
    private DateTime _loadedAt;

    public StationDirectory(SqlRunner sql) => _sql = sql;

    /// <summary>Customs station code to display name, e.g. SWE to "Songwe Border".</summary>
    public async Task<IReadOnlyDictionary<string, string>> CustomsNamesAsync(CancellationToken ct)
    {
        if (_customs is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromHours(6))
            return _customs;

        await _lock.WaitAsync(ct);
        try
        {
            if (_customs is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromHours(6))
                return _customs;

            var result = await _sql.QueryAsync("Customs station names", NamesSql, [], VisualKind.None, ct, maxRows: 1000);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in result.Rows)
                if (row[0] is string code && row[1] is string name)
                    names[code] = TitleCase(name);

            _customs = names;
            _loadedAt = DateTime.UtcNow;
            return names;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Finds customs station codes for what a user typed: an exact code ("SWE"), or a name where every
    /// meaningful word matches ("Songwe", "Mwanza border", "Kamuzu airport").
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveCustomsAsync(string input, CancellationToken ct)
    {
        var names = await CustomsNamesAsync(ct);
        var text = input.Trim();
        if (names.ContainsKey(text)) return [names.Keys.First(k => k.Equals(text, StringComparison.OrdinalIgnoreCase))];

        var words = text.Split(new[] { ' ', ',', '-', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !NoiseWords.Contains(w))
            .ToList();
        if (words.Count == 0) return [];

        return names
            .Where(kv => words.All(w => kv.Value.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .ToList();
    }

    private static string TitleCase(string name) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Trim().ToLowerInvariant());
}
