using System;

namespace JVLinkServerTray.Tests
{
    public static class TrayTextTests
    {
        static HealthReport Restarts(long count, string reason)
        {
            return new HealthReport { Kind = HealthKind.Healthy, HttpStatus = 200, Status = "healthy", RestartCount = count, LastRestartReason = reason };
        }

        [Test] public static void TrayText_sourceIsReadAsUtf8() {
            // A source file read in the wrong code page would turn the Japanese literals into other characters.
            Assert.Equal(0x7DD1, (int)TrayText.ColorName(TrayColor.Green)[0], "緑 is U+7DD1");
            Assert.Equal(6, TrayText.ColorWord(TrayColor.Green).Length, "動いています has 6 characters");
        }
        [Test] public static void TrayText_colourWords() {
            Assert.Equal("動いています", TrayText.ColorWord(TrayColor.Green), "green");
            Assert.Equal("確かめています", TrayText.ColorWord(TrayColor.Yellow), "yellow");
            Assert.Equal("止まっています", TrayText.ColorWord(TrayColor.Red), "red");
            Assert.Equal("止めています", TrayText.ColorWord(TrayColor.Gray), "gray");
        }
        [Test] public static void TrayText_tooltip_short_isExact() {
            Assert.Equal("JVLinkServer: 動いています 正常に動いています", TrayText.Tooltip(TrayColor.Green, "正常に動いています"), "text");
            Assert.Equal("JVLinkServer: 止めています", TrayText.Tooltip(TrayColor.Gray, null), "no reason");
        }
        [Test] public static void TrayText_tooltip_long_isCutTo63() {
            string reason = new string('あ', 100);
            string t = TrayText.Tooltip(TrayColor.Red, reason);
            Assert.Equal(63, t.Length, "length");
            Assert.True(t.StartsWith("JVLinkServer: 止まっています あ"), "start: " + t);
            Assert.True(t.EndsWith("…"), "ellipsis");
            Assert.True(TrayText.Tooltip(TrayColor.Red, "JVLinkServer.exe が見つかりません（ini の ServerExe）").Length <= 63, "real reason");
        }
        [Test] public static void TrayText_cut_neverSplitsASurrogatePair() {
            string s = "ab" + "\uD83D\uDE00" + "cdef";        // a, b, one emoji (2 chars), c, d, e, f
            string t = TrayText.Cut(s, 4);                       // would end inside the emoji
            Assert.Equal("ab…", t, "cut");
            Assert.Equal("abc", TrayText.Cut("abc", 3), "fits");
            Assert.Equal("", TrayText.Cut(null, 5), "null");
        }
        [Test] public static void TrayText_duration() {
            Assert.Equal("1 分未満", TrayText.Duration(TimeSpan.FromSeconds(59)), "seconds");
            Assert.Equal("12 分", TrayText.Duration(TimeSpan.FromMinutes(12.5)), "minutes");
            Assert.Equal("3 時間 12 分", TrayText.Duration(new TimeSpan(3, 12, 40)), "hours");
            Assert.Equal("2 日 4 時間", TrayText.Duration(new TimeSpan(2, 4, 30, 0)), "days");
            Assert.Equal("1 分未満", TrayText.Duration(TimeSpan.FromSeconds(-5)), "negative");
        }
        [Test] public static void TrayText_statusLine_perColour_withThePort() {
            DateTime at = new DateTime(2026, 11, 1, 9, 5, 0);
            Assert.Equal("状態: 動いています（3 時間 12 分 · ポート 8765）", TrayText.StatusLine(TrayColor.Green, "正常に動いています", new TimeSpan(3, 12, 0), at, 8765), "green");
            Assert.Equal("状態: 確かめています（ポート 8765）", TrayText.StatusLine(TrayColor.Yellow, "起動中です", TimeSpan.Zero, at, 8765), "yellow");
            Assert.Equal("状態: 止めています（09:05 から · ポート 8765）", TrayText.StatusLine(TrayColor.Gray, "停止中", TimeSpan.Zero, at, 8765), "gray");
            Assert.Equal("状態: 片付けられないプロセスがあります（ポート 8765）", TrayText.StatusLine(TrayColor.Red, "片付けられないプロセスがあります", TimeSpan.Zero, at, 8765), "red short");
            Assert.Equal("状態: 止まっています（ポート 8765）", TrayText.StatusLine(TrayColor.Red, new string('x', 41), TimeSpan.Zero, at, 8765), "red long");
            Assert.Equal("状態: 止まっています（ポート 8765）", TrayText.StatusLine(TrayColor.Red, null, TimeSpan.Zero, at, 8765), "red none");
        }
        [Test] public static void TrayText_statusLine_withoutAPort() {
            // The tray that could not read its ini controls no server and names no port.
            DateTime at = new DateTime(2026, 11, 1, 9, 5, 0);
            Assert.Equal("状態: 動いています（3 時間 12 分）", TrayText.StatusLine(TrayColor.Green, "正常に動いています", new TimeSpan(3, 12, 0), at, 0), "green");
            Assert.Equal("状態: 確かめています", TrayText.StatusLine(TrayColor.Yellow, "起動中です", TimeSpan.Zero, at, 0), "yellow");
            Assert.Equal("状態: 止めています（09:05 から）", TrayText.StatusLine(TrayColor.Gray, "停止中", TimeSpan.Zero, at, 0), "gray");
            Assert.Equal("状態: 設定ファイルが見つかりません（smoke.ini）", TrayText.StatusLine(TrayColor.Red, "設定ファイルが見つかりません（smoke.ini）", TimeSpan.Zero, at, 0), "red short");
            Assert.Equal("状態: 止まっています", TrayText.StatusLine(TrayColor.Red, null, TimeSpan.Zero, at, 0), "red none");
        }
        [Test] public static void TrayText_exitQuestion() {
            Assert.Equal("サーバーも止まります。JVLinkServer につないでいるプログラムは、データを取れなくなります。終了しますか？",
                TrayText.ExitQuestion(true), "controls the server");
            Assert.Equal("終了しますか？", TrayText.ExitQuestion(false), "controls no server (ini not read)");
        }
        [Test] public static void TrayText_restartLine() {
            Assert.Null(TrayText.RestartLine(null), "no report");
            Assert.Null(TrayText.RestartLine(HealthReport.Unreachable("refused")), "unreachable");
            Assert.Equal("JV-Link の自動再起動 0 回", TrayText.RestartLine(Restarts(0, null)), "zero");
            Assert.Equal("JV-Link の自動再起動 5 回 · 最後（JV-Link COM task timed out during jvrt_open）",
                TrayText.RestartLine(Restarts(5, "post-stream health check for /query/realtime: unhealthy: JV-Link COM task timed out during jvrt_open")), "five");
            string line = TrayText.RestartLine(Restarts(1, new string('y', 80)));
            Assert.True(line.EndsWith("…）"), "long reason is cut: " + line);
        }
        [Test] public static void TrayText_userTextAvoidsInternalWords() {
            string line = TrayText.RestartLine(Restarts(2, "x"));
            Assert.False(line.Contains("作業役"), "restart line: " + line);
        }
        [Test] public static void TrayText_configArg() {
            Assert.Null(TrayText.ConfigArg(new string[0]), "absent");
            Assert.Null(TrayText.ConfigArg(null), "null");
            Assert.Equal(@"C:\x\smoke.ini", TrayText.ConfigArg(new[] { "--config", @"C:\x\smoke.ini" }), "value");
            Assert.Equal(@"a b.ini", TrayText.ConfigArg(new[] { "--other", "--CONFIG", "a b.ini" }), "case, position");
            Assert.Equal("", TrayText.ConfigArg(new[] { "--config" }), "no value");
        }
    }
}
