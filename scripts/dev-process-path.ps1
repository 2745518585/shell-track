# Path discovery with query-only access also works when MainModule/Process.Path
# can't read an elevated process. No handle with termination rights is opened here.
if (-not ('ShellTrackDevProcessPath' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ShellTrackDevProcessPath
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    public static string Read(int processId)
    {
        using (var handle = OpenProcess(0x1000, false, processId))
        {
            if (handle.IsInvalid) return null;
            int size = 32768;
            var path = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        }
    }
}
'@
}
