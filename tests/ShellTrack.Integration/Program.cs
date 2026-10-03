using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ShellTrack.Client;
using ShellTrack.Contracts;
using ShellTrack.Core;

string root = Path.GetFullPath(Path.Combine("work", "tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
Process? host = null;
Task? hostOutput = null, hostError = null;
int hostGeneration = 0;
int checks = 0;
string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
try
{
    CoreLogChecks.Run(Check);
    host = StartHost();
    using var client = await Connect();
    var cmd = await client.CreateAsync(Request("cmd", "echo CMD_OK & exit /b 3"));
    var cmdText = await Output(client, cmd.Id);
    Check(cmdText.Contains("CMD_OK"), "CMD 输出进入 ConPTY");
    Check((await client.GetAsync(cmd.Id)).ExitCode == 3, "CMD 非零退出码");
    var quotedCmd = await client.CreateAsync(Request("cmd", "echo \"quoted words\" & exit /b 4"));
    Check((await Output(client, quotedCmd.Id)).Contains("\"quoted words\"") && (await client.GetAsync(quotedCmd.Id)).ExitCode == 4, "CMD 内层引号不被反斜杠改写");
    var powershellRequest = Request("pwsh", "Write-Output '中文 引号 \" 保留'; exit 7");
    var ps = await client.CreateAsync(powershellRequest);
    Check((await Output(client, ps.Id)).Contains("中文 引号 \" 保留"), "PowerShell 中文与引号");
    Check((await client.GetAsync(ps.Id)).ExitCode == 7, "PowerShell 非零退出码");
    string? psPreview = (await client.GetAsync(ps.Id)).LatestOutputLine;
    Check(psPreview?.Contains("中文 引号 \" 保留") == true, "任务预览提取最后一行可读输出");
    await ExpectStatus(() => client.OutputAsync(ps.Id, -1), HttpStatusCode.BadRequest, "拒绝负输出游标");
    await ExpectStatus(() => client.CreateAsync(powershellRequest with { RequestId = Guid.NewGuid().ToString("N"), WorkingDirectory = Path.Combine(root, "missing") }), HttpStatusCode.BadRequest, "工作目录校验");
    var textView = new TextLog();
    byte[] splitText = Encoding.UTF8.GetBytes("中文\x1b[31m red\x1b[0m\r\n");
    foreach (byte b in splitText) textView.Append(new byte[] { b });
    Check(textView.ToString() == "中文 red\n", "跨字节 UTF-8 与控制序列的只读文本解码");
    Check((await client.CreateAsync(powershellRequest)).Id == ps.Id, "创建请求幂等");
    await ExpectStatus(() => client.CreateAsync(powershellRequest with { Command = "exit 0" }), HttpStatusCode.Conflict, "requestId 参数冲突");

    var parallel = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => client.CreateAsync(Request("pwsh", $"Write-Output 'PARALLEL_{i}'; Start-Sleep -Milliseconds 200"))));
    for (int i = 0; i < parallel.Length; i++) Check((await Output(client, parallel[i].Id)).Contains($"PARALLEL_{i}"), "并发任务输出隔离 " + i);
    var large = await client.CreateAsync(Request("pwsh", "1..2500 | ForEach-Object { 'BLOCK_' + $_ + ('x' * 120) }; Write-Output 'OUTPUT_END'"));
    Check((await Output(client, large.Id)).Contains("OUTPUT_END"), "大量输出排空无死锁");
    Check((await client.GetAsync(large.Id)).OutputTruncated && (await client.GetAsync(large.Id)).RecordedLength == 65536, "录制配额达到后仍转发实时输出");
    Check((await client.GetAsync(large.Id)).LatestOutputLine?.Contains("OUTPUT_END") == true, "录制达到上限后任务预览继续更新");
    var flood = await client.CreateAsync(Request("pwsh", "$line = 'x' * 1024; 1..6000 | ForEach-Object { $line }; Write-Output 'FLOOD_END'"));
    while (!(await client.GetAsync(flood.Id)).IsFinished) await Task.Delay(100, deadline.Token);
    var floodInfo = await client.GetAsync(flood.Id);
    var gap = await client.OutputAsync(flood.Id, floodInfo.RecordedLength, deadline.Token);
    Check(gap.Gap && long.Parse(gap.NextOffset) > floodInfo.RecordedLength, "慢查看器收到明确的输出缺口");
    Check((await Output(client, flood.Id)).Contains("FLOOD_END"), "输出缺口后可继续读到末尾");

    var interactive = await client.CreateAsync(Request("cmd", null) with { DisconnectPolicy = DisconnectPolicy.Terminate });
    using (var socket = await client.OpenTerminalAsync(interactive.Id, deadline.Token))
    {
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new TerminalMessage("resize", Columns: 90, Rows: 24), Protocol.Json), WebSocketMessageType.Text, true, deadline.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("echo INTERACTIVE_OK\r\nexit /b 9\r\n"), WebSocketMessageType.Binary, true, deadline.Token);
        Check((await Output(client, interactive.Id)).Contains("INTERACTIVE_OK"), "交互输入转发");
        Check((await client.GetAsync(interactive.Id)).ExitCode == 9, "交互退出码");
        Check((await client.GetAsync(interactive.Id)).Columns == 90, "终端尺寸同步");
    }
    var detached = await client.CreateAsync(Request("cmd", null) with { DisconnectPolicy = DisconnectPolicy.Terminate });
    using (var socket = await client.OpenTerminalAsync(detached.Id, deadline.Token)) socket.Abort();
    await Output(client, detached.Id);
    Check((await client.GetAsync(detached.Id)).IsFinished, "交互断线终止");
    var kept = await client.CreateAsync(Request("cmd", null) with { DisconnectPolicy = DisconnectPolicy.Continue });
    using (var socket = await client.OpenTerminalAsync(kept.Id, deadline.Token))
    {
        bool inputConflict = false;
        try { using var duplicate = await client.OpenTerminalAsync(kept.Id, deadline.Token); }
        catch (WebSocketException) { inputConflict = true; }
        Check(inputConflict, "唯一输入所有者");
        socket.Abort();
    }
    await Task.Delay(200, deadline.Token);
    Check(!(await client.GetAsync(kept.Id)).IsFinished, "保留会话断线后继续运行");
    await client.TerminateAsync(kept.Id); await Output(client, kept.Id);

    var runningRequest = Request("pwsh", "Write-Output 'RUNNING_PREVIEW'; Start-Sleep -Seconds 60");
    var running = await client.CreateAsync(runningRequest);
    while ((await client.GetAsync(running.Id)).LatestOutputLine?.Contains("RUNNING_PREVIEW") != true) await Task.Delay(50, deadline.Token);
    Check(!(await client.GetAsync(running.Id)).IsFinished, "运行中的任务即时更新输出预览");
    Check(!(await client.GetAsync(running.Id)).Request.Notify, "任务默认不启用完成通知");
    Check((await client.SetNotificationAsync(running.Id, true, deadline.Token)).Request.Notify, "管理客户端可打开运行任务的完成通知");
    var retryAfterToggle = await client.CreateAsync(runningRequest);
    Check(retryAfterToggle.Id == running.Id && retryAfterToggle.Request.Notify, "调整通知后重试原创建请求仍幂等且保留当前设置");
    await ExpectStatus(() => client.CreateAsync(runningRequest with { Notify = true }), HttpStatusCode.Conflict, "调整通知后仍拒绝改变原创建参数的重试");
    Check(!(await client.SetNotificationAsync(running.Id, false, deadline.Token)).Request.Notify, "管理客户端可关闭运行任务的完成通知");
    await ExpectStatus(() => client.DeleteAsync(running.Id), HttpStatusCode.Conflict, "禁止删除运行任务");
    await client.TerminateAsync(running.Id);
    await Output(client, running.Id);
    Check((await client.GetAsync(running.Id)).IsFinished, "显式终止任务");
    await ExpectStatus(() => client.SetNotificationAsync(running.Id, true), HttpStatusCode.Conflict, "不能修改已结束任务的通知设置");
    var initialNotificationRequest = Request("pwsh", "Start-Sleep -Seconds 60") with { Notify = true };
    var notificationDisabled = await client.CreateAsync(initialNotificationRequest);
    Check(!(await client.SetNotificationAsync(notificationDisabled.Id, false, deadline.Token)).Request.Notify, "可以关闭创建时已启用的完成通知");
    await client.TerminateAsync(notificationDisabled.Id); await Output(client, notificationDisabled.Id);

    using var anonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{client.Connection.Port}") };
    using var noAuth = await anonymous.GetAsync("/v1/tasks", deadline.Token);
    Check(noAuth.StatusCode == HttpStatusCode.Unauthorized, "匿名不能读日志");
    var credentials = JsonSerializer.Deserialize<ClientCredentials>(await File.ReadAllTextAsync(Path.Combine(root, "credentials.json")), Protocol.Json)!;
    anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Read);
    using var readMutation = await anonymous.PostAsJsonAsync("/v1/tasks", Request("cmd", "echo forbidden"), Protocol.Json, deadline.Token);
    Check(readMutation.StatusCode == HttpStatusCode.Forbidden, "只读不能创建任务");
    using var readNotification = await anonymous.PostAsJsonAsync($"/v1/tasks/{running.Id}/notification", new { enabled = true }, Protocol.Json, deadline.Token);
    Check(readNotification.StatusCode == HttpStatusCode.Forbidden, "只读凭据不能修改通知设置");
    anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Manage);
    using var missingNotificationSetting = await anonymous.PostAsJsonAsync($"/v1/tasks/{running.Id}/notification", new Dictionary<string, bool>(), Protocol.Json, deadline.Token);
    Check(missingNotificationSetting.StatusCode == HttpStatusCode.BadRequest, "通知设置请求必须明确提供 enabled");
    anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Read);
    using var forbiddenSocket = new ClientWebSocket();
    forbiddenSocket.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Manage);
    bool denied = false;
    try { await forbiddenSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{client.Connection.Port}/v1/tasks/{ps.Id}/terminal"), deadline.Token); }
    catch (WebSocketException) { denied = true; }
    Check(denied, "管理凭据不能写终端");
    anonymous.DefaultRequestHeaders.Add("Origin", "https://example.com");
    using var origin = await anonymous.GetAsync("/v1/tasks", deadline.Token);
    Check(origin.StatusCode == HttpStatusCode.Forbidden, "拒绝网页来源");
    using var observer = await ShellTrackClient.ConnectAsync(root, autoStart: false, readOnly: true, cancellation: deadline.Token);
    bool observerDenied = false;
    try { using var socket = await observer.OpenTerminalAsync(ps.Id); }
    catch (InvalidOperationException) { observerDenied = true; }
    Check(observerDenied, "默认客户端不持有终端写入能力");
    using var events = new ClientWebSocket();
    events.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Read);
    await events.ConnectAsync(new Uri($"ws://127.0.0.1:{client.Connection.Port}/v1/events"), deadline.Token);
    var eventBuffer = new byte[65536];
    var received = await events.ReceiveAsync(eventBuffer, deadline.Token);
    Check(received.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(eventBuffer, 0, received.Count).Contains("state"), "自动化状态订阅");
    events.Abort();

    var failed = await client.CreateAsync(Request("missing-shell", "echo invalid"));
    Check(failed.State == SessionState.Failed && failed.Error is not null, "未知 shell 明确失败");
    await client.DeleteAsync(failed.Id);
    await ExpectStatus(() => client.GetAsync(failed.Id), HttpStatusCode.NotFound, "删除已结束历史");

    // Use this build's configuration, not the most recently published RID output.
    string cliPath = Path.GetFullPath(Path.Combine("src", "ShellTrack.Cli", "bin", configuration, "net10.0-windows", "shelltrack.dll"));
    var cliStart = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string arg in new[] { cliPath, "--data-dir", root, "--command", "Write-Output 'CLI_OK'; exit 7" }) cliStart.ArgumentList.Add(arg);
    using var cli = Process.Start(cliStart)!;
    var stdout = cli.StandardOutput.ReadToEndAsync(deadline.Token);
    var stderr = cli.StandardError.ReadToEndAsync(deadline.Token);
    await cli.WaitForExitAsync(deadline.Token);
    Check(cli.ExitCode == 7 && (await stdout).Contains("CLI_OK"), "CLI 真实输出与退出码透传");
    await stderr;

    string autoRoot = Path.Combine(root, "autostart 中文 space");
    string executablePath = Path.ChangeExtension(cliPath, ".exe");
    string captureScript = "$ErrorActionPreference = 'Stop'\n" +
        "$cli = '" + executablePath.Replace("'", "''") + "'\n" +
        "$data = '" + autoRoot.Replace("'", "''") + "'\n" +
        "[IO.File]::WriteAllText($data + '.phase', 'before capture: ' + $cli)\n" +
        "$captured = & $cli --data-dir $data --shell cmd --command 'echo AUTOSTART_CAPTURE_OK & exit /b 7'\n" +
        "[IO.File]::WriteAllText($data + '.phase', 'after capture')\n" +
        "if ($LASTEXITCODE -ne 7 -or ($captured -join '') -notmatch 'AUTOSTART_CAPTURE_OK') { throw 'capture failed' }\n" +
        "& $cli --data-dir $data shutdown\n" +
        "[IO.File]::WriteAllText($data + '.phase', 'after shutdown')\n" +
        "Write-Output 'AUTOSTART_DONE'\n";
    var captureStart = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    captureStart.ArgumentList.Add("-NoProfile"); captureStart.ArgumentList.Add("-EncodedCommand");
    captureStart.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(captureScript)));
    using (var capture = Process.Start(captureStart)!)
    using (var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
    {
        captureTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        var captureOutput = capture.StandardOutput.ReadToEndAsync();
        var captureError = capture.StandardError.ReadToEndAsync();
        try
        {
            await capture.WaitForExitAsync(captureTimeout.Token);
            Check(capture.ExitCode == 0 && (await captureOutput).Contains("AUTOSTART_DONE"), "PowerShell 收集输出时自动启动后台不会阻塞管道");
            await captureError;
        }
        finally
        {
            if (!capture.HasExited) { capture.Kill(); await capture.WaitForExitAsync(); }
            try { using var autoClient = await ShellTrackClient.ConnectAsync(autoRoot, autoStart: false); await autoClient.ShutdownAsync(); }
            catch (IOException) { }
            catch (HttpRequestException) { }
        }
    }

    var interrupted = await client.CreateAsync(Request("pwsh", "Start-Sleep -Seconds 60"));
    int childPid = interrupted.ProcessId!.Value;
    host.Kill(); await host.WaitForExitAsync(deadline.Token);
    await DrainHostLogs();
    host.Dispose(); host = null;
    host = StartHost();
    using var recovered = await Connect();
    Check((await recovered.GetAsync(interrupted.Id)).State == SessionState.Interrupted, "后台异常退出恢复状态");
    Check((await Output(recovered, ps.Id)).Contains("中文"), "重启后保留历史输出");
    Check((await recovered.GetAsync(ps.Id)).LatestOutputLine == psPreview, "重启后保留历史任务的最新输出预览");
    Check((await recovered.GetAsync(large.Id)).LatestOutputLine?.Contains("OUTPUT_END") == true, "重启后保留未录入文件的最后输出预览");
    Check(!(await recovered.GetAsync(running.Id)).Request.Notify, "重启后保留任务通知设置");
    var recoveredNotificationRetry = await recovered.CreateAsync(initialNotificationRequest);
    Check(recoveredNotificationRetry.Id == notificationDisabled.Id && !recoveredNotificationRetry.Request.Notify, "重启后创建请求幂等仍使用原始通知参数");
    bool childEnded;
    try { childEnded = Process.GetProcessById(childPid).HasExited; } catch (ArgumentException) { childEnded = true; }
    Check(childEnded, "后台退出清理所属进程");
    await recovered.ShutdownAsync(deadline.Token);
    await host.WaitForExitAsync(deadline.Token);
    await DrainHostLogs();
    Check(!File.Exists(Path.Combine(root, "connection.json")), "正常关闭清理连接描述");
    Console.WriteLine($"PASS: {checks} checks. Data: {root}");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("诊断目录：" + root); return 1; }
