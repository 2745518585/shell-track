using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ShellTrack.Client;

internal static class DetachedHost
{
    internal static void Start(string executable, string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("后台自动启动需要 Windows。");
        var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        // Explicitly disable handle inheritance: even ShellExecute can retain
        // PowerShell's output pipe and prevent a captured native command returning.
        if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000,
            IntPtr.Zero, null, ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动 Shell Track 后台。");
        CloseHandle(process.Thread);
        CloseHandle(process.Process);
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, CharacterWidth, CharacterHeight, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
