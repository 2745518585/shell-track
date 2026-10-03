using System.Runtime.InteropServices;

namespace ShellTrack.Windows;

/// <summary>Starts a desktop helper through Explorer's user environment.</summary>
public static class DesktopProcessLauncher
{
    public static bool TryLaunch(string executable, IReadOnlyList<string> arguments)
    {
        if (GetShellWindow() == IntPtr.Zero) return false;
        object? windows = null, browser = null, document = null, application = null;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!)!;
            int desktopWindow = 0;
            browser = ((dynamic)windows).FindWindowSW(0, 0, 8, out desktopWindow, 1);
            if (browser is null) return false;
            document = ((dynamic)browser).Document;
            application = ((dynamic)document).Application;
            ((dynamic)application).ShellExecute(executable, string.Join(" ", arguments.Select(ConPtySession.QuoteArgument)),
                Path.GetDirectoryName(executable)!, "open", 0);
            return true;
        }
        finally
        {
            foreach (object? value in new[] { application, document, browser, windows })
                if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
}
