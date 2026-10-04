namespace ShellTrack.Core;

// Use the same cursor-aware projection as the viewer so prompt redraws don't
// become a different, duplicated summary in task metadata.
public sealed class LatestLinePreview
{
    private readonly TextLog log;
    private readonly int characterLimit;
    public LatestLinePreview(int characterLimit = 400, int terminalRows = 30)
    {
        if (characterLimit < 2) throw new ArgumentOutOfRangeException(nameof(characterLimit));
        this.characterLimit = characterLimit;
        log = new TextLog(Math.Max(32768, characterLimit), trimCurrentLine: true, terminalRows: terminalRows);
    }
    public string LastLine
    {
        get
        {
            string value = log.LastLine;
            if (value.Length <= characterLimit) return value;
            int count = characterLimit - 1;
            if (char.IsHighSurrogate(value[count - 1])) count--;
            return value[..count] + "…";
        }
    }
    public void Append(ReadOnlySpan<byte> bytes) => log.Append(bytes);
}