finally
{
    if (host is not null)
    {
        if (!host.HasExited) { host.Kill(); host.WaitForExit(); }
        await DrainHostLogs();
        host.Dispose();
    }
}

void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
CreateSessionRequest Request(string shell, string? command) => new() { Shell = shell, Command = command, WorkingDirectory = Environment.CurrentDirectory };
Process StartHost()
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    string hostPath = Path.GetFullPath(Path.Combine("src", "ShellTrack.Host", "bin", configuration, "net10.0-windows", "ShellTrack.Host.dll"));
    foreach (string value in new[] { hostPath, "--data-dir", root, "--output-limit", "65536" }) start.ArgumentList.Add(value);
    var process = Process.Start(start)!;
    int generation = ++hostGeneration;
    hostOutput = SaveHostLog(process.StandardOutput, Path.Combine(root, $"host-{generation}-stdout.log"));
    hostError = SaveHostLog(process.StandardError, Path.Combine(root, $"host-{generation}-stderr.log"));
    return process;
}
async Task SaveHostLog(StreamReader reader, string path)
{
    await File.WriteAllTextAsync(path, await reader.ReadToEndAsync(), Encoding.UTF8);
}
async Task DrainHostLogs()
{
    if (hostOutput is not null) await hostOutput;
    if (hostError is not null) await hostError;
    hostOutput = hostError = null;
}
async Task<ShellTrackClient> Connect()
{
    for (int i = 0; i < 100; i++)
    {
        try { return await ShellTrackClient.ConnectAsync(root, autoStart: false, cancellation: deadline.Token, allowTerminal: true); }
        catch (IOException) { await Task.Delay(100, deadline.Token); }
    }
    throw new Exception("后台未启动。");
}
async Task<string> Output(ShellTrackClient client, string id)
{
    using var result = new MemoryStream();
    await foreach (var bytes in client.FollowOutputAsync(id, cancellation: deadline.Token)) result.Write(bytes);
    return Encoding.UTF8.GetString(result.ToArray());
}
async Task ExpectStatus(Func<Task> action, HttpStatusCode status, string label)
{
    try { await action(); } catch (HttpRequestException ex) when (ex.StatusCode == status) { Check(true, label); return; }
    throw new Exception("FAIL: " + label);
}
