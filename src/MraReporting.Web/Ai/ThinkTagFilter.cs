using System.Text;

namespace MraReporting.Ai;

/// <summary>
/// Qwen3 in no-think mode still emits an empty &lt;think&gt;&lt;/think&gt; block. This removes any
/// think blocks from a streamed reply, even when a tag is split across two chunks, and trims
/// the leading blank lines that follow it.
/// </summary>
public sealed class ThinkTagFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private bool _insideThink;
    private bool _startedOutput;

    public string Process(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return "";
        _pending.Append(chunk);
        var text = _pending.ToString();
        _pending.Clear();

        var output = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (_insideThink)
            {
                var end = text.IndexOf(Close, i, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Keep the tail in case "</think>" is split across chunks.
                    var keepFrom = Math.Max(i, text.Length - (Close.Length - 1));
                    _pending.Append(text, keepFrom, text.Length - keepFrom);
                    return Emit(output);
                }
                _insideThink = false;
                i = end + Close.Length;
            }
            else
            {
                var start = text.IndexOf(Open, i, StringComparison.Ordinal);
                if (start < 0)
                {
                    var keep = Math.Min(PartialPrefixAtEnd(text, Open), text.Length - i);
                    output.Append(text, i, text.Length - i - keep);
                    _pending.Append(text, text.Length - keep, keep);
                    return Emit(output);
                }
                output.Append(text, i, start - i);
                _insideThink = true;
                i = start + Open.Length;
            }
        }
        return Emit(output);
    }

    /// <summary>Call once the stream ends to release any text held back.</summary>
    public string Flush()
    {
        var rest = _insideThink ? new StringBuilder() : new StringBuilder(_pending.ToString());
        _pending.Clear();
        return Emit(rest);
    }

    private string Emit(StringBuilder output)
    {
        var s = output.ToString();
        if (!_startedOutput)
        {
            s = s.TrimStart();
            if (s.Length > 0) _startedOutput = true;
        }
        return s;
    }

    private static int PartialPrefixAtEnd(string text, string tag)
    {
        for (var k = Math.Min(tag.Length - 1, text.Length); k > 0; k--)
            if (text.EndsWith(tag[..k], StringComparison.Ordinal))
                return k;
        return 0;
    }
}
