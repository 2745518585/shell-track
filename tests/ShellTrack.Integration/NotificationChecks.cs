using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ShellTrack.Contracts;
using ShellTrack.Core;
using ShellTrack.Host;

internal static class NotificationChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check, CancellationToken cancellation)
    {
        var matcher = new OutputNotificationMatcher(["never", "中文.*ERROR"]);
        OutputNotificationMatch? matched = null;
        foreach (byte b in Encoding.UTF8.GetBytes("\x1b]0;ERROR hidden\a中文\x1b[31m ERROR\x1b[0m"))
            matched ??= matcher.Append(new byte[] { b });
        check(matched is { Pattern: "中文.*ERROR", Text: "中文 ERROR" }, "输出正则跨 UTF-8 字节和 ANSI 序列匹配，多个条件为 OR");
        var hidden = new OutputNotificationMatcher(["hidden"]);
        check(hidden.Append(Encoding.UTF8.GetBytes("\x1b]0;hidden\aactual")) is null, "终端标题等控制序列不触发输出条件");
        var multiline = new OutputNotificationMatcher([@"^second\nthird"]);
        check(multiline.Append(Encoding.UTF8.GetBytes("first\r\nsecond\r\nthi")) is null &&
            multiline.Append(Encoding.UTF8.GetBytes("rd"))?.Pattern == @"^second\nthird", "输出正则跨片段和换行匹配");
        var bounded = new OutputNotificationMatcher(["WINDOW_END"]);
        for (int i = 0; i < 20; i++) bounded.Append(Encoding.UTF8.GetBytes(new string('x', 16384)));
        check(bounded.Append(Encoding.UTF8.GetBytes("WINDOW_END")) is not null, "超长逻辑行仍有界并匹配最新输出");
        var anchored = new OutputNotificationMatcher(["^TARGET"]);
        anchored.Append(Encoding.UTF8.GetBytes(new string('x', 32768)));
        anchored.Append(Encoding.UTF8.GetBytes("TARGET" + new string('x', 32768 - 6)));
        check(anchored.Append(Encoding.UTF8.GetBytes("x")) is null, "滚动窗口截断不把逻辑行中间误判为行首");
        var timeout = new OutputNotificationMatcher(["(a+)+$", "SAFE"]);
        check(timeout.Append(Encoding.UTF8.GetBytes(new string('a', 30000) + "!SAFE"))?.Pattern == "SAFE" && timeout.Error is not null,
            "病态正则超时停用，其他条件继续匹配");
        check(timeout.Append(Encoding.UTF8.GetBytes(" next SAFE")) is not null, "超时条件不重复阻塞后续输出");

        string dataRoot = Path.Combine(root, "notification-conditions");
        var sent = new ConcurrentQueue<SessionInfo>();
        using (var manager = new SessionManager(dataRoot, NullLogger<SessionManager>.Instance, 1024))
        {
            manager.NotificationRequested += sent.Enqueue; // No real Windows toasts during automated tests.
            var request = Request("echo READY & ping -n 3 127.0.0.1 >nul & echo READY & exit /b 0") with { Notify = true, NotifyPatterns = ["UNMATCHED", "READY"] };
            var task = await manager.CreateAsync(request);
            await Until(() => manager.Get(task.Id).NotificationTrigger is not null && sent.Any(s => s.Id == task.Id));
            check(!manager.Get(task.Id).IsFinished && sent.Count(s => s.Id == task.Id) == 1, "输出条件在任务运行中立即触发");
            check(manager.Get(task.Id).NotificationTrigger is { Kind: "outputMatch", Pattern: "READY", MatchedText: "READY" }, "记录命中条件、时间与匹配内容");
            check((await manager.CreateAsync(request with { NotifyPatterns = request.NotifyPatterns.ToArray() })).Id == task.Id, "正则数组按内容比较，创建重试保持幂等");
            await Until(() => manager.Get(task.Id).IsFinished);
            await Task.Delay(100, cancellation);
            check(sent.Count(s => s.Id == task.Id) == 1, "重复匹配和随后结束不重复通知同一任务");

            var end = await manager.CreateAsync(Request("echo no_match & exit /b 4") with { Notify = true, NotifyPatterns = ["ABSENT"] });
            await Until(() => manager.Get(end.Id).NotificationTrigger is not null && sent.Any(s => s.Id == end.Id));
            check(manager.Get(end.Id).NotificationTrigger?.Kind == "completed" && sent.Count(s => s.Id == end.Id) == 1, "输出不匹配时，结束条件仍触发 OR 通知");
            var none = await manager.CreateAsync(Request("echo ordinary") with { NotifyPatterns = ["ABSENT"] });
            await Until(() => manager.Get(none.Id).NotificationStatus == "notMatched");
            check(manager.Get(none.Id).NotificationTrigger is null && !sent.Any(s => s.Id == none.Id), "仅正则且未匹配不发送结束通知");

            var live = await manager.CreateAsync(Request("echo OLD & ping -n 3 127.0.0.1 >nul & echo FUTURE") with { NotifyPatterns = ["ABSENT"] });
            await Until(() => manager.Get(live.Id).LatestOutputLine.Contains("OLD"));
            manager.SetNotification(live.Id, false, ["FUTURE"]);
            manager.SetNotification(live.Id, true);
            check(manager.Get(live.Id).Request.NotifyPatterns.SequenceEqual(["FUTURE"]), "旧的结束开关接口保留正则条件");
            await Until(() => manager.Get(live.Id).NotificationTrigger is not null);
            check(manager.Get(live.Id).NotificationTrigger?.Pattern == "FUTURE", "运行中更新条件匹配保存后的新输出");
            var partial = await manager.CreateAsync(Request("echo PREFIX & ping -n 3 127.0.0.1 >nul & echo SUFFIX") with { NotifyPatterns = ["(?s)PREFIX.*SUFFIX"] });
            await Until(() => manager.Get(partial.Id).LatestOutputLine.Contains("PREFIX"));
            manager.SetNotification(partial.Id, true);
            await Until(() => manager.Get(partial.Id).NotificationTrigger is not null);
            check(manager.Get(partial.Id).NotificationTrigger?.Kind == "outputMatch", "调整结束条件保留正则的跨片段匹配状态");

            var quota = await manager.CreateAsync(Request("for /L %i in (1,1,300) do @echo padding_padding_padding & echo AFTER_QUOTA") with { NotifyPatterns = ["AFTER_QUOTA"] });
            await Until(() => manager.Get(quota.Id).NotificationTrigger is not null);
            check(manager.Get(quota.Id).OutputTruncated && manager.Get(quota.Id).NotificationTrigger?.Pattern == "AFTER_QUOTA", "记录配额耗尽后仍匹配实时输出");
            await Until(() => manager.Get(live.Id).IsFinished && manager.Get(quota.Id).IsFinished);
        }
        using var recovered = new SessionManager(dataRoot, NullLogger<SessionManager>.Instance, 1024);
        check(recovered.List().Any(s => s.NotificationTrigger?.Pattern == "READY" && s.Request.NotifyPatterns.SequenceEqual(["UNMATCHED", "READY"])),
            "重启恢复通知条件与触发记录，不重发通知");

        CreateSessionRequest Request(string command) => new() { Shell = "cmd", Command = command, WorkingDirectory = root };
        async Task Until(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(30, cancellation);
        }
    }
}
