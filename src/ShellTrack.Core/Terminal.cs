namespace ShellTrack.Core;

public sealed record TerminalStart(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, int Columns, int Rows, string? RawArguments = null);

public interface ITerminalSession : IDisposable
{
    int ProcessId { get; }
    Stream Output { get; }
    void Write(ReadOnlySpan<byte> data);
    void Resize(int columns, int rows);
    void Terminate();
    // Close ConPTY on a worker while the consumer continues draining Output.
    Task<int> WaitForExitAsync();
}

public interface ITerminalFactory
{
    ITerminalSession Start(TerminalStart start);
}
