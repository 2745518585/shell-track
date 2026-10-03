using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ShellTrack.Core;

namespace ShellTrack.Windows;

public sealed class ConPtyFactory : ITerminalFactory
{
    public ITerminalSession Start(TerminalStart start) => new ConPtySession(start);
}

public sealed class ConPtySession : ITerminalSession
{
    private IntPtr console, process, job;
    private readonly object gate = new();
    private readonly object writeGate = new();
    private FileStream input = null!;
    private FileStream output = null!;
    private Task<int>? completion;
    private bool disposed;
    public int ProcessId { get; private set; }
    public Stream Output => output;

    public ConPtySession(TerminalStart start)
    {
        IntPtr attributes = IntPtr.Zero;
        bool initialized = false;
        Native.ProcessInfo child = default;
        Check(Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0));
        using (inputRead)
        using (inputWrite)
        {
            Check(Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0));
            using (outputRead)
            using (outputWrite)
            {
                try
                {
                    Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new(start.Columns, start.Rows), inputRead, outputWrite, 0, out console));
                    nuint size = 0;
                    Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attributes = Marshal.AllocHGlobal(checked((int)size));
                    Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                    initialized = true;
                    Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x00020016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                    var startup = new Native.StartupInfoEx { Attributes = attributes };
                    startup.Info.cb = Marshal.SizeOf<Native.StartupInfoEx>();
                    // Prevent redirected parent stdio from bypassing the pseudo console.
                    // NULL handles are replaced by console handles during child initialization.
                    startup.Info.flags = 0x100; // STARTF_USESTDHANDLES
                    var command = new StringBuilder(start.RawArguments is null
                        ? string.Join(" ", new[] { start.Executable }.Concat(start.Arguments).Select(QuoteArgument))
                        : QuoteArgument(start.Executable) + " " + start.RawArguments);
                    Check(Native.CreateProcessW(start.Executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x4, IntPtr.Zero, start.WorkingDirectory, ref startup, out child));
                    process = child.Process;
                    ProcessId = child.ProcessId;
                    job = Native.CreateJobObjectW(IntPtr.Zero, null);
                    Check(job != IntPtr.Zero);
                    var limits = new Native.ExtendedLimits();
                    limits.Basic.Flags = 0x2000; // KILL_ON_JOB_CLOSE
                    Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>()));
                    Check(Native.AssignProcessToJobObject(job, process));
                    input = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(inputWrite.DangerousGetHandle(), true), FileAccess.Write, 4096, false);
                    inputWrite.SetHandleAsInvalid();
                    output = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(outputRead.DangerousGetHandle(), true), FileAccess.Read, 4096, false);
                    outputRead.SetHandleAsInvalid();
                    Check(Native.ResumeThread(child.Thread) != uint.MaxValue);
                }
                catch
                {
                    if (process != IntPtr.Zero) Native.TerminateProcess(process, 1);
                    input?.Dispose();
                    // Close the pipe reader before closing a failed startup's pseudo console.
                    output?.Dispose();
                    outputRead.Dispose();
                    Dispose();
                    throw;
                }
                finally
                {
                    if (child.Thread != IntPtr.Zero) Native.CloseHandle(child.Thread);
                    if (initialized) Native.DeleteProcThreadAttributeList(attributes);
                    if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                }
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        // A blocked input pipe must not hold the lifecycle lock used by termination.
        lock (writeGate) { ObjectDisposedException.ThrowIf(disposed, this); input.Write(data); input.Flush(); }
    }

    public void Resize(int columns, int rows)
    {
        lock (gate) { if (!disposed && console != IntPtr.Zero) Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(console, new(columns, rows))); }
    }

    public void Terminate()
    {
        lock (gate) { if (!disposed && job != IntPtr.Zero) Check(Native.TerminateJobObject(job, 130)); }
    }

    public Task<int> WaitForExitAsync()
    {
        lock (gate) return completion ??= Task.Run(() =>
        {
            if (Native.WaitForSingleObject(process, uint.MaxValue) != 0) throw new Win32Exception();
            Check(Native.GetExitCodeProcess(process, out var code));
            IntPtr closing;
            lock (gate)
            {
                closing = console;
                console = IntPtr.Zero;
            }
            if (closing != IntPtr.Zero) Native.ClosePseudoConsole(closing);
            return unchecked((int)code);
        });
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
            input?.Dispose();
            output?.Dispose();
            if (console != IntPtr.Zero) { Native.ClosePseudoConsole(console); console = IntPtr.Zero; }
            if (process != IntPtr.Zero) { Native.CloseHandle(process); process = IntPtr.Zero; }
        }
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    public static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
