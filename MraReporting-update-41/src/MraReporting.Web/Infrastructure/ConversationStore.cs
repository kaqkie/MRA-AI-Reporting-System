using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MraReporting.Infrastructure;

/// <summary>One line in the history list.</summary>
/// <summary>Where the search words were found, as a short snippet around them, per kind of text.</summary>
public sealed record SearchMatch(string? Best, string? Question, string? Answer, string? Table);

/// <param name="Match">Only when searching: snippets around the words that were found.</param>
public sealed record ConversationSummary(string Id, string Title, DateTime UpdatedAt, SearchMatch? Match = null);

/// <summary>
/// Saved conversations, one JSON file per conversation, in App_Data/history/&lt;user&gt;/.
/// Each user sees and deletes only their own. The folder name is a hash of the user name,
/// so names with odd characters (DOMAIN\user) are safe. Later this can move to AI-REPORTING.
/// The stored content is what the page shows: the questions, answers, tables and report links.
/// </summary>
public sealed class ConversationStore
{
    public const int MaxBytes = 5 * 1024 * 1024;     // one conversation, tables included
    private const int MaxListed = 200;

    private readonly string _root;

    public ConversationStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(env.ContentRootPath, "App_Data", "history");
    }

    public static bool ValidId(string? id) => Guid.TryParse(id, out _);

    /// <summary>The user's conversations, newest first. With search text, only those that contain it.</summary>
    public IReadOnlyList<ConversationSummary> List(string userName, string? search = null)
    {
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var dir = UserDir(userName);
        if (!Directory.Exists(dir)) return [];
        var list = new List<ConversationSummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var updated = root.TryGetProperty("updatedAt", out var u) && u.TryGetDateTime(out var d) ? d : File.GetLastWriteTimeUtc(file);
                SearchMatch? match = null;
                if (search is not null)
                {
                    match = FindMatch(root, title, search);
                    if (match is null) continue;
                }
                list.Add(new ConversationSummary(Path.GetFileNameWithoutExtension(file), title, updated, match));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // a damaged file is skipped, not fatal
            }
        }
        return list.OrderByDescending(c => c.UpdatedAt).Take(MaxListed).ToList();
    }

    /// <summary>
    /// Every word of the search must appear somewhere in the conversation: its title, a question,
    /// an answer, a table title or a name in a table. Returns a snippet around the words for each
    /// kind of text they were found in, so the search window can show and highlight them.
    /// </summary>
    private static SearchMatch? FindMatch(JsonElement root, string title, string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var questions = new List<string>();
        var answers = new List<string>();
        var tables = new List<string>();
        if (root.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in turns.EnumerateArray())
            {
                if (turn.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String) questions.Add(q.GetString()!);
                if (turn.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String) answers.Add(a.GetString()!);
                if (!turn.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) continue;
                foreach (var ev in events.EnumerateArray())
                {
                    if (!ev.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) continue;
                    if (result.TryGetProperty("title", out var rt) && rt.ValueKind == JsonValueKind.String) tables.Add(rt.GetString()!);
                    if (!result.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                    foreach (var row in rows.EnumerateArray().Take(500))
                        if (row.ValueKind == JsonValueKind.Array)
                            foreach (var cell in row.EnumerateArray())
                                if (cell.ValueKind == JsonValueKind.String) tables.Add(cell.GetString()!);
                }
            }
        }

        var all = string.Join("\n", new[] { title }.Concat(questions).Concat(answers).Concat(tables));
        if (!words.All(w => all.Contains(w, StringComparison.OrdinalIgnoreCase))) return null;

        var question = Snippet(questions, words);
        var answer = Snippet(answers, words);
        var table = Snippet(tables, words);
        var inTitle = words.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));
        var best = answer ?? question ?? table;
        if (inTitle && question is not null && question.Trim('.', ' ') == title.Trim()) best = answer ?? table; // the title already shows it
        return new SearchMatch(best, question, answer, table);
    }

    /// <summary>
    /// About 120 characters around the first search word, from the text that holds the most of the
    /// words, with "..." where it was cut. Markdown bold markers are removed.
    /// </summary>
    private static string? Snippet(List<string> texts, string[] words)
    {
        var text = texts
            .Select(t => (Text: t, Hits: words.Count(w => t.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Text)
            .FirstOrDefault();
        if (text is null) return null;

        text = text.Replace("**", "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        var at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 45);
        if (start > 0)
        {
            var space = text.IndexOf(' ', start);
            if (space > 0 && space < at) start = space + 1;
        }
        var end = Math.Min(text.Length, at + 80);
        if (end < text.Length)
        {
            var space = text.LastIndexOf(' ', end);
            if (space > at) end = space;
        }
        return (start > 0 ? "..." : "") + text[start..end].Trim() + (end < text.Length ? "..." : "");
    }

    public string? Get(string userName, string id)
    {
        var file = FilePath(userName, id);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>Saves the conversation sent by the page, stamping the id and the time on the server.</summary>
    public void Save(string userName, string id, JsonObject conversation, DateTime now)
    {
        conversation["id"] = id;
        conversation["updatedAt"] = now;
        var title = (conversation["title"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "").Trim();
        conversation["title"] = title.Length == 0 ? "Untitled conversation" : title.Length > 80 ? title[..80] : title;

        var file = FilePath(userName, id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, conversation.ToJsonString());
        File.Move(temp, file, overwrite: true);
    }

    public bool Delete(string userName, string id)
    {
        var file = FilePath(userName, id);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    public int DeleteAll(string userName)
    {
        var dir = UserDir(userName);
        if (!Directory.Exists(dir)) return 0;
        var files = Directory.GetFiles(dir, "*.json");
        foreach (var f in files) File.Delete(f);
        return files.Length;
    }

    private string UserDir(string userName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userName.Trim().ToLowerInvariant())))[..24];
        return Path.Combine(_root, hash.ToLowerInvariant());
    }

    private string FilePath(string userName, string id) =>
        Path.Combine(UserDir(userName), Guid.Parse(id).ToString("D") + ".json");
}
