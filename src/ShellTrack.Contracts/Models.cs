using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShellTrack.Contracts;

public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

public enum SessionState { Starting, Running, Stopping, Exited, Failed, Interrupted }
public enum DisconnectPolicy { Continue, Terminate }

public sealed record CreateSessionRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Shell { get; init; } = "pwsh";
    public string? Command { get; init; }
    // Null uses Shell Track's command mode; empty means a shell with no arguments.
    public string? RawArguments { get; init; }
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;
    public int Columns { get; init; } = 120;
    public int Rows { get; init; } = 30;
    public DisconnectPolicy DisconnectPolicy { get; init; } = DisconnectPolicy.Continue;
    public bool Notify { get; init; }
    public string[] NotifyPatterns { get; init; } = [];
}

public sealed record SessionInfo
{
    public required string Id { get; init; }
    public required CreateSessionRequest Request { get; init; }
    public SessionState State { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int? ProcessId { get; init; }
    public int? ExitCode { get; init; }
    public string? Error { get; init; }
    public long OutputLength { get; init; }
    public long RecordedLength { get; init; }
    public bool OutputTruncated { get; init; }
    public string LatestOutputLine { get; init; } = "";
    public int Columns { get; init; }
    public int Rows { get; init; }
    public string? NotificationStatus { get; init; }
    public string? NotificationError { get; init; }
    public NotificationResult? Notification { get; init; }
    public NotificationTrigger? NotificationTrigger { get; init; }
    public string? NotificationConditionError { get; init; }
    public bool IsFinished => State is SessionState.Exited or SessionState.Failed or SessionState.Interrupted;
}

public sealed record OutputPage(string TaskId, string Offset, string NextOffset, string Data, bool Complete, bool Truncated, bool Gap = false);
public sealed record ApiError(string Code, string Message, string RequestId);
public sealed record HealthInfo(string HostId, int ProtocolVersion, string Version, bool SupportsRawArguments = false, bool SupportsNotificationConditions = false);
public sealed record HostConnection(int Port, string HostId, int ProcessId);
public sealed record ClientCredentials(string Read, string Manage, string Terminal);
public sealed record SessionEvent(string Kind, string TaskId, SessionInfo? Session = null);
public sealed record TerminalMessage(string Kind, string? Data = null, int Columns = 0, int Rows = 0);
public sealed record NotificationResult(string Status, string? Detail = null, uint WindowsId = 0, string? Setting = null, string? SystemState = null, bool? InHistory = null);
public sealed record NotificationPreference([property: JsonRequired] bool Enabled, string[]? Patterns = null);
public sealed record NotificationTrigger(string Kind, DateTimeOffset At, string? Pattern = null, string? MatchedText = null);
