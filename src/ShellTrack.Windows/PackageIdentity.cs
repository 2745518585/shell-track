using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ShellTrack.Windows;

public static class PackageIdentity
{
    public static string? FamilyName
    {
        get
        {
            uint length = 0;
            int result = GetCurrentPackageFamilyName(ref length, null);
            if (result == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
            if (result != 122) throw new Win32Exception(result);
            var name = new StringBuilder((int)length);
            result = GetCurrentPackageFamilyName(ref length, name);
            if (result != 0) throw new Win32Exception(result);
            return name.ToString();
        }
    }

    // Launch through this package's alias so Windows assigns the Desktop
    // application identity rather than inheriting the CLI/Host application ID.
    public static string? ViewerExecutable => FamilyName is string family
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", family, "shelltrack-viewer.exe") : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? name);
}
