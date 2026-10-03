using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ShellTrack.Contracts;

namespace ShellTrack.Client;

public sealed class ShellTrackClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string? terminalToken;
    public HostConnection Connection { get; }
    public string DataRoot { get; }
    public event Action<long, long>? OutputGap;
    public static string DefaultDataRoot => Environment.GetEnvironmentVariable("SHELLTRACK_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellTrack");

    private ShellTrackClient(string root, HostConnection connection, ClientCredentials credentials, bool readOnly, bool allowTerminal)
    {
        DataRoot = root; Connection = connection; terminalToken = allowTerminal ? credentials.Terminal : null;
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{connection.Port}"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readOnly ? credentials.Read : credentials.Manage);
    }

    public static async Task<ShellTrackClient> ConnectAsync(string? dataRoot = null, bool autoStart = true, bool readOnly = false, CancellationToken cancellation = default, bool allowTerminal = false)
    {
        string root = Path.GetFullPath(dataRoot ?? DefaultDataRoot);
        bool launched = false;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            ShellTrackClient? client = null;
            try
            {
                var connection = JsonSerializer.Deserialize<HostConnection>(await File.ReadAllTextAsync(Path.Combine(root, "connection.json"), cancellation), Protocol.Json)!;
                var credentials = JsonSerializer.Deserialize<ClientCredentials>(await File.ReadAllTextAsync(Path.Combine(root, "credentials.json"), cancellation), Protocol.Json)!;
                client = new(root, connection, credentials, readOnly, allowTerminal);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                var health = await client.Read<HealthInfo>("/v1/health", timeout.Token);
                if (health.HostId != connection.HostId || health.ProtocolVersion != 1) throw new IOException("后台身份或协议不匹配。");
                return client;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
            {
                client?.Dispose();
                cancellation.ThrowIfCancellationRequested();
                if (!autoStart) throw new IOException("无法连接 Shell Track 后台。", ex);
            }
            if (!launched)
            {
                string host = FindApplication("ShellTrack.Host") ?? throw new FileNotFoundException("找不到后台程序。请先构建解决方案或使用发布目录。");
                string executable = Path.ChangeExtension(host, ".exe");
                bool appHost = File.Exists(executable);
                DetachedHost.Start(appHost ? executable : "dotnet",
                    appHost ? ["--data-dir", root, "--quiet"] : [host, "--data-dir", root, "--quiet"]);
                launched = true;
            }
            await Task.Delay(100, cancellation);
        }
        throw new TimeoutException("后台未能在 10 秒内启动，请手动运行 Host 查看错误。");
    }

    public static string? FindApplication(string name)
    {
        string adjacent = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        if (File.Exists(adjacent)) return adjacent;
        string nested = Path.Combine(AppContext.BaseDirectory, name, name + ".dll");
        if (File.Exists(nested)) return nested;
        string desktop = Path.Combine(AppContext.BaseDirectory, "desktop", name + ".dll");
        if (File.Exists(desktop)) return desktop;
        string parent = Path.Combine(AppContext.BaseDirectory, "..", name + ".dll");
        if (File.Exists(parent)) return Path.GetFullPath(parent);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "ShellTrack.slnx"))) continue;
            string bin = Path.Combine(directory.FullName, "src", name, "bin");
            if (!Directory.Exists(bin)) return null;
            return Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "ref" + Path.DirectorySeparatorChar))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }

    public Task<SessionInfo[]> ListAsync(int skip = 0, int take = 1000, CancellationToken cancellation = default) => Read<SessionInfo[]>($"/v1/tasks?skip={skip}&take={take}", cancellation);
    public Task<HealthInfo> HealthAsync(CancellationToken cancellation = default) => Read<HealthInfo>("/v1/health", cancellation);
    public Task<SessionInfo> GetAsync(string id, CancellationToken cancellation = default) => Read<SessionInfo>($"/v1/tasks/{Uri.EscapeDataString(id)}", cancellation);
    public Task<OutputPage> OutputAsync(string id, long offset, CancellationToken cancellation = default) => Read<OutputPage>($"/v1/tasks/{Uri.EscapeDataString(id)}/output?offset={offset}", cancellation);
    public async Task<SessionInfo> CreateAsync(CreateSessionRequest request, CancellationToken cancellation = default)
    {
        using var response = await http.PostAsJsonAsync("/v1/tasks", request, Protocol.Json, cancellation);
        await Ensure(response, cancellation); return (await response.Content.ReadFromJsonAsync<SessionInfo>(Protocol.Json, cancellation))!;
    }
    public async Task<SessionInfo> TerminateAsync(string id, CancellationToken cancellation = default)
    {
        using var response = await http.PostAsync($"/v1/tasks/{Uri.EscapeDataString(id)}/terminate", null, cancellation);
        await Ensure(response, cancellation); return (await response.Content.ReadFromJsonAsync<SessionInfo>(Protocol.Json, cancellation))!;
    }
    public async Task<SessionInfo> SetNotificationAsync(string id, bool enabled, CancellationToken cancellation = default)
    {
        using var response = await http.PostAsJsonAsync($"/v1/tasks/{Uri.EscapeDataString(id)}/notification", new NotificationPreference(enabled), Protocol.Json, cancellation);
        await Ensure(response, cancellation); return (await response.Content.ReadFromJsonAsync<SessionInfo>(Protocol.Json, cancellation))!;
    }
    public async Task DeleteAsync(string id, CancellationToken cancellation = default)
    {
        using var response = await http.DeleteAsync($"/v1/tasks/{Uri.EscapeDataString(id)}", cancellation); await Ensure(response, cancellation);
    }
    public async Task ShutdownAsync(CancellationToken cancellation = default)
    {
        using var response = await http.PostAsync("/v1/shutdown", null, cancellation); await Ensure(response, cancellation);
    }
    public async Task<SessionInfo[]> ListAllAsync(CancellationToken cancellation = default)
    {
        var result = new List<SessionInfo>();
        while (true)
        {
            var page = await ListAsync(result.Count, 1000, cancellation);
            result.AddRange(page);
            if (page.Length < 1000) return result.DistinctBy(s => s.Id).ToArray();
        }
    }
    public async Task<ClientWebSocket> OpenTerminalAsync(string id, CancellationToken cancellation = default)
    {
        if (terminalToken is null) throw new InvalidOperationException("该客户端未启用终端输入能力。");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + terminalToken);
        try { await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Connection.Port}/v1/tasks/{Uri.EscapeDataString(id)}/terminal"), cancellation); return socket; }
        catch { socket.Dispose(); throw; }
    }
    public async IAsyncEnumerable<byte[]> FollowOutputAsync(string id, long offset = 0, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        while (true)
        {
            var page = await OutputAsync(id, offset, cancellation);
            var data = Convert.FromBase64String(page.Data);
            if (page.Gap) OutputGap?.Invoke(offset, long.Parse(page.NextOffset, System.Globalization.CultureInfo.InvariantCulture) - data.Length);
            offset = long.Parse(page.NextOffset, System.Globalization.CultureInfo.InvariantCulture);
            if (data.Length > 0) yield return data;
            if (page.Complete) yield break;
            if (data.Length == 0) await Task.Delay(40, cancellation);
        }
    }
    private async Task<T> Read<T>(string path, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(path, cancellation);
        await Ensure(response, cancellation); return (await response.Content.ReadFromJsonAsync<T>(Protocol.Json, cancellation))!;
    }
    private static async Task Ensure(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode) return;
        var error = await response.Content.ReadFromJsonAsync<ApiError>(Protocol.Json, cancellation);
        throw new HttpRequestException(error?.Message ?? response.ReasonPhrase, null, response.StatusCode);
    }
    public void Dispose() => http.Dispose();
}
