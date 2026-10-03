using System.Runtime.InteropServices;

namespace ShellTrack.Desktop;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 71;
    private readonly IntPtr window;
    private readonly SubclassProc callback;
    private readonly Action show, exit;
    private readonly IntPtr icon;
    private readonly bool ownsIcon;
    private bool disposed;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    public TrayIcon(IntPtr handle, Action showWindow, Action exitApplication)
    {
        window = handle; show = showWindow; exit = exitApplication; callback = HandleMessage;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "shelltrack.ico");
        icon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x10 | 0x40);
        ownsIcon = icon != IntPtr.Zero;
        if (!ownsIcon) icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        if (!SetWindowSubclass(window, callback, 1, 0)) throw new System.ComponentModel.Win32Exception();
        Add();
    }
    private void Add()
    {
        var data = Data();
        data.Flags = 1 | 2 | 4; data.Callback = CallbackMessage;
        data.Icon = icon; data.Tip = "Shell Track · 双击打开，右键菜单";
        if (!Shell_NotifyIcon(0, ref data)) throw new System.ComponentModel.Win32Exception();
    }
    private IntPtr HandleMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, nuint id, nuint reference)
    {
        if (message == taskbarCreated) { Add(); return IntPtr.Zero; }
        if (message == CallbackMessage)
        {
            if (lParam.ToInt64() == 0x203) show();
            if (lParam.ToInt64() == 0x205)
            {
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "打开 Shell Track"); AppendMenu(menu, 0, 2, "退出查看器");
                    GetCursorPos(out var point); SetForegroundWindow(window);
                    int selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, window, IntPtr.Zero);
                    if (selected == 1) show(); else if (selected == 2) exit();
                }
                finally { DestroyMenu(menu); }
            }
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    private NotifyIconData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1, Tip = "", Info = "", InfoTitle = "" };
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        var data = Data(); Shell_NotifyIcon(2, ref data); RemoveWindowSubclass(window, callback, 1);
        if (ownsIcon) DestroyIcon(icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, nuint id, nuint reference);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
