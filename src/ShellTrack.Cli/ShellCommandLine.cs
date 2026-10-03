using System.Runtime.InteropServices;

internal static class ShellCommandLine
{
    // Read the native command line before .NET parses argv. Rebuilding it from
    // args would change cmd.exe's special quoting and the shell's own parsing.
    internal static string Arguments()
    {
        string command = Marshal.PtrToStringUni(GetCommandLine())!;
        int index = 0;
        while (index < command.Length && char.IsWhiteSpace(command[index])) index++;
        if (index < command.Length && command[index] == '"')
        {
            index++;
            while (index < command.Length && command[index] != '"') index++;
            if (index < command.Length) index++;
        }
        else while (index < command.Length && !char.IsWhiteSpace(command[index])) index++;
        while (index < command.Length && char.IsWhiteSpace(command[index])) index++;
        return command[index..];
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCommandLineW")]
    private static extern IntPtr GetCommandLine();
}
