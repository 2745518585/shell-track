using System.Text;
using System.Text.RegularExpressions;

namespace ShellTrack.Core;

public sealed record OutputNotificationMatch(string Pattern, string Text);

/// <summary>Matches a bounded, decoded terminal text window across read boundaries.</summary>
public sealed class OutputNotificationMatcher
{
    public const int WindowLimit = 65536, PatternLimit = 16, PatternLengthLimit = 2048;
    private readonly TerminalTextDecoder decoder = new();
    private readonly StringBuilder text = new();
    private readonly List<(string Pattern, Regex Regex)> patterns;
    private int cursor, lineStart;
    private char precedingCharacter;
    private bool trimmed;
    public string? Error { get; private set; }

    public OutputNotificationMatcher(IEnumerable<string> expressions)
    {
        string[] values = expressions.ToArray();
        if (values.Length > PatternLimit) throw new ArgumentException($"最多设置 {PatternLimit} 个输出正则条件。");
        patterns = [];
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > PatternLengthLimit)
                throw new ArgumentException($"输出正则不能为空，且不能超过 {PatternLengthLimit} 个字符。");
            try { patterns.Add((value, new Regex(value, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25)))); }
            catch (ArgumentException ex) { throw new ArgumentException("输出正则无效：" + ex.Message); }
        }
    }

    public OutputNotificationMatch? Append(ReadOnlySpan<byte> bytes)
    {
        if (patterns.Count == 0) return null;
        decoder.Append(bytes, AppendCharacter);
        string snapshot = trimmed ? precedingCharacter + text.ToString() : text.ToString();
        for (int i = 0; i < patterns.Count; i++)
        {
            var condition = patterns[i];
            try
            {
                var match = condition.Regex.Match(snapshot, trimmed ? 1 : 0);
                if (match.Success) return new(condition.Pattern, match.Value.Length <= 240 ? match.Value : match.Value[..240] + "…");
            }
            catch (RegexMatchTimeoutException)
            {
                Error = "输出正则匹配超时，已停用该条件：" + condition.Pattern;
                patterns.RemoveAt(i--); // Other conditions continue; never repeatedly stall output on this pattern.
            }
        }
        return null;
    }

    private void AppendCharacter(char c)
    {
        if (c == '\r') { cursor = lineStart; return; }
        if (c == '\b') { cursor = Math.Max(lineStart, cursor - 1); return; }
        if (c == '\n') { text.Append('\n'); cursor = lineStart = text.Length; }
        else if (cursor < text.Length) text[cursor++] = c;
        else { text.Append(c); cursor++; }
        if (text.Length <= WindowLimit) return;
        int remove = WindowLimit / 2;
        precedingCharacter = text[remove - 1]; trimmed = true;
        text.Remove(0, remove);
        cursor = Math.Max(0, cursor - remove); lineStart = Math.Max(0, lineStart - remove);
    }
}
