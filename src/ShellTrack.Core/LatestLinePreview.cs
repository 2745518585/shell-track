using System.Text;

namespace ShellTrack.Core;

// Unlike a retained log, a card preview is bounded even for an unlimited line.
public sealed class LatestLinePreview
{
    private readonly TerminalTextDecoder decoder = new();
    private readonly StringBuilder current = new();
    private readonly int characterLimit;
    private long cursor, lineLength;
    private string previous = "";

    public LatestLinePreview(int characterLimit = 400)
    {
        if (characterLimit < 2) throw new ArgumentOutOfRangeException(nameof(characterLimit));
        this.characterLimit = characterLimit;
    }

    public string LastLine => current.Length == 0 ? previous : FormatCurrent();
    public void Append(ReadOnlySpan<byte> bytes) => decoder.Append(bytes, AppendCharacter);

    private void AppendCharacter(char c)
    {
        if (c == '\r') { cursor = 0; return; }
        if (c == '\b') { cursor = Math.Max(0, cursor - 1); return; }
        if (c == '\n')
        {
            if (current.Length > 0) previous = FormatCurrent();
            current.Clear(); cursor = lineLength = 0;
            return;
        }
        if (cursor < characterLimit)
        {
            if (cursor < current.Length) current[(int)cursor] = c;
            else current.Append(c);
        }
        cursor++;
        lineLength = Math.Max(lineLength, cursor);
    }

    private string FormatCurrent()
    {
        if (lineLength <= characterLimit) return current.ToString();
        int count = characterLimit - 1;
        if (char.IsHighSurrogate(current[count - 1])) count--;
        return current.ToString(0, count) + "…";
    }
}
