using System.Net.WebSockets;
using System.Text;
using ShellTrack.Client;
using ShellTrack.Contracts;
using ShellTrack.Core;

internal static class ConsoleEncodingChecks
{
    public static async Task RunAsync(ShellTrackClient client, string root, string configuration, Action<bool, string> check, CancellationToken cancellation)
    {
        string cli = Path.GetFullPath(Path.Combine("src", "ShellTrack.Cli", "bin", configuration, "net10.0-windows", "shelltrack.exe"));
        string wrapper = Path.GetFullPath(Path.Combine("src", "ShellTrack.Cmd", "bin", configuration, "net10.0-windows", "shelltrack-cmd.exe"));
        CreateSessionRequest Request(string command) => new()
        {
            Shell = "cmd", Command = command, WorkingDirectory = Path.GetFullPath("."),
            DisconnectPolicy = DisconnectPolicy.Continue
        };
        async Task<string> Read(string id)
        {
            var log = new TextLog();
            await foreach (byte[] bytes in client.FollowOutputAsync(id, cancellation: cancellation)) log.Append(bytes);
            return log.ToString();
        }
        // The outer ConPTY is an actual console for the CLI. Testing only a
        // redirected CLI misses native ReadFile/WriteFile code-page conversion.
        var output = await client.CreateAsync(Request($"chcp 936 > nul & \"{cli}\" -d \"{root}\" -s cmd -c \"echo CMD_ENCODING_中文\" & chcp"), cancellation);
        string outputText = await Read(output.Id);
        check(outputText.Contains("CMD_ENCODING_中文"), "936 外层控制台中 CLI 中文输出正确显示");
        check(outputText.Contains("936"), "CLI 结束后恢复外层控制台代码页");
        var wrapped = await client.CreateAsync(Request($"chcp 936 > nul & set \"SHELLTRACK_DATA_DIR={root}\" & \"{wrapper}\" /D /C \"echo WRAPPER_ENCODING_中文\" & chcp"), cancellation);
        string wrappedText = await Read(wrapped.Id);
        check(wrappedText.Contains("WRAPPER_ENCODING_中文") && wrappedText.Contains("936"), "cmd 包装入口也正确转发中文并恢复代码页");

        var interactive = await client.CreateAsync(Request($"chcp 936 > nul & \"{cli}\" -d \"{root}\" -s cmd & chcp"), cancellation);
        try
        {
            using var socket = await client.OpenTerminalAsync(interactive.Id, cancellation);
            var ready = new TextLog();
            long offset = 0;
            while (!ready.ToString().Contains("Microsoft Windows"))
            {
                var page = await client.OutputAsync(interactive.Id, offset, cancellation);
                ready.Append(Convert.FromBase64String(page.Data)); offset = long.Parse(page.NextOffset);
                if (page.Complete) throw new Exception("Interactive encoding fixture exited before the shell was ready.");
                await Task.Delay(30, cancellation);
            }
            await socket.SendAsync(Encoding.UTF8.GetBytes("echo INPUT_ENCODING_中文\r\nexit /b 0\r\n"), WebSocketMessageType.Binary, true, cancellation);
            string interactiveText = await Read(interactive.Id);
            check(interactiveText.Split('\n').Any(line => line.Trim() == "INPUT_ENCODING_中文"), "936 控制台中交互中文输入和命令输出完整往返");
            check(interactiveText.Contains("936"), "交互会话结束后恢复原代码页");
        }
        finally
        {
            if (!(await client.GetAsync(interactive.Id)).IsFinished) await client.TerminateAsync(interactive.Id);
        }
    }
}
