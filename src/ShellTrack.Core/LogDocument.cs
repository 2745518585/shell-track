namespace ShellTrack.Core;

// Text positions use UTF-16, matching the Windows text document APIs. Logical
// lines are separated by one CR; visual wrapping never enters the copied text.
public sealed class LogDocument
{
    public string Text { get; private set; } = "";
    public IReadOnlyList<NumberedLogLine> Lines { get; private set; } = [];
    public int[] Starts { get; private set; } = [];

    public int Update(IReadOnlyList<NumberedLogLine> lines)
    {
        string next = string.Join('\r', lines.Select(l => l.Text));
        int prefix = 0;
        while (prefix < Math.Min(Text.Length, next.Length) && Text[prefix] == next[prefix]) prefix++;
        if (prefix > 0 && prefix < next.Length && char.IsLowSurrogate(next[prefix])) prefix--;
        Lines = lines; Starts = new int[lines.Count];
        for (int i = 1; i < lines.Count; i++) Starts[i] = Starts[i - 1] + lines[i - 1].Text.Length + 1;
        Text = next;
        return prefix;
    }
    public int LineAt(int position)
    {
        if (Starts.Length == 0) return -1;
        int index = Array.BinarySearch(Starts, Math.Clamp(position, 0, Text.Length));
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }
    public string SelectedText(int start, int end)
    {
        int from = Math.Clamp(Math.Min(start, end), 0, Text.Length);
        int to = Math.Clamp(Math.Max(start, end), 0, Text.Length);
        return Text[from..to].Replace('\r', '\n');
    }
}
