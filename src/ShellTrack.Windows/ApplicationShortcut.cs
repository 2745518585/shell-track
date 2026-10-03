using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ShellTrack.Windows;

public sealed record ApplicationShortcutInfo(string Target, string Arguments, string Icon, string? AppId, Guid? Activator);

/// <summary>Registers the Shell identity used by unpackaged notifications.</summary>
public static class ApplicationShortcut
{
    private static readonly Guid PropertyFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");


    public static bool Ensure(string path, string target, string icon, string appId, Guid activator)
    {
        target = Path.GetFullPath(target);
        icon = Path.GetFullPath(icon);
        path = Path.GetFullPath(path);
        if (!File.Exists(target)) throw new FileNotFoundException("Shortcut target is missing.", target);
        if (!File.Exists(icon)) throw new FileNotFoundException("Shortcut icon is missing.", icon);
        if (File.Exists(path))
        {
            var current = Inspect(path);
            if (string.Equals(current.Target, target, StringComparison.OrdinalIgnoreCase) && current.Arguments == "" &&
                string.Equals(current.Icon, icon, StringComparison.OrdinalIgnoreCase) && current.AppId == appId && current.Activator == activator)
                return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".lnk";
        object link = CreateLink();
        try
        {
            var shell = (IShellLink)link;
            shell.SetPath(target);
            shell.SetArguments("");
            shell.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            shell.SetDescription("Shell Track task viewer");
            shell.SetIconLocation(icon, 0);
            var store = (IPropertyStore)link;
            SetString(store, 5, appId);
            var key = new PropertyKey(PropertyFormat, 26);
            var value = new PropertyValue { Type = 72, Pointer = Marshal.AllocCoTaskMem(16) }; // VT_CLSID
            try { Marshal.StructureToPtr(activator, value.Pointer, false); store.SetValue(ref key, ref value); }
            finally { Marshal.FreeCoTaskMem(value.Pointer); }
            store.Commit();
            ((IPersistFile)link).Save(temporary, true);
            File.Move(temporary, path, overwrite: true);
            // Flush Shell's shortcut change notification before a caller submits a toast.
            SHChangeNotify(0x00002000, 0x1005, path, IntPtr.Zero); // UPDATEITEM, PATHW | FLUSH
            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static ApplicationShortcutInfo Inspect(string path)
    {
        object link = CreateLink();
        try
        {
            ((IPersistFile)link).Load(Path.GetFullPath(path), 0);
            var shell = (IShellLink)link;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var icon = new StringBuilder(32768);
            shell.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            shell.GetArguments(arguments, arguments.Capacity);
            shell.GetIconLocation(icon, icon.Capacity, out _);
            var store = (IPropertyStore)link;
            var key = new PropertyKey(PropertyFormat, 5);
            store.GetValue(ref key, out var value);
            string? appId;
            try { appId = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
            key.Id = 26;
            store.GetValue(ref key, out value);
            Guid? activator;
            try { activator = value.Type == 72 ? Marshal.PtrToStructure<Guid>(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
            return new(target.ToString(), arguments.ToString(), icon.ToString(), appId, activator);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static object CreateLink() => Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
    private static void SetString(IPropertyStore store, uint id, string text)
    {
        var key = new PropertyKey(PropertyFormat, id);
        var value = new PropertyValue { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(text) }; // VT_LPWSTR
        try { store.SetValue(ref key, ref value); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropertyValue value);
        void SetValue(ref PropertyKey key, ref PropertyValue value);
        void Commit();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short value); void SetHotkey(short value);
        void GetShowCmd(out int value); void SetShowCmd(int value);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string text, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint events, uint flags, string first, IntPtr second);
}
