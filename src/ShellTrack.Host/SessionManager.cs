using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ShellTrack.Contracts;
using ShellTrack.Core;
using ShellTrack.Windows;

namespace ShellTrack.Host;

public sealed class SessionManager : IDisposable
{
    public const long OutputLimit = 64L * 1024 * 1024, TotalLimit = 1024L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, Entry> sessions = new();
    private readonly SemaphoreSlim creation = new(1);
    private readonly string root;
    private readonly ILogger<SessionManager> logger;
    private readonly ITerminalFactory factory = new ConPtyFactory();
    private readonly long outputLimit;
    private readonly CancellationTokenSource stopping = new();
    private bool disposed;
    public event Action<SessionInfo>? Completed;

    private sealed class Entry(SessionInfo info, string directory, CreateSessionRequest? initialRequest = null)
    {
        public readonly object Gate = new();
        public SessionInfo Info = info;
        public readonly CreateSessionRequest InitialRequest = initialRequest ?? info.Request;
        public readonly string Directory = directory;
        public readonly LatestLinePreview Preview = new();
        public DateTimeOffset LastCheckpoint;
        public ITerminalSession? Terminal;
        public Task? Completion;
        public bool InputAttached;
        public readonly Queue<(long Offset, byte[] Bytes)> Live = new();
        public int LiveBytes;
    }

