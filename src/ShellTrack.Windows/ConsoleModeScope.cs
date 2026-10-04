using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ShellTrack.Windows;

public sealed class ConsoleModeScope : IDisposable
{
    private readonly IntPtr input = Native.GetStdHandle(-10), output = Native.GetStdHandle(-11);
    private readonly uint inputMode, outputMode;
    private readonly bool hasInput, hasOutput;
    private readonly uint inputCodePage, outputCodePage;
    private Encoding? inputEncoding, outputEncoding;
    private bool disposed;
    public ConsoleModeScope()
    {
        hasInput = Native.GetConsoleMode(input, out inputMode);
        hasOutput = Native.GetConsoleMode(output, out outputMode);
        inputCodePage = hasInput ? Native.GetConsoleCP() : 0;
        outputCodePage = hasOutput ? Native.GetConsoleOutputCP() : 0;
        try
        {
            // ConPTY transport is always UTF-8. ReadFile/WriteFile on a real
            // console would otherwise interpret those bytes using e.g. CP936.
            // Use the managed setters as well, to refresh Console's cached encodings.
            if (hasInput) { inputEncoding = Console.InputEncoding; Console.InputEncoding = new UTF8Encoding(false); }
            if (hasOutput) { outputEncoding = Console.OutputEncoding; Console.OutputEncoding = new UTF8Encoding(false); }
            // VT input, no line buffering/echo/processed Ctrl+C, no Quick Edit pause.
            if (hasInput && !Native.SetConsoleMode(input, (inputMode | 0x200 | 0x80) & ~(0x1u | 0x2u | 0x4u | 0x40u))) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (hasOutput && !Native.SetConsoleMode(output, outputMode | 0x4 | 0x8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (inputEncoding is not null) Console.InputEncoding = inputEncoding; }
        finally
        {
            try { if (outputEncoding is not null) Console.OutputEncoding = outputEncoding; }
            finally
            {
                // A shell's chcp can leave managed caches different from native
                // pages. Restore the original native values exactly, independently.
                if (inputCodePage != 0) Native.SetConsoleCP(inputCodePage);
                if (outputCodePage != 0) Native.SetConsoleOutputCP(outputCodePage);
                if (hasInput) Native.SetConsoleMode(input, inputMode);
                if (hasOutput) Native.SetConsoleMode(output, outputMode);
            }
        }
    }
}
