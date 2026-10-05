namespace MraReporting.Data;

/// <summary>
/// A "Code,Name" list kept by MRA staff in a CSV file next to appsettings.json. No EIS table holds
/// these names, so the file is the only source: a code without a name in the file is shown as the
/// bare code, never guessed. The file is re-read automatically when it changes, so edits need no restart.
/// Later these lists can move to tables in AI-REPORTING (TaxRates, CodeLookups).
/// </summary>
public abstract class CodeNameList
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly string _what;
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();
    private DateTime _loadedWriteTime = DateTime.MinValue;

    protected CodeNameList(IWebHostEnvironment env, ILogger logger, string fileName, string what)
    {
        _path = Path.Combine(env.ContentRootPath, fileName);
        _logger = logger;
        _what = what;
    }

    /// <summary>Code to name, only for codes that have a name filled in.</summary>
    public IReadOnlyDictionary<string, string> Names
    {
        get
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path)) return _names;
                    var writeTime = File.GetLastWriteTimeUtc(_path);
                    if (writeTime == _loadedWriteTime) return _names;

                    var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in File.ReadAllLines(_path).Skip(1))
                    {
                        var comma = line.IndexOf(',');
                        if (comma <= 0) continue;
                        var code = line[..comma].Trim().Trim('"');
                        var name = line[(comma + 1)..].Trim().Trim('"');
                        if (code.Length > 0 && name.Length > 0) names[code] = name;
                    }

                    _names = names;
                    _loadedWriteTime = writeTime;
                    _logger.LogInformation("Loaded {Count} {What} from {Path}", names.Count, _what, _path);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not read {Path}; keeping the previous names.", _path);
                }
                return _names;
            }
        }
    }

    /// <summary>"Name (CODE)" when a name is on file, otherwise the code alone.</summary>
    public string Label(string code) =>
        Names.TryGetValue(code.Trim(), out var name) ? $"{name} ({code.Trim()})" : code.Trim();
}

/// <summary>Official tax office names, from office-names.csv.</summary>
public sealed class OfficeDirectory(IWebHostEnvironment env, ILogger<OfficeDirectory> logger)
    : CodeNameList(env, logger, "office-names.csv", "tax office names");

/// <summary>Tax rate names for the RateID codes in InvoiceTaxBreakdown (A, B, E ...), from tax-rates.csv.</summary>
public sealed class RateDirectory(IWebHostEnvironment env, ILogger<RateDirectory> logger)
    : CodeNameList(env, logger, "tax-rates.csv", "tax rate names");
