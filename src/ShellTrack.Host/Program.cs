using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ShellTrack.Contracts;
using ShellTrack.Host;

string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellTrack");
bool quiet = false;
long outputLimit = SessionManager.OutputLimit;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--data-dir" or "-d" && i + 1 < args.Length) dataRoot = Path.GetFullPath(args[++i]);
    else if (args[i] is "--quiet" or "-q") quiet = true;
    else if (args[i] is "--output-limit" or "-l" && i + 1 < args.Length && long.TryParse(args[++i], out var requested) && requested is >= 1024 and <= 536870912) outputLimit = requested;
    else if (args[i] is "--help" or "-h")
    {
        Console.WriteLine("ShellTrack.Host [--data-dir|-d <path>] [--quiet|-q] [--output-limit|-l <bytes>]"); return 0;
    }
    else { Console.Error.WriteLine("使用 --help 或 -h 查看选项。"); return 2; }
}
HostFiles.SecureDirectory(dataRoot);
FileStream instanceLock;
try { instanceLock = new FileStream(Path.Combine(dataRoot, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException) { Console.Error.WriteLine("该数据目录已有后台运行。"); return 3; }
using (instanceLock)
{
    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    if (quiet) builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Loopback, 0); options.Limits.MaxRequestBodySize = 128 * 1024;
    });
    builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSingleton(sp => new SessionManager(dataRoot, sp.GetRequiredService<ILogger<SessionManager>>(), outputLimit));
    var app = builder.Build();
    var manager = app.Services.GetRequiredService<SessionManager>();
    var notifier = new NotificationDispatcher(dataRoot, app.Logger);
    manager.Completed += session =>
    {
        if (session.Request.Notify) _ = Task.Run(async () =>
        {
            var result = await notifier.SendAsync(session);
            try { manager.RecordNotification(session.Id, result); }
            catch (Exception ex) { app.Logger.LogWarning(ex, "通知状态保存失败"); }
        });
    };
    var credentials = new ClientCredentials(HostFiles.Token(), HostFiles.Token(), HostFiles.Token());
    var hostId = Guid.NewGuid().ToString("N");
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
    app.Use(async (context, next) =>
    {
        string requestId = context.TraceIdentifier;
        if (context.Request.Headers.ContainsKey("Origin") || context.Request.Host.Host != "127.0.0.1")
        { await Error(context, 403, "forbidden", "只允许本地原生客户端。", requestId); return; }
        string token = context.Request.Headers.Authorization.ToString();
        token = token.StartsWith("Bearer ", StringComparison.Ordinal) ? token[7..] : "";
        bool read = Matches(token, credentials.Read), manage = Matches(token, credentials.Manage), terminal = Matches(token, credentials.Terminal);
        if (!read && !manage && !terminal) { await Error(context, 401, "unauthorized", "需要有效凭据。", requestId); return; }
        bool terminalRoute = context.Request.Path.Value?.EndsWith("/terminal", StringComparison.Ordinal) == true;
        bool mutation = context.Request.Method is not ("GET" or "HEAD");
        if (terminalRoute ? !terminal : mutation && !manage)
        { await Error(context, 403, "forbidden", "凭据没有该操作权限。", requestId); return; }
        try { await next(context); }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            int status = ex switch { KeyNotFoundException => 404, ArgumentException or JsonException or FormatException => 400, InvalidOperationException => 409, _ => 500 };
            if (status == 500) app.Logger.LogError(ex, "请求失败 {RequestId}", requestId);
            await Error(context, status, status == 500 ? "internal_error" : "invalid_request", status == 500 ? "后台请求失败。" : ex.Message, requestId);
        }
    });
    app.MapGet("/v1/health", () => new HealthInfo(hostId, 1, "0.1.0"));
    app.MapGet("/v1/tasks", (int? skip, int? take) =>
    {
        if ((skip ?? 0) < 0 || (take ?? 100) is < 1 or > 1000) throw new ArgumentException("分页参数无效。");
        return manager.List(skip ?? 0, take ?? 100);
    });
    app.MapPost("/v1/tasks", async (CreateSessionRequest request) => Results.Json(await manager.CreateAsync(request), Protocol.Json, statusCode: 201));
    app.MapGet("/v1/tasks/{id}", (string id) => manager.Get(id));
    app.MapGet("/v1/tasks/{id}/output", (string id, long? offset, int? count) => manager.ReadOutput(id, offset ?? 0, count ?? 65536));
    app.MapPost("/v1/tasks/{id}/terminate", (string id) => manager.Terminate(id));
    app.MapPost("/v1/tasks/{id}/notification", (string id, NotificationPreference preference) => manager.SetNotification(id, preference.Enabled));
    app.MapDelete("/v1/tasks/{id}", (string id) => { manager.Delete(id); return Results.NoContent(); });
    app.MapPost("/v1/shutdown", (IHostApplicationLifetime lifetime) =>
    {
        _ = Task.Run(async () => { await Task.Delay(200); lifetime.StopApplication(); });
        return Results.Accepted();
    });
    app.MapGet("/v1/events", async (HttpContext context) =>
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var known = new Dictionary<string, SessionInfo>();
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var current = manager.List(0, int.MaxValue).ToDictionary(s => s.Id);
                foreach (var session in current.Values)
                    if (!known.TryGetValue(session.Id, out var previous) || previous != session)
                        await Send(socket, new SessionEvent("state", session.Id, session), context.RequestAborted);
                foreach (string deleted in known.Keys.Except(current.Keys)) await Send(socket, new SessionEvent("deleted", deleted), context.RequestAborted);
                known = current;
                await Task.Delay(300, context.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    });
    app.MapGet("/v1/tasks/{id}/terminal", async (HttpContext context, string id) =>
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        manager.AttachInput(id);
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[65536];
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 65536) { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "输入过大", context.RequestAborted); return; }
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Binary) manager.WriteInput(id, message.ToArray());
                else
                {
                    var resize = JsonSerializer.Deserialize<TerminalMessage>(message.ToArray(), Protocol.Json)!;
                    if (resize.Kind != "resize") throw new ArgumentException("未知终端消息。");
                    manager.Resize(id, resize.Columns, resize.Rows);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { manager.DetachInput(id); }
    });
    await app.StartAsync();
    using var housekeeping = new CancellationTokenSource();
    var housekeepingTask = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try { while (await timer.WaitForNextTickAsync(housekeeping.Token)) manager.Cleanup(); }
        catch (OperationCanceledException) { }
    });
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
    int port = new Uri(addresses.Addresses.Single()).Port;
    HostFiles.Write(Path.Combine(dataRoot, "credentials.json"), credentials);
    HostFiles.Write(Path.Combine(dataRoot, "connection.json"), new HostConnection(port, hostId, Environment.ProcessId));
    if (!quiet) Console.WriteLine($"Shell Track 后台已启动，PID {Environment.ProcessId}。");
    try { await app.WaitForShutdownAsync(); }
    finally
    {
        housekeeping.Cancel(); await housekeepingTask;
        manager.Dispose();
        File.Delete(Path.Combine(dataRoot, "connection.json")); File.Delete(Path.Combine(dataRoot, "credentials.json"));
        await app.DisposeAsync();
    }
}
return 0;

static bool Matches(string value, string expected) => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(value), System.Text.Encoding.UTF8.GetBytes(expected));
static async Task Error(HttpContext context, int status, string code, string message, string requestId)
{
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ApiError(code, message, requestId), Protocol.Json);
}
static async Task Send<T>(WebSocket socket, T value, CancellationToken cancellation)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));
    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json), WebSocketMessageType.Text, true, timeout.Token);
}
