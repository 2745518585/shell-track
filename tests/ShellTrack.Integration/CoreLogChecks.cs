using System.Text;
using ShellTrack.Core;

internal static class CoreLogChecks
{
    public static void Run(Action<bool, string> check)
    {
        var log = new TextLog();
        check(log.GetLines().Count == 0 && log.FirstLineNumber == 1, "空日志不产生虚构行号");
        byte[] bytes = Encoding.UTF8.GetBytes("\x1b]0;hidden title\a中文\x1b[31m red\x1b[0m\r\n");
        foreach (byte value in bytes) log.Append(new byte[] { value });
        var lines = log.GetLines();
        check(lines.Count == 2 && lines[0] == new NumberedLogLine(1, "中文 red") && lines[1] == new NumberedLogLine(2, ""), "UTF-8 与 CSI/OSC 跨字节输出仍保留逻辑行号");
        log.Append(Encoding.UTF8.GetBytes("next\n\nlast"));
        lines = log.GetLines();
        check(lines.Select(line => line.Number).SequenceEqual(new long[] { 1, 2, 3, 4 }) && lines[2].Text == "" && lines[3].Text == "last", "空行与跨块追加保持连续逻辑行号");
        check(log.LastLine == "last", "最新行预览读取当前未结束行");
        var cursorLog = new TextLog();
        foreach (byte value in Encoding.UTF8.GetBytes("中文\x1b[8;1Hnext\n")) cursorLog.Append(new byte[] { value });
        check(cursorLog.GetLines()[0].Text == "中文" && cursorLog.GetLines()[1].Text == "next", "ConPTY 绝对行定位不会把相邻输出粘成一行");
        var cursorPreview = new LatestLinePreview();
        cursorPreview.Append(Encoding.UTF8.GetBytes("中文\x1b[8;1Hnext\n"));
        check(cursorPreview.LastLine == "next", "ConPTY 光标定位后的卡片预览保留最新一行");

        var carriage = new TextLog();
        carriage.Append(Encoding.UTF8.GetBytes("abc\rxy"));
        check(carriage.GetLines().Single() == new NumberedLogLine(1, "xyc"), "进度输出的回车覆写不会增加行号");
        carriage.Append(Encoding.UTF8.GetBytes("\rabc\bX\n"));
        check(carriage.GetLines()[0].Text == "abX" && carriage.LastLine == "abX", "退格覆写与行尾换行保持最后有效输出");

        var longLine = new TextLog(128);
        string longText = new('x', 20_000);
        longLine.Append(Encoding.UTF8.GetBytes(longText));
        check(longLine.GetLines().Single() == new NumberedLogLine(1, longText), "长行即使超过保留阈值也只有一个逻辑行号");

        var bounded = new TextLog(256);
        for (int number = 1; number <= 50; number++) bounded.Append(Encoding.UTF8.GetBytes($"row-{number:D3}: " + new string('x', 40) + "\n"));
        lines = bounded.GetLines();
        check(bounded.FirstLineNumber > 1 && lines[0].Number == bounded.FirstLineNumber && lines[^1].Number == 51, "日志裁剪后保留绝对行号");
        check(lines.Take(lines.Count - 1).All(line => line.Text == $"row-{line.Number:D3}: " + new string('x', 40)) && lines.Zip(lines.Skip(1)).All(pair => pair.Second.Number == pair.First.Number + 1), "有界日志只删除整行并保持剩余行完整");

        var gap = new TextLog();
        gap.Append(Encoding.UTF8.GetBytes("before\x1b[31"));
        gap.ResetAfterGap();
        gap.Append(Encoding.UTF8.GetBytes("after"));
        check(gap.GetLines()[^1].Text == "after" && gap.GetLines()[^1].Number > 1, "输出缺口清空未完成的转义序列并分隔逻辑行");

        var preview = new LatestLinePreview();
        foreach (byte value in bytes) preview.Append(new byte[] { value });
        check(preview.LastLine == "中文 red", "卡片预览逐字节解码 UTF-8 与 ANSI 控制序列");
        preview.Append(Encoding.UTF8.GetBytes("abc\rxy\n\n"));
        check(preview.LastLine == "xyc", "卡片预览保留回车覆写后最后非空行");
        preview.Append(Encoding.UTF8.GetBytes(new string('x', 100_000)));
        check(preview.LastLine.Length == 400 && preview.LastLine.EndsWith('…'), "超长输出的卡片预览限制在 400 字符");
        preview.Append(Encoding.UTF8.GetBytes("\nNEXT"));
        check(preview.LastLine == "NEXT", "超长行之后的预览可以继续更新");

        var emojiPreview = new LatestLinePreview();
        emojiPreview.Append(Encoding.UTF8.GetBytes(new string('x', 398) + "😀tail"));
        check(emojiPreview.LastLine.Length <= 400 && emojiPreview.LastLine.EndsWith('…') && new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(emojiPreview.LastLine)) == emojiPreview.LastLine, "截断卡片预览不会产生不完整的 emoji 字符");
    }
}
