using System.Text;

namespace ShellTrack.Core;

public sealed record NumberedLogLine(long Number, string Text);

// Incremental plain-text view, not a terminal emulator. A wrapped display line
// keeps its logical number, and retained history is trimmed only at newlines.
public sealed class TextLog
{
    private readonly StringBuilder text = new();
    private readonly TerminalTextDecoder decoder = new();
    private readonly int retainedCharacterLimit;
    private int cursor, lineStart;
    public long FirstLineNumber { get; private set; } = 1;

    public TextLog(int retainedCharacterLimit = 1_000_000)
    {
        if (retainedCharacterLimit < 1) throw new ArgumentOutOfRangeException(nameof(retainedCharacterLimit));
        this.retainedCharacterLimit = retainedCharacterLimit;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        decoder.Append(bytes, AppendCharacter);
        TrimHistory();
    }

    private void AppendCharacter(char c)
    {
        if (c == '\r') { cursor = lineStart; return; }
        if (c == '\n') { text.Append('\n'); cursor = lineStart = text.Length; return; }
        if (c == '\b') { cursor = Math.Max(lineStart, cursor - 1); return; }
        if (cursor < text.Length) text[cursor++] = c;
        else { text.Append(c); cursor++; }
    }

    private void TrimHistory()
    {
        if (text.Length <= retainedCharacterLimit) return;
        int target = (int)((long)retainedCharacterLimit * 3 / 4);
        int remove = 0;
        long lines = 0;
        for (int i = 0; i < lineStart && text.Length - remove > target; i++)
        {
            if (text[i] != '\n') continue;
            remove = i + 1;
            lines++;
        }
        if (remove == 0) return; // Preserve an oversized current logical line.
        text.Remove(0, remove);
        cursor -= remove;
        lineStart -= remove;
        FirstLineNumber += lines;
    }

    public IReadOnlyList<NumberedLogLine> GetLines()
    {
        if (text.Length == 0) return Array.Empty<NumberedLogLine>();
        var lines = new List<NumberedLogLine>();
        int start = 0;
        long number = FirstLineNumber;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(new(number++, text.ToString(start, i - start)));
            start = i + 1;
        }
        lines.Add(new(number, text.ToString(start, text.Length - start)));
        return lines;
    }

    // A task card should show the last output, even when it ends in a newline.
    public string LastLine
    {
        get
        {
            int end = text.Length;
            while (end > 0 && text[end - 1] == '\n') end--;
            if (end == 0) return "";
            int start = end;
            while (start > 0 && text[start - 1] != '\n') start--;
            return text.ToString(start, end - start);
        }
    }

    // A missing byte range may cut a UTF-8 character or escape sequence. Do not
    // let that partial state consume the next available output or overwrite it.
    public void ResetAfterGap()
    {
        decoder.Reset();
        if (text.Length > 0 && text[^1] != '\n') AppendCharacter('\n');
        cursor = lineStart = text.Length;
        TrimHistory();
    }

    public override string ToString() => text.ToString();
}
