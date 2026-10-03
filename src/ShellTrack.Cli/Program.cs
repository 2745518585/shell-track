using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using ShellTrack.Client;
using ShellTrack.Contracts;
using ShellTrack.Windows;

try { return await Run(args); }
catch (Exception ex) { Console.Error.WriteLine($"shelltrack: {ex.Message}"); return 125; }

static async Task<int> Run(string[] arguments)
{
#if WRAP_PWSH || WRAP_POWERSHELL || WRAP_CMD
#if WRAP_PWSH
    const string wrappedShell = "pwsh";
#elif WRAP_POWERSHELL
    const string wrappedShell = "powershell";
#else
    const string wrappedShell = "cmd";
#endif
    using var client = await ShellTrackClient.ConnectAsync(allowTerminal: true);
    if (!(await client.HealthAsync()).SupportsRawArguments)
        throw new InvalidOperationException("当前后台版本不支持 shell 参数透传，请结束旧后台后重试。");
    client.OutputGap += (from, to) => Console.Error.WriteLine($"\n[shelltrack: 输出 {from}..{to} 已超出记录与实时缓冲范围]");
    var size = Size();
    var session = await client.CreateAsync(new CreateSessionRequest
    {
        Shell = wrappedShell, RawArguments = ShellCommandLine.Arguments(),
        WorkingDirectory = Environment.CurrentDirectory, Columns = size.Columns, Rows = size.Rows,
        DisconnectPolicy = DisconnectPolicy.Terminate
    });
    return await RunSession(client, session, false, size);
#else
    string? dataRoot = null, command = null, outputFile = null;
    string shell = "pwsh", cwd = Environment.CurrentDirectory, action = "run";
    string? id = null;
    bool notify = false, detach = false, keep = false, json = false;
    for (int i = 0; i < arguments.Length; i++)
    {
        string Value() => ++i < arguments.Length ? arguments[i] : throw new ArgumentException("选项缺少参数。");
        switch (arguments[i])
        {
            case "-h": case "--help":
                Console.WriteLine("""
                    Shell Track — Windows 终端代理
                    shelltrack [-s pwsh|cmd|powershell] [-c <script>] [-n] [-b] [-k]
                    shelltrack list|ls [-j]
                    shelltrack show|sh、stop|st、delete|rm <taskId>
                    shelltrack export|ex <taskId> -o <file>
                    shelltrack ui|u
                    shelltrack shutdown|sd    结束后台及其所有运行任务
                    --shell -s <shell>   --command -c <script>   --cwd -w <path>
                    --data-dir -d <path>   --output -o <file>   --json -j
                    --notify -n   --detach -b   --keep -k   --help -h
                    --detach 仅用于单次命令；--keep 保留断开后的交互会话。
                    """);
                return 0;
            case "--data-dir": case "-d": dataRoot = Value(); break;
            case "--shell": case "-s": shell = Value(); break;
            case "--command": case "-c": command = Value(); break;
            case "--cwd": case "-w": cwd = Path.GetFullPath(Value()); break;
            case "--output": case "-o": outputFile = Value(); break;
            case "--notify": case "-n": notify = true; break;
            case "--detach": case "-b": detach = true; break;
            case "--keep": case "-k": keep = true; break;
            case "--json": case "-j": json = true; break;
            case "ls": case "sh": case "st": case "rm": case "ex": case "u": case "sd":
            case "list": case "show": case "stop": case "delete": case "export": case "ui": case "shutdown":
                if (action != "run") throw new ArgumentException("只能指定一个子命令。");
                action = arguments[i] switch
                {
                    "ls" => "list", "sh" => "show", "st" => "stop", "rm" => "delete",
                    "ex" => "export", "u" => "ui", "sd" => "shutdown", _ => arguments[i]
                };
                if (action is "show" or "stop" or "delete" or "export") id = Value();
                break;
            default: throw new ArgumentException($"未知参数：{arguments[i]}");
        }
    }
    if (detach && command is null) throw new ArgumentException("--detach 必须指定 --command。");
    if (action == "export" && outputFile is null) throw new ArgumentException("export 需要 --output。");
    using var client = await ShellTrackClient.ConnectAsync(dataRoot, autoStart: action != "shutdown", allowTerminal: action == "run");
    client.OutputGap += (from, to) => Console.Error.WriteLine($"\n[shelltrack: 输出 {from}..{to} 已超出记录与实时缓冲范围]");
    switch (action)
    {
        case "list":
            var sessions = await client.ListAllAsync();
            if (json) Console.WriteLine(JsonSerializer.Serialize(sessions, Protocol.Json));
            else foreach (var s in sessions) Console.WriteLine($"{s.Id}  {s.State,-11} {s.ExitCode,4}  {s.Request.Shell}  {s.Request.Command ?? (string.IsNullOrEmpty(s.Request.RawArguments) ? "交互会话" : s.Request.RawArguments)}");
            return 0;
        case "stop": Console.WriteLine((await client.TerminateAsync(id!)).State); return 0;
        case "delete": await client.DeleteAsync(id!); return 0;
        case "shutdown": await client.ShutdownAsync(); return 0;
        case "show":
            using (var mode = new ConsoleModeScope())
                await CopyOutput(client, id!, Console.OpenStandardOutput());
            return 0;
        case "export":
            await using (var file = new FileStream(outputFile!, FileMode.CreateNew, FileAccess.Write))
                await CopyOutput(client, id!, file);
            return 0;
        case "ui":
            string desktop = ShellTrackClient.FindApplication("ShellTrack.Desktop") ?? throw new FileNotFoundException("未找到桌面程序，请先构建 ShellTrack.Desktop.slnx。");
            var start = new ProcessStartInfo(PackageIdentity.ViewerExecutable ?? Path.ChangeExtension(desktop, ".exe")) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(client.DataRoot);
            using (Process.Start(start)) { }
            return 0;
    }
    var size = Size();
    var session = await client.CreateAsync(new CreateSessionRequest
    {
        Shell = shell, Command = command, WorkingDirectory = cwd, Notify = notify,
        Columns = size.Columns, Rows = size.Rows,
        DisconnectPolicy = command is null && !keep ? DisconnectPolicy.Terminate : DisconnectPolicy.Continue
    });
    if (session.State == SessionState.Failed) throw new InvalidOperationException(session.Error);
    if (detach) { Console.WriteLine(session.Id); return 0; }
    return await RunSession(client, session, command is null && !keep, size);
#endif
}
static async Task<int> RunSession(ShellTrackClient client, SessionInfo session, bool terminateOnInputEnd, (int Columns, int Rows) size)
{
    if (session.State == SessionState.Failed) throw new InvalidOperationException(session.Error);
    using var cancellation = new CancellationTokenSource();
    ClientWebSocket? socket = null;
    using var consoleMode = new ConsoleModeScope();
    var sendGate = new SemaphoreSlim(1);
    try
    {
        // Fast single-command sessions can finish before the input handshake.
        if (!(await client.GetAsync(session.Id)).IsFinished)
        {
            try { socket = await client.OpenTerminalAsync(session.Id); }
            catch (WebSocketException) { if (!(await client.GetAsync(session.Id)).IsFinished) throw; }
        }
        if (socket is not null)
        {
            var inputSocket = socket;
            _ = Task.Run(async () =>
            {
                var input = Console.OpenStandardInput(); var buffer = new byte[4096];
                try
                {
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellation.Token)) > 0)
                    {
                        await sendGate.WaitAsync(cancellation.Token);
                        try { await inputSocket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, cancellation.Token); }
                        finally { sendGate.Release(); }
                    }
                    if (terminateOnInputEnd) await client.TerminateAsync(session.Id);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
            });
            _ = Task.Run(async () =>
            {
                var previous = size;
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        await Task.Delay(150, cancellation.Token);
                        var current = Size();
                        if (current == previous) continue;
                        await sendGate.WaitAsync(cancellation.Token);
                        try { await inputSocket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new TerminalMessage("resize", Columns: current.Columns, Rows: current.Rows), Protocol.Json), WebSocketMessageType.Text, true, cancellation.Token); }
                        finally { sendGate.Release(); }
                        previous = current;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
            });
        }
        await CopyOutput(client, session.Id, Console.OpenStandardOutput());
        var final = await client.GetAsync(session.Id);
        if (final.OutputTruncated) Console.Error.WriteLine("\n[shelltrack: 输出记录不完整或超过配额]");
        if (final.Error is not null) Console.Error.WriteLine(final.Error);
        return final.ExitCode ?? 125;
    }
    finally { cancellation.Cancel(); socket?.Abort(); socket?.Dispose(); }
}
static async Task CopyOutput(ShellTrackClient client, string id, Stream destination)
{
    await foreach (var bytes in client.FollowOutputAsync(id)) { await destination.WriteAsync(bytes); await destination.FlushAsync(); }
}
static (int Columns, int Rows) Size()
{
    try { return (Math.Clamp(Console.WindowWidth, 1, 1000), Math.Clamp(Console.WindowHeight, 1, 1000)); }
    catch (IOException) { return (120, 30); }
}
