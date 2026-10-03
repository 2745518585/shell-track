namespace ShellTrack.Windows;

public sealed class ConsoleModeScope : IDisposable
{
    private readonly IntPtr input = Native.GetStdHandle(-10), output = Native.GetStdHandle(-11);
    private readonly uint inputMode, outputMode;
    private readonly bool hasInput, hasOutput;
    public ConsoleModeScope()
    {
        hasInput = Native.GetConsoleMode(input, out inputMode);
        hasOutput = Native.GetConsoleMode(output, out outputMode);
        // VT input, no line buffering/echo/processed Ctrl+C, no Quick Edit pause.
        if (hasInput) Native.SetConsoleMode(input, (inputMode | 0x200 | 0x80) & ~(0x1u | 0x2u | 0x4u | 0x40u));
        if (hasOutput) Native.SetConsoleMode(output, outputMode | 0x4 | 0x8);
    }
    public void Dispose()
    {
        if (hasInput) Native.SetConsoleMode(input, inputMode);
        if (hasOutput) Native.SetConsoleMode(output, outputMode);
    }
}
