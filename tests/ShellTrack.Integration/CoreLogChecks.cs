using System.Text;
using ShellTrack.Core;

internal static class CoreLogChecks
{
    public static void Run(Action<bool, string> check)
    {
        var document = new LogDocument();
        document.Update([new(100, "中文😀"), new(101, ""), new(102, "last"), new(103, "")]);
        check(document.Text == "中文😀\r\rlast\r" && document.Starts.SequenceEqual(new[] { 0, 5, 6, 11 }),
            "连续日志文档保留空行与真实换行，文本中不混入行号");
        check(document.LineAt(4) == 0 && document.LineAt(5) == 1 && document.LineAt(7) == 2 && document.LineAt(11) == 3,
            "UTF-16 文本位置正确映射到逻辑行，包含中文和 emoji");
        check(document.SelectedText(1, 8) == "文😀\n\nla" && document.SelectedText(8, 1) == "文😀\n\nla",
            "正反向跨行复制保留空行与正文片段，不添加行号或折行");
        check(document.SelectedText(0, document.Text.Length + 1) == "中文😀\n\nlast\n",
            "全选复制排除原生文档额外的末尾段落标记");
        document.Update([new(1, "😀 old")]);
        check(document.Update([new(1, "😁 new")]) == 0, "增量替换不会从 emoji 的半个字符对开始");
        document.Update([new(20, "line")]);
        check(document.Update([new(20, "line"), new(21, "next")]) == 4 && document.Text == "line\rnext", "追加日志只替换变化的尾部");
        document.Update([]);
        check(document.Text == "" && document.LineAt(0) == -1, "切换任务清空连续文档及行号映射");
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
        var controlStrings = new TextLog();
        foreach (byte value in Encoding.UTF8.GetBytes("a\x1bPignored\aSTILL_HIDDEN\x1b\\b\x1b_hidden image\x1b\\c\x1b^private\x1b\\d\x1bXsos\x1b\\e\u0090hidden\u009cf")) controlStrings.Append(new byte[] { value });
        check(controlStrings.ToString() == "abcdef", "跨块终端控制字符串和图片负载不泄漏到日志正文");
        var links = new TextLog();
        links.Append(Encoding.UTF8.GetBytes("\x1b]8;;https://example.com\x1b\\链接\x1b]8;;\x1b\\\x1bPcancelled\x18OK"));
        check(links.ToString() == "链接OK", "超链接保留可读文本，取消控制序列后正常显示输出");
        var cursorLog = new TextLog();
        foreach (byte value in Encoding.UTF8.GetBytes("中文\x1b[8;1Hnext\n")) cursorLog.Append(new byte[] { value });
        check(cursorLog.GetLines()[0].Text == "中文" && cursorLog.GetLines()[7].Text == "next", "ConPTY 绝对行定位保留行位置且不会把相邻输出粘成一行");
        var cursorPreview = new LatestLinePreview();
        cursorPreview.Append(Encoding.UTF8.GetBytes("中文\x1b[8;1Hnext\n"));
        check(cursorPreview.LastLine == "next", "ConPTY 光标定位后的卡片预览保留最新一行");

        string redraw = "PowerShell\r\nPS> \x1b[93me\x1b[97mexit\x1b[2;6H\b\x1b[93mec\x1b[97mho 0\x1b[2;5Hecho 0\r\n0\r\nPS> exit\r\n";
        var edited = new TextLog();
        foreach (byte value in Encoding.UTF8.GetBytes(redraw)) edited.Append(new byte[] { value });
        check(edited.GetLines().Select(line => line.Text).SequenceEqual(new[] { "PowerShell", "PS> echo 0", "0", "PS> exit", "" }), "PSReadLine 预测与逐字重绘只保留最终命令和一次输出");
        var editedPreview = new LatestLinePreview(); editedPreview.Append(Encoding.UTF8.GetBytes(redraw));
        check(editedPreview.LastLine == "PS> exit", "任务摘要也正确解析交互式重绘");
        var erase = new TextLog(); erase.Append(Encoding.UTF8.GetBytes("progress 100%\rOK\x1b[K\n"));
        check(erase.GetLines()[0].Text == "OK", "擦除行尾删除被短文本覆盖后的旧内容");
        erase.Append(Encoding.UTF8.GetBytes("abcdef\x1b[3DXY\x1b[1GZ"));
        check(erase.LastLine == "ZbcXYf", "相对光标移动和绝对列定位覆写正确字符");
        var wide = new TextLog(); wide.Append(Encoding.UTF8.GetBytes("中文> old\x1b[1;7HNEW\x1b[K"));
        check(wide.LastLine == "中文> NEW", "中文宽字符使用终端列宽而非 UTF-16 下标定位");
        var scrolled = new TextLog(terminalRows: 3); scrolled.Append(Encoding.UTF8.GetBytes("one\ntwo\nthree\nfour\x1b[2;1HEDIT\x1b[K"));
        check(scrolled.GetLines().Select(line => line.Text).SequenceEqual(new[] { "one", "two", "EDIT", "four" }), "滚屏后的绝对行定位保留历史并编辑当前视口");
        var cleared = new TextLog(); cleared.Append(Encoding.UTF8.GetBytes("old\nold2\x1b[2J\x1b[Hnew\x1b[K"));
        check(cleared.GetLines()[0].Text == "new" && !cleared.ToString().Contains("old"), "清屏重绘不混入屏幕上的旧文本");
        var saved = new TextLog(); saved.Append(Encoding.UTF8.GetBytes("abc\x1b[s\nnext\x1b[uX"));
        check(saved.GetLines()[0].Text == "abcX" && saved.GetLines()[1].Text == "next", "保存和恢复光标不会将重绘追加到最后一行");
        var combining = new TextLog(); combining.Append(Encoding.UTF8.GetBytes("e\u0301> old\x1b[1;4HNEW"));
        check(combining.LastLine == "e\u0301> NEW", "组合附加符不占用额外光标列");
        var eraseCharacters = new TextLog(); eraseCharacters.Append(Encoding.UTF8.GetBytes("abcde\r\x1b[2C\x1b[2XZ"));
        check(eraseCharacters.LastLine == "abZ e", "擦除字符保持光标位置并保留后续字符");

        var carriage = new TextLog();
        carriage.Append(Encoding.UTF8.GetBytes("abc\rxy"));
        check(carriage.GetLines().Single() == new NumberedLogLine(1, "xyc"), "进度输出的回车覆写不会增加行号");
        carriage.Append(Encoding.UTF8.GetBytes("\rabc\bX\n"));
        check(carriage.GetLines()[0].Text == "abX" && carriage.LastLine == "abX", "退格覆写与行尾换行保持最后有效输出");

        var longLine = new TextLog(128);
        string longText = new('x', 20_000);
        longLine.Append(Encoding.UTF8.GetBytes(longText));
        check(longLine.GetLines().Single() == new NumberedLogLine(1, longText), "长行即使超过保留阈值也只有一个逻辑行号");
        var tail = new TextLog(128, trimCurrentLine: true);
        tail.Append(Encoding.UTF8.GetBytes(longText + "TAIL"));
        check(tail.ToString().Length <= 128 && tail.LastLine.EndsWith("TAIL") && tail.GetLines().Single().Number == 1,
            "卡片尾部模式限制超长行内存并保留末尾内容和逻辑行号");
        tail.Append(Encoding.UTF8.GetBytes("\nNEXT"));
        check(tail.GetLines()[^1] == new NumberedLogLine(2, "NEXT"), "截断超长行后仍正确追加和编号新行");
        var emojiTail = new TextLog(6, trimCurrentLine: true);
        emojiTail.Append(Encoding.UTF8.GetBytes("xxxx😀END"));
        check(new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(emojiTail.ToString())) == emojiTail.ToString(),
            "卡片尾部裁剪不会切断 UTF-16 字符对");

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
