using System.Globalization;
using System.Text;

namespace ShellTrack.Core;

public sealed record NumberedLogLine(long Number, string Text);

// Bounded text projection: cursor edits update existing rows instead of adding
// every redraw to history. Colour and full-screen application state aren't rendered.
public sealed class TextLog
{
    private sealed class Line
    {
        public readonly StringBuilder Text = new();
        public int Width, PrefixWidth;
        public bool Ascii = true;
    }
    private readonly List<Line> lines = [];
    private readonly TerminalTextDecoder decoder = new();
    private readonly int retainedCharacterLimit, terminalRows;
    private readonly bool trimCurrentLine;
    private long cursorRow = 1, viewportTop = 1, savedRow;
    private int column, savedColumn, characterCount;
    private char pendingSurrogate;
    private bool positionKnown = true;
    public long FirstLineNumber { get; private set; } = 1;

    public TextLog(int retainedCharacterLimit = 1_000_000, bool trimCurrentLine = false, int terminalRows = 30)
    {
        if (retainedCharacterLimit < 1) throw new ArgumentOutOfRangeException(nameof(retainedCharacterLimit));
        if (terminalRows is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(terminalRows));
        this.retainedCharacterLimit = retainedCharacterLimit;
        this.trimCurrentLine = trimCurrentLine;
        this.terminalRows = terminalRows;
    }
    public void Append(ReadOnlySpan<byte> bytes)
    {
        decoder.Append(bytes, AppendCharacter, ApplySequence);
        TrimHistory();
    }
    private Line? CurrentLine(bool create = true)
    {
        if (cursorRow < FirstLineNumber) return null;
        int index = checked((int)(cursorRow - FirstLineNumber));
        if (create) while (lines.Count <= index) { if (lines.Count > 0) characterCount++; lines.Add(new()); }
        return index < lines.Count ? lines[index] : null;
    }
    private void AppendCharacter(char c)
    {
        if (char.IsHighSurrogate(c)) { pendingSurrogate = c; return; }
        Rune rune;
        if (pendingSurrogate != 0)
        {
            rune = char.IsLowSurrogate(c) ? new Rune(pendingSurrogate, c) : Rune.ReplacementChar;
            pendingSurrogate = '\0'; WriteRune(rune);
            if (char.IsLowSurrogate(c)) return;
        }
        if (c == '\r') { column = 0; return; }
        if (c == '\n')
        {
            CurrentLine(); cursorRow++; column = 0;
            viewportTop = Math.Max(viewportTop, cursorRow - terminalRows + 1);
            CurrentLine(); return;
        }
        if (c == '\b') { column = Math.Max(0, column - 1); return; }
        if (c == '\t') { int target = (column / 8 + 1) * 8; while (column < target) WriteRune(new Rune(' ')); return; }
        WriteRune(Rune.TryCreate(c, out rune) ? rune : Rune.ReplacementChar);
    }
    private static int CellWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        int value = rune.Value;
        return value is >= 0x1100 and <= 0x115f or >= 0x2329 and <= 0x232a
            or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff
            or >= 0xfe10 and <= 0xfe19 or >= 0xfe30 and <= 0xfe6f or >= 0xff01 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1faff or >= 0x20000 and <= 0x3fffd ? 2 : 1;
    }
    private static Rune ReadRune(StringBuilder text, int index) => char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
        ? new Rune(text[index], text[index + 1]) : new Rune(text[index]);
    private static int ColumnIndex(Line line, int target, out int actual)
    {
        actual = line.PrefixWidth;
        if (target >= line.Width) { actual = line.Width; return line.Text.Length; }
        if (line.Ascii) { actual = Math.Max(target, line.PrefixWidth); return Math.Max(0, target - line.PrefixWidth); }
        for (int i = 0; i < line.Text.Length;)
        {
            var rune = ReadRune(line.Text, i); int width = CellWidth(rune);
            if (width > 0 && actual + width > target) return i;
            actual += width; i += rune.Utf16SequenceLength;
            if (actual == target)
            {
                while (i < line.Text.Length && CellWidth(ReadRune(line.Text, i)) == 0) i += ReadRune(line.Text, i).Utf16SequenceLength;
                return i;
            }
        }
        return line.Text.Length;
    }
    private void WriteRune(Rune rune)
    {
        int width = CellWidth(rune);
        var line = CurrentLine();
        if (line is null || column < line.PrefixWidth) { column += width; return; }
        int before = line.Text.Length;
        if (rune.Value >= 0x7f) line.Ascii = false;
        if (column >= line.Width)
        {
            line.Text.Append(' ', column - line.Width);
            if (rune.IsBmp) line.Text.Append((char)rune.Value); else line.Text.Append(rune.ToString());
            line.Width = column + width;
        }
        else if (line.Ascii)
        {
            line.Text[column - line.PrefixWidth] = (char)rune.Value;
        }
        else
        {
            int start = ColumnIndex(line, column, out int startCell);
            int end = ColumnIndex(line, column + width, out int endCell);
            if (endCell < column + width && end < line.Text.Length)
            {
                var ending = ReadRune(line.Text, end); end += ending.Utf16SequenceLength; endCell += CellWidth(ending);
            }
            line.Text.Remove(start, end - start);
            string replacement = new string(' ', Math.Max(0, column - startCell)) + rune.ToString() + new string(' ', Math.Max(0, endCell - column - width));
            line.Text.Insert(start, replacement); line.Width = Math.Max(line.Width, column + width);
        }
        column += width; characterCount += line.Text.Length - before;
    }
    private void ApplySequence(char command, string parameters)
    {
        if (parameters == "ESC")
        {
            if (command == '7') { savedRow = cursorRow - viewportTop; savedColumn = column; }
            else if (command == '8') { cursorRow = viewportTop + savedRow; column = savedColumn; }
            return;
        }
        // Private modes such as cursor visibility and bracketed paste aren't text edits.
        if (parameters.Any(c => c is not (>= '0' and <= '9') and not ';')) return;
        string[] values = parameters.Split(';');
        int Value(int index, int fallback = 1) => index < values.Length && int.TryParse(values[index], out int value) ? Math.Clamp(value == 0 && fallback == 1 ? 1 : value, 0, 1000) : fallback;
        int count = Value(0);
        switch (command)
        {
            case 'H': case 'f':
                int row = Math.Min(terminalRows, count);
                if (!positionKnown) { viewportTop = cursorRow - row + 1; positionKnown = true; }
                cursorRow = viewportTop + row - 1; column = Value(1) - 1; break;
            case 'A': cursorRow = Math.Max(viewportTop, cursorRow - count); break;
            case 'B': cursorRow = Math.Min(viewportTop + terminalRows - 1, cursorRow + count); break;
            case 'C': column += count; break;
            case 'D': column = Math.Max(0, column - count); break;
            case 'G': column = count - 1; break;
            case 'd': cursorRow = viewportTop + Math.Min(terminalRows, count) - 1; break;
            case 'E': cursorRow = Math.Min(viewportTop + terminalRows - 1, cursorRow + count); column = 0; break;
            case 'F': cursorRow = Math.Max(viewportTop, cursorRow - count); column = 0; break;
            case 's': savedRow = cursorRow - viewportTop; savedColumn = column; break;
            case 'u': cursorRow = viewportTop + savedRow; column = savedColumn; break;
            case 'K': EraseLine(Value(0, 0)); break;
            case 'J':
                int mode = Value(0, 0);
                if (mode is < 0 or > 2) break;
                for (int i = 0; i < lines.Count; i++)
                {
                    long number = FirstLineNumber + i;
                    if (number < viewportTop || number >= viewportTop + terminalRows) continue;
                    if (number == cursorRow && mode != 2) { EraseLine(mode); continue; }
                    if (mode == 2 || mode == 0 && number > cursorRow || mode == 1 && number < cursorRow) Clear(lines[i]);
                }
                break;
            case 'X':
                int saved = column;
                int endColumn = Math.Min(column + count, CurrentLine(false)?.Width ?? column);
                while (column < endColumn) WriteRune(new Rune(' '));
                column = saved; break;
        }
        column = Math.Min(column, Math.Max(1000, CurrentLine(false)?.Width ?? 0));
    }
    private void Clear(Line line)
    {
        characterCount -= line.Text.Length; line.Text.Clear(); line.Width = line.PrefixWidth = 0; line.Ascii = true;
    }
    private void EraseLine(int mode)
    {
        var line = CurrentLine(false);
        if (line is null) return;
        int before = line.Text.Length;
        if (mode == 2) { Clear(line); return; }
        if (mode == 0)
        {
            int index = ColumnIndex(line, column, out int cell);
            if (column < line.PrefixWidth) { Clear(line); return; }
            line.Text.Remove(index, line.Text.Length - index); line.Width = cell;
        }
        else if (mode == 1)
        {
            int end = ColumnIndex(line, column + 1, out int cell);
            line.Text.Remove(0, end); line.Text.Insert(0, new string(' ', Math.Max(0, cell - line.PrefixWidth)));
        }
        characterCount += line.Text.Length - before;
    }
    private void TrimHistory()
    {
        if (characterCount <= retainedCharacterLimit) return;
        int target = (int)((long)retainedCharacterLimit * 3 / 4), remove = 0;
        while (remove < lines.Count - 1 && characterCount > target)
        {
            characterCount -= lines[remove].Text.Length + 1; remove++;
        }
        if (remove > 0) { lines.RemoveRange(0, remove); FirstLineNumber += remove; }
        if (trimCurrentLine && characterCount > retainedCharacterLimit && lines.Count == 1)
        {
            var line = lines[0]; int prefix = line.Text.Length - Math.Max(1, target);
            if (prefix < line.Text.Length && char.IsLowSurrogate(line.Text[prefix])) prefix++;
            for (int i = 0; i < prefix;) { var rune = ReadRune(line.Text, i); line.PrefixWidth += CellWidth(rune); i += rune.Utf16SequenceLength; }
            line.Text.Remove(0, prefix); characterCount -= prefix;
        }
    }
    public IReadOnlyList<NumberedLogLine> GetLines() => lines.Select((line, index) => new NumberedLogLine(FirstLineNumber + index, line.Text.ToString())).ToArray();
    public string LastLine
    {
        get
        {
            for (int i = lines.Count - 1; i >= 0; i--) if (lines[i].Text.Length > 0) return lines[i].Text.ToString();
            return "";
        }
    }
    public void ResetAfterGap()
    {
        decoder.Reset(); pendingSurrogate = '\0';
        cursorRow = FirstLineNumber + lines.Count;
        if (lines.Count > 0 && lines[^1].Text.Length == 0) cursorRow--;
        column = 0; viewportTop = cursorRow; positionKnown = false;
        savedRow = 0; savedColumn = 0;
        TrimHistory();
    }
    public override string ToString() => string.Join('\n', lines.Select(line => line.Text.ToString()));
}
