using System;

namespace JVLinkServerTray.Tests
{
    public static class WatchdogTests
    {
        static TimeSpan S(double sec) { return TimeSpan.FromSeconds(sec); }
        static HealthReport Ok() { return new HealthReport { Kind = HealthKind.Healthy, HttpStatus = 200, Status = "healthy" }; }
        static HealthReport Busy() { return new HealthReport { Kind = HealthKind.Answered, HttpStatus = 503, Status = "starting" }; }
        static HealthReport Down() { return HealthReport.Unreachable("refused"); }
        static Watchdog W() { var w = new Watchdog(new TrayConfig()); w.ServerStarted(S(0)); return w; }

[Test] public static void Watchdog_healthy_isGreen() {
    var d = W().Step(true, Ok(), S(5));
    Assert.Equal(TrayColor.Green, d.Color, "color"); Assert.Equal(WatchAction.None, d.Action, "action");
}
[Test] public static void Watchdog_selfHealBlip_like_1101_neverRestarts() {
    // 11/1 15:00: healthy, then 503 for two polls while upstream rebuilt the worker, then healthy
    var w = W(); w.Step(true, Ok(), S(5));
    var a = w.Step(true, Busy(), S(10)); var b = w.Step(true, Busy(), S(15)); var c = w.Step(true, Ok(), S(20));
    Assert.Equal(TrayColor.Yellow, a.Color, "a"); Assert.Equal(WatchAction.None, a.Action, "a act");
    Assert.Equal(WatchAction.None, b.Action, "b act"); Assert.Equal(TrayColor.Green, c.Color, "c");
}
[Test] public static void Watchdog_503_staysYellow_for9m59s() {
    var w = W(); w.Step(true, Ok(), S(5));
    for (int t = 10; t < 10 + 599; t += 5) Assert.Equal(WatchAction.None, w.Step(true, Busy(), S(t)).Action, "t=" + t);
}
[Test] public static void Watchdog_503_for10min_restartsOnce_thenWaitsForGreen() {
    var w = W(); w.Step(true, Ok(), S(5)); w.Step(true, Busy(), S(10));
    var d = w.Step(true, Busy(), S(610));
    Assert.Equal(WatchAction.StopAndStart, d.Action, "escalate"); Assert.Equal(0, w.Attempt, "attempt untouched");
    w.ServerStarted(S(615)); w.Step(true, Busy(), S(620));
    var late = w.Step(true, Busy(), S(1300));
    Assert.Equal(WatchAction.None, late.Action, "no second escalation");
    Assert.True(late.Reason.Contains("10 分以上戻りません") && late.Reason.Contains("starting"), "reason: " + late.Reason);
    w.Step(true, Ok(), S(1305)); w.Step(true, Busy(), S(1310));
    Assert.Equal(WatchAction.StopAndStart, w.Step(true, Busy(), S(1910)).Action, "again after green");
}
[Test] public static void Watchdog_processExit_isRed_restartsAfter10s() {
    var w = W(); w.Step(true, Ok(), S(5));
    var a = w.Step(false, Down(), S(10)); var b = w.Step(false, Down(), S(15)); var c = w.Step(false, Down(), S(20));
    Assert.Equal(TrayColor.Red, a.Color, "red"); Assert.Equal(WatchAction.None, a.Action, "wait");
    Assert.Equal(WatchAction.None, b.Action, "still waiting"); Assert.Equal(WatchAction.StopAndStart, c.Action, "restart at +10s");
    Assert.Equal(1, w.Attempt, "attempt");
}
[Test] public static void Watchdog_repeatedExits_backOff() {
    var w = W(); double t = 0; int[] want = { 10, 30, 60, 120, 300, 300 };
    foreach (int delay in want) {
        t += 1; w.Step(false, Down(), S(t));                       // failure seen
        Assert.Equal(WatchAction.None, w.Step(false, Down(), S(t + delay - 1)).Action, "before " + delay);
        Assert.Equal(WatchAction.StopAndStart, w.Step(false, Down(), S(t + delay)).Action, "at " + delay);
        t += delay; w.ServerStarted(S(t));
    }
}
[Test] public static void Watchdog_green10min_resetsBackoff() {
    var w = W(); w.Step(false, Down(), S(1)); w.Step(false, Down(), S(11)); w.ServerStarted(S(11));
    Assert.Equal(1, w.Attempt, "after one restart");
    w.Step(true, Ok(), S(20)); w.Step(true, Ok(), S(619));
    Assert.Equal(1, w.Attempt, "9m59s green"); w.Step(true, Ok(), S(620));
    Assert.Equal(0, w.Attempt, "10m green");
}
[Test] public static void Watchdog_unreachable_inGrace_isYellow_then6TimesIsRed() {
    var w = W();                                               // grace 60 s, limit 6
    Assert.Equal(TrayColor.Yellow, w.Step(true, Down(), S(30)).Color, "grace");
    for (int i = 1; i <= 5; i++) {
        var d = w.Step(true, Down(), S(60 + 5 * i));
        Assert.Equal(TrayColor.Yellow, d.Color, "miss " + i); Assert.Equal(WatchAction.None, d.Action, "miss act " + i);
    }
    Assert.Equal(TrayColor.Red, w.Step(true, Down(), S(95)).Color, "6th miss");
    Assert.Equal(WatchAction.StopAndStart, w.Step(true, Down(), S(105)).Action, "after 10 s");
}
[Test] public static void Watchdog_answerResetsUnreachableStreak() {
    var w = W();
    for (int i = 1; i <= 5; i++) w.Step(true, Down(), S(60 + 5 * i));
    w.Step(true, Busy(), S(90));
    for (int i = 1; i <= 5; i++) Assert.Equal(TrayColor.Yellow, w.Step(true, Down(), S(90 + 5 * i)).Color, "miss " + i);
}
[Test] public static void Watchdog_deadHandleButAnswering_doesNotRestart() {
    var w = W(); w.Step(true, Ok(), S(5));
    var a = w.Step(false, Busy(), S(10)); var b = w.Step(false, Ok(), S(15));
    Assert.Equal(TrayColor.Yellow, a.Color, "503 with a dead handle is yellow"); Assert.Equal(WatchAction.None, a.Action, "no restart");
    Assert.Equal(TrayColor.Green, b.Color, "healthy with a dead handle is green");
}
[Test] public static void Watchdog_deadAndUnreachable_skipsGraceAndLimit() {
    var w = W(); var d = w.Step(false, Down(), S(1));          // still inside the 60 s grace
    Assert.Equal(TrayColor.Red, d.Color, "red at once");
    Assert.Equal(WatchAction.StopAndStart, w.Step(false, Down(), S(11)).Action, "restart after 10 s");
}
[Test] public static void Watchdog_userStopped_isGray_evenIfProcessDead() {
    var w = W(); w.UserStopped(); var d = w.Step(false, Down(), S(100));
    Assert.Equal(TrayColor.Gray, d.Color, "gray"); Assert.Equal(WatchAction.None, d.Action, "none");
    w.ServerStarted(S(200)); Assert.Equal(TrayColor.Green, w.Step(true, Ok(), S(205)).Color, "started again");
}
[Test] public static void Watchdog_blocked_isRed_noAction_untilServerStarted() {
    var w = W(); w.Blocked("JVLinkServer.exe が見つかりません（ini の ServerExe）");
    var d = w.Step(false, Down(), S(1000));
    Assert.Equal(TrayColor.Red, d.Color, "red"); Assert.Equal(WatchAction.None, d.Action, "none");
    Assert.True(d.Reason.Contains("見つかりません"), "reason");
    w.ServerStarted(S(1001)); Assert.Equal(TrayColor.Yellow, w.Step(true, Down(), S(1002)).Color, "grace again");
}

