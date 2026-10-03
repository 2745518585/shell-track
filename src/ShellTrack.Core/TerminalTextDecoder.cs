using System.Buffers;
using System.Text;

namespace ShellTrack.Core;

// Shares UTF-8 and ANSI state between the full log and bounded task previews.
internal sealed class TerminalTextDecoder
{
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private int escapeState;
    private readonly StringBuilder control = new();
    private bool lineHasText;

    public void Append(ReadOnlySpan<byte> bytes, Action<char> consume)
    {
        if (bytes.IsEmpty) return;
        char[] chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
        try
        {
            int count = decoder.GetChars(bytes, chars, false);
            foreach (char c in chars.AsSpan(0, count))
            {
                if (escapeState == 1)
                {
                    control.Clear();
                    escapeState = c == '[' ? 2 : c == ']' ? 3 : c is >= '\x20' and <= '\x2f' ? 5 : 0;
                    continue;
                }
                if (escapeState == 2)
                {
                    if (c is >= '@' and <= '~')
                    {
                        // ConPTY may replace a line break with a move to the next
                        // row. Preserve that boundary in this append-only log view.
                        // This intentionally does not emulate arbitrary cursor edits.
                        if (c is 'H' or 'f' && lineHasText)
                        {
                            string[] position = control.ToString().Split(';');
                            if (position.Length < 2 || position[1] is "" or "1")
                            {
                                consume('\n'); lineHasText = false;
                            }
                        }
                        control.Clear(); escapeState = 0;
                    }
                    else if (control.Length < 128) control.Append(c);
                    continue;
                }
                if (escapeState == 3) { if (c == '\a') escapeState = 0; else if (c == '\x1b') escapeState = 4; continue; }
                if (escapeState == 4) { escapeState = c == '\\' ? 0 : c == '\x1b' ? 4 : 3; continue; }
                if (escapeState == 5) { if (c is >= '\x30' and <= '\x7e') escapeState = 0; continue; }
                if (c is '\x1b' or '\x9b' or '\x9d') { control.Clear(); escapeState = c == '\x1b' ? 1 : c == '\x9b' ? 2 : 3; continue; }
                if (char.IsControl(c) && c is not ('\r' or '\n' or '\b' or '\t')) continue;
                consume(c);
                if (c == '\n') lineHasText = false;
                else if (!char.IsControl(c)) lineHasText = true;
            }
        }
        finally { ArrayPool<char>.Shared.Return(chars); }
    }

    public void Reset() { decoder.Reset(); escapeState = 0; control.Clear(); lineHasText = false; }
}