    public SessionManager(string dataRoot, ILogger<SessionManager> log, long maxOutput = OutputLimit)
    {
        root = Path.Combine(dataRoot, "sessions");
        logger = log;
        outputLimit = maxOutput;
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(root, "metadata.json", SearchOption.AllDirectories))
        {
            try
            {
                var info = JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(file), Protocol.Json)!;
                var directory = Path.GetDirectoryName(file)!;
                var output = Path.Combine(directory, "output.bin");
                long recorded = File.Exists(output) ? new FileInfo(output).Length : 0;
                if (!info.IsFinished)
                {
                    info = info with
                    {
                        State = SessionState.Interrupted, EndedAt = DateTimeOffset.UtcNow, Error = "后台进程已中断。",
                        NotificationStatus = info.Request.Notify ? "interrupted" : null,
                        NotificationError = info.Request.Notify ? "后台已中断，未发送完成通知。" : null
                    };
                }
                // Older histories had no preview. Also recover bytes written since
                // the last checkpoint, unless a newer live-overflow preview exists.
                if (recorded > 0 && (string.IsNullOrEmpty(info.LatestOutputLine) || recorded > info.OutputLength))
                {
                    var preview = new LatestLinePreview();
                    using var logFile = File.OpenRead(output);
                    var buffer = new byte[16384];
                    int count;
                    while ((count = logFile.Read(buffer)) > 0) preview.Append(buffer.AsSpan(0, count));
                    info = info with { LatestOutputLine = preview.LastLine };
                }
                info = info with { OutputLength = Math.Max(info.OutputLength, recorded), RecordedLength = recorded };
                if (info.NotificationStatus == "pending")
                {
                    string audit = Path.Combine(dataRoot, "notifications", info.Id + ".json");
                    var result = File.Exists(audit)
                        ? JsonSerializer.Deserialize<NotificationResult>(File.ReadAllText(audit), Protocol.Json)
                        : null;
                    result ??= new("interrupted", "通知发送未完成，未重新发送。");
                    info = info with { NotificationStatus = result.Status, NotificationError = result.Detail, Notification = result };
                }
                string initialRequestPath = Path.Combine(directory, "request.json");
                var initialRequest = File.Exists(initialRequestPath)
                    ? JsonSerializer.Deserialize<CreateSessionRequest>(File.ReadAllText(initialRequestPath), Protocol.Json)
                    : info.Request;
                var entry = new Entry(info, directory, initialRequest);
                sessions[info.Id] = entry;
                Persist(entry);
            }
            catch (Exception ex) { logger.LogWarning(ex, "无法恢复记录 {File}", file); }
        }
        Cleanup();
    }
    public IReadOnlyList<SessionInfo> List(int skip = 0, int take = 100)
        => sessions.Values.Select(Snapshot).OrderByDescending(s => s.StartedAt).Skip(skip).Take(take).ToArray();
    public SessionInfo Get(string id) => Snapshot(Find(id));
    private Entry Find(string id) => sessions.TryGetValue(id, out var value) ? value : throw new KeyNotFoundException("任务不存在。");
    private static SessionInfo Snapshot(Entry entry) { lock (entry.Gate) return entry.Info; }

    public async Task<SessionInfo> CreateAsync(CreateSessionRequest request)
    {
        Validate(request);
        await creation.WaitAsync(stopping.Token);
        try
        {
            var previous = sessions.Values.FirstOrDefault(e => e.InitialRequest.RequestId == request.RequestId);
            if (previous is not null)
            {
                if (previous.InitialRequest != request) throw new InvalidOperationException("requestId 已被其他参数使用。");
                return Snapshot(previous);
            }
            Cleanup();
            if (sessions.Values.Count(e => !Snapshot(e).IsFinished) >= 16) throw new InvalidOperationException("最多同时运行 16 个任务。");
            long bytes = sessions.Values.Sum(e => { var snapshot = Snapshot(e); return snapshot.IsFinished ? snapshot.RecordedLength : outputLimit; });
            if (bytes + outputLimit > TotalLimit) throw new InvalidOperationException("历史输出配额不足，请清理已结束记录。");
            string id = Guid.NewGuid().ToString("N");
            var info = new SessionInfo { Id = id, Request = request, State = SessionState.Starting, StartedAt = DateTimeOffset.UtcNow, Columns = request.Columns, Rows = request.Rows, NotificationStatus = request.Notify ? "pending" : null };
            var directory = Path.Combine(root, id);
            Directory.CreateDirectory(directory);
            HostFiles.Write(Path.Combine(directory, "request.json"), request);
            var entry = new Entry(info, directory);
            Persist(entry);
            sessions[id] = entry;
            try
            {
                lock (entry.Gate)
                {
                    entry.Terminal = factory.Start(ResolveShell(request));
                    entry.Info = entry.Info with { State = SessionState.Running, ProcessId = entry.Terminal.ProcessId };
                    Persist(entry);
                    entry.Completion = Task.Run(() => RunAsync(entry));
                }
            }
            catch (Exception ex)
            {
                lock (entry.Gate)
                {
                    entry.Terminal?.Dispose();
                    entry.Info = entry.Info with { State = SessionState.Failed, EndedAt = DateTimeOffset.UtcNow, Error = ex.Message };
                    Persist(entry);
                }
                Completed?.Invoke(Snapshot(entry));
            }
            return Snapshot(entry);
        }
        finally { creation.Release(); }
    }

    private async Task RunAsync(Entry entry)
    {
        var terminal = entry.Terminal!;
        Exception? outputError = null;
        var drain = Task.Run(() =>
        {
            FileStream? log = null;
            try { log = new FileStream(Path.Combine(entry.Directory, "output.bin"), FileMode.Create, FileAccess.Write, FileShare.Read, 4096); }
            catch (Exception ex) { outputError = ex; }
            var buffer = new byte[16384];
            try
            {
                int count;
                while ((count = terminal.Output.Read(buffer)) > 0)
                {
                    lock (entry.Gate)
                    {
                        long offset = entry.Info.OutputLength;
                        int keep = (int)Math.Min(count, outputLimit - entry.Info.RecordedLength);
                        if (log is not null && keep > 0)
                        {
                            try
                            {
                                log.Write(buffer, 0, keep); log.Flush();
                                entry.Info = entry.Info with { RecordedLength = entry.Info.RecordedLength + keep };
                            }
                            catch (Exception ex) { outputError = ex; log.Dispose(); log = null; }
                        }
                        if (keep < count || log is null) entry.Info = entry.Info with { OutputTruncated = true };
                        // Keep forwarding after the recording quota. A bounded ring lets live readers
                        // follow without allowing stalled clients to hold the console or grow memory.
                        if (offset + count > entry.Info.RecordedLength)
                        {
                            int start = (int)Math.Clamp(entry.Info.RecordedLength - offset, 0, count);
                            byte[] live = buffer.AsSpan(start, count - start).ToArray();
                            entry.Live.Enqueue((offset + start, live)); entry.LiveBytes += live.Length;
                            while (entry.LiveBytes > 4 * 1024 * 1024 && entry.Live.Count > 1) entry.LiveBytes -= entry.Live.Dequeue().Bytes.Length;
                        }
                        entry.Preview.Append(buffer.AsSpan(0, count));
                        entry.Info = entry.Info with { OutputLength = offset + count, LatestOutputLine = entry.Preview.LastLine };
                        // Keep crash recovery useful without rewriting metadata for
                        // every output packet. Failure must not stop draining ConPTY.
                        if (DateTimeOffset.UtcNow - entry.LastCheckpoint > TimeSpan.FromSeconds(1))
                        {
                            try { Persist(entry); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                entry.LastCheckpoint = DateTimeOffset.UtcNow;
                                logger.LogWarning(ex, "任务状态保存失败 {Id}", entry.Info.Id);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { outputError ??= ex; }
            finally { log?.Dispose(); }
        });
        try
        {
            int code = await terminal.WaitForExitAsync();
            await drain;
            lock (entry.Gate)
            {
                entry.Info = entry.Info with { State = SessionState.Exited, ExitCode = code, EndedAt = DateTimeOffset.UtcNow, Error = outputError?.Message, OutputTruncated = entry.Info.OutputTruncated || outputError is not null };
                Persist(entry);
            }
        }
        catch (Exception ex)
        {
            terminal.Dispose(); await drain;
            lock (entry.Gate)
            {
                entry.Info = entry.Info with { State = SessionState.Failed, EndedAt = DateTimeOffset.UtcNow, Error = ex.Message, OutputTruncated = true };
                Persist(entry);
            }
        }
        finally { terminal.Dispose(); }
        try { Completed?.Invoke(Snapshot(entry)); }
        catch (Exception ex) { logger.LogWarning(ex, "任务通知失败"); }
    }

    public OutputPage ReadOutput(string id, long offset, int count)
    {
        if (offset < 0 || count is < 1 or > 262144) throw new ArgumentException("输出范围无效。");
        var entry = Find(id);
        lock (entry.Gate)
        {
            var info = entry.Info;
            if (offset > info.OutputLength) throw new ArgumentException("输出位置超出已保存范围。");
            long available = entry.Live.Count > 0 ? entry.Live.Peek().Offset : info.OutputLength;
            bool gap = offset >= info.RecordedLength && offset < available;
            long position = gap ? available : offset;
            var data = new byte[(int)Math.Min(count, (position < info.RecordedLength ? info.RecordedLength : info.OutputLength) - position)];
            if (data.Length > 0 && position < info.RecordedLength)
            {
                using var file = new FileStream(Path.Combine(entry.Directory, "output.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                file.Position = position; file.ReadExactly(data);
            }
            else if (data.Length > 0)
            {
                int copied = 0;
                foreach (var chunk in entry.Live)
                {
                    if (chunk.Offset + chunk.Bytes.Length <= position) continue;
                    int start = (int)Math.Max(0, position - chunk.Offset);
                    int length = Math.Min(chunk.Bytes.Length - start, data.Length - copied);
                    chunk.Bytes.AsSpan(start, length).CopyTo(data.AsSpan(copied));
                    copied += length; position += length;
                    if (copied == data.Length) break;
                }
                position -= copied;
            }
            return new(id, offset.ToString(System.Globalization.CultureInfo.InvariantCulture), (position + data.Length).ToString(System.Globalization.CultureInfo.InvariantCulture), Convert.ToBase64String(data), info.IsFinished && position + data.Length == info.OutputLength, info.OutputTruncated, gap);
        }
    }
    public SessionInfo Terminate(string id)
    {
        var entry = Find(id);
        lock (entry.Gate)
        {
            if (!entry.Info.IsFinished)
            {
                entry.Terminal?.Terminate(); entry.Info = entry.Info with { State = SessionState.Stopping }; Persist(entry);
            }
            return entry.Info;
        }
    }
    public SessionInfo SetNotification(string id, bool enabled)
    {
        var entry = Find(id);
        lock (entry.Gate)
        {
            if (entry.Info.IsFinished) throw new InvalidOperationException("任务已结束，无法调整完成通知。");
            if (entry.Info.Request.Notify == enabled) return entry.Info;
            var updated = entry.Info with
            {
                Request = entry.Info.Request with { Notify = enabled },
                NotificationStatus = enabled ? "pending" : null,
                NotificationError = null, Notification = null
            };
            // Publishing the preference and the completion transition share Gate.
            // Commit to disk first so a failed write cannot acknowledge a change.
            Persist(entry, updated);
            entry.Info = updated;
            return updated;
        }
    }
    public void AttachInput(string id)
    {
        var entry = Find(id);
        lock (entry.Gate)
        {
            if (entry.Info.IsFinished) throw new InvalidOperationException("任务已结束。");
            if (entry.InputAttached) throw new InvalidOperationException("任务已有输入客户端。");
            entry.InputAttached = true;
        }
    }
    public void DetachInput(string id)
    {
        var entry = Find(id);
        bool terminate;
        lock (entry.Gate) { entry.InputAttached = false; terminate = entry.Info.Request.DisconnectPolicy == DisconnectPolicy.Terminate; }
        if (terminate) Terminate(id);
    }
    public void WriteInput(string id, byte[] data) => Find(id).Terminal?.Write(data);
    public void Resize(string id, int columns, int rows)
    {
        ValidateSize(columns, rows);
        var entry = Find(id);
        lock (entry.Gate)
        {
            if (entry.Info.IsFinished) return;
            entry.Terminal?.Resize(columns, rows); entry.Info = entry.Info with { Columns = columns, Rows = rows };
            File.AppendAllText(Path.Combine(entry.Directory, "sizes.jsonl"), JsonSerializer.Serialize(new { offset = entry.Info.OutputLength.ToString(), columns, rows, timestamp = DateTimeOffset.UtcNow }, Protocol.Json) + "\n");
        }
    }
    public void Delete(string id)
    {
        var entry = Find(id);
        lock (entry.Gate)
        {
            if (!sessions.TryGetValue(id, out var current) || current != entry) throw new KeyNotFoundException("任务不存在。");
            if (!entry.Info.IsFinished) throw new InvalidOperationException("只能删除已结束的任务。");
            Directory.Delete(entry.Directory, true); sessions.TryRemove(id, out _);
            DeleteNotificationAudit(id);
        }
    }
    public void RecordNotification(string id, NotificationResult result)
    {
        if (!sessions.TryGetValue(id, out var entry))
        {
            DeleteNotificationAudit(id);
            return;
        }
        lock (entry.Gate)
        {
            // History may have been deleted while the external notification helper was running.
            if (!Directory.Exists(entry.Directory))
            {
                DeleteNotificationAudit(id);
                return;
            }
            entry.Info = entry.Info with { NotificationStatus = result.Status, NotificationError = result.Detail, Notification = result };
            Persist(entry);
        }
    }
    public void Cleanup()
    {
        foreach (var entry in sessions.Values)
        {
            var info = Snapshot(entry);
            if (info.IsFinished && info.EndedAt < DateTimeOffset.UtcNow.AddDays(-7))
            {
                try { Delete(info.Id); }
                catch (KeyNotFoundException) { } // Another client already removed it.
                catch (IOException ex) { logger.LogWarning(ex, "历史任务清理失败 {Id}", info.Id); }
                catch (UnauthorizedAccessException ex) { logger.LogWarning(ex, "历史任务清理失败 {Id}", info.Id); }
            }
        }
    }
    private void DeleteNotificationAudit(string id)
    {
        string path = Path.Combine(Path.GetDirectoryName(root)!, "notifications", id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
    private static void Persist(Entry entry, SessionInfo? info = null)
    {
        var file = Path.Combine(entry.Directory, "metadata.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(info ?? entry.Info, Protocol.Json)); File.Move(file + ".tmp", file, true);
        entry.LastCheckpoint = DateTimeOffset.UtcNow;
    }
    private static void Validate(CreateSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128) throw new ArgumentException("requestId 长度无效。");
        if (!Directory.Exists(request.WorkingDirectory)) throw new ArgumentException("工作目录不存在。");
        if (request.Command?.Length > 24000) throw new ArgumentException("命令过长。");
        if (string.IsNullOrWhiteSpace(request.Shell) || request.Command?.Contains('\0') == true) throw new ArgumentException("shell 或命令无效。");
        if (!Enum.IsDefined(request.DisconnectPolicy)) throw new ArgumentException("断开策略无效。");
        ValidateSize(request.Columns, request.Rows);
    }
    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > 1000 || rows is < 1 or > 1000) throw new ArgumentException("终端尺寸应在 1 至 1000 之间。");
    }
    private static TerminalStart ResolveShell(CreateSessionRequest request)
    {
        string shell = request.Shell.ToLowerInvariant();
        string executable;
        List<string> arguments = [];
        string? rawArguments = null;
        if (shell is "cmd" or "cmd.exe")
        {
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            arguments.Add("/D");
            if (request.Command is not null) rawArguments = "/D /S /C \"" + request.Command + "\"";
        }
        else if (shell is "pwsh" or "pwsh.exe" or "powershell" or "powershell.exe")
        {
            executable = shell.StartsWith("powershell", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : FindOnPath("pwsh.exe");
            arguments.Add("-NoLogo");
            if (request.Command is not null)
            {
                arguments.Add("-NoProfile"); arguments.Add("-EncodedCommand");
                arguments.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(request.Command)));
            }
        }
        else throw new ArgumentException("当前支持 pwsh、powershell 和 cmd。");
        return new(executable, arguments, Path.GetFullPath(request.WorkingDirectory), request.Columns, request.Rows, rawArguments);
    }
    private static string FindOnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string path = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(path)) return path;
        }
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", name);
        return File.Exists(standard) ? standard : throw new FileNotFoundException("未找到 pwsh，请安装 PowerShell 7。");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        foreach (var entry in sessions.Values)
            if (!Snapshot(entry).IsFinished) try { entry.Terminal?.Terminate(); } catch (Exception ex) { logger.LogWarning(ex, "任务终止失败"); }
        Task.WaitAll(sessions.Values.Select(e => e.Completion).OfType<Task>().ToArray(), TimeSpan.FromSeconds(10));
        stopping.Dispose(); creation.Dispose();
    }
}