        [Test] public static void Watchdog_answerCancelsPendingFailure_alive() {
            var w = W();
            for (int i = 1; i <= 6; i++) w.Step(true, Down(), S(60 + 5 * i));   // 6th miss at 90 = failure
            w.Step(true, Busy(), S(95));
            for (int t = 100; t <= 120; t += 5) w.Step(true, Down(), S(t));
            var d = w.Step(true, Down(), S(125));
            Assert.Equal(TrayColor.Red, d.Color, "red"); Assert.Equal(WatchAction.None, d.Action, "fresh wait");
            Assert.Equal(WatchAction.None, w.Step(true, Down(), S(134)).Action, "before 135");
            Assert.Equal(WatchAction.StopAndStart, w.Step(true, Down(), S(135)).Action, "at 135");
        }
        [Test] public static void Watchdog_answerCancelsPendingFailure_deadHandle() {
            var w = W(); w.Step(true, Ok(), S(5));
            w.Step(false, Down(), S(10));
            Assert.Equal(TrayColor.Yellow, w.Step(false, Busy(), S(15)).Color, "yellow");
            var d = w.Step(false, Down(), S(20));
            Assert.Equal(TrayColor.Red, d.Color, "red"); Assert.Equal(WatchAction.None, d.Action, "new failure waits");
            Assert.Equal(WatchAction.StopAndStart, w.Step(false, Down(), S(30)).Action, "at 30");
        }
        [Test] public static void Watchdog_blockedAfterUserStopped_isRedWithReason() {
            var w = W(); w.UserStopped(); w.Blocked("JVLinkServer.exe が見つかりません（ini の ServerExe）");
            var d = w.Step(false, Down(), S(10));
            Assert.Equal(TrayColor.Red, d.Color, "red"); Assert.Equal(WatchAction.None, d.Action, "none");
            Assert.True(d.Reason.Contains("見つかりません"), "reason");
        }
    }
}
