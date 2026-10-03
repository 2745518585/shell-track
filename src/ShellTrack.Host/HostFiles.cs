using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ShellTrack.Contracts;

namespace ShellTrack.Host;

internal static class HostFiles
{
    public static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    public static void Write<T>(string path, T value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Protocol.Json));
        File.Move(path + ".tmp", path, true);
    }
    public static string Token() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}
