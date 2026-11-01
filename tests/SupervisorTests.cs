using System;
using System.Collections.Generic;

namespace JVLinkServerTray.Tests
{
    // The OS side of the work items, faked. ListFails makes every process-list call fail the way a WMI error or
    // time-out does in ServerProcess (CountRunning/WatchSupervisor throw, Stop gives Unknown, the count gives -1).
    sealed class FakeOps : IServerOps
    {
        public bool Exe = true;
        public HealthReport HealthNow = HealthReport.Unreachable("refused");
        public bool ListFails;           // every process-list call fails
        public bool UnreadableFails;     // only CountUnreadableSameName fails
        public int Running;              // processes running ServerExe
        public int Unreadable;           // same-name processes whose path cannot be read
        public StopResult? StopAnswer;   // forces Stop's answer (Blocked: a refused kill)
        public bool StopThrows;
        public bool Alive = true;        // the watched supervisor
        public TimeSpan? Age;            // how long the watched supervisor has run
        public bool Watching;
        public int Starts, Stops, StartsWithoutCleanScan;
        bool _clean;                     // the last process-list look found nothing left

        public bool ExeExists() { return Exe; }
        public HealthReport Health(int timeoutMs) { return HealthNow; }
        public int CountRunning()
        {
            _clean = false;
            if (ListFails) throw new TimeoutException("WMI did not answer");
            _clean = Running == 0;
            return Running;
        }
        public bool WatchSupervisor()
        {
            if (ListFails) throw new TimeoutException("WMI did not answer");
            Watching = Running > 0;
            return Watching;
        }
        public bool WatchedAlive() { return !Watching || Alive; }
        public void Unwatch() { Watching = false; }
        public StopResult Stop(int timeoutSeconds)
        {
            Stops++;
            _clean = false;
            if (StopThrows) throw new InvalidOperationException("boom");
            if (StopAnswer != null) return StopAnswer.Value;
            if (ListFails) return StopResult.Unknown;
            Running = 0;
            Watching = false;
            _clean = true;
            return StopResult.Clean;
        }
        public int CountUnreadableSameName()
        {
            if (ListFails || UnreadableFails)
            {
                _clean = false;
                return -1;
            }
            return Unreadable;
        }
        public bool Start()
        {
            if (!_clean || Unreadable != 0) StartsWithoutCleanScan++;
            Starts++;
            _clean = false;
            Running = 2;
            Alive = true;
            Watching = true;
            Age = TimeSpan.Zero;
            return true;
        }
        public string WatchedPid { get { return Watching ? "100" : "?"; } }
        public TimeSpan? WatchedAge { get { return Watching ? Age : null; } }
    }

    sealed class FakeLog : ITrayLog
    {
        public readonly List<string> Lines = new List<string>();
        public int Cleanups;
        public void Write(string message) { Lines.Add(message); }
        public int Cleanup(DateTime today, int keepDays) { Cleanups++; return 0; }
        public int Count(string part)
        {
            int n = 0;
            foreach (string l in Lines) if (l.Contains(part)) n++;
            return n;
        }
        public string All() { return string.Join("\n", Lines.ToArray()); }
    }

    sealed class Rig
    {
        public readonly FakeOps Ops = new FakeOps();
        public readonly FakeLog Log = new FakeLog();
        public readonly Supervisor Sup;
        public TimeSpan Now;
        public DateTime Wall = new DateTime(2026, 11, 1, 9, 0, 0);

        public Rig()
        {
            Sup = new Supervisor(Ops, new TrayConfig(), null, @"C:\test\JVLinkServerTray.ini", @"C:\test\logs", Log,
                delegate { return Now; }, delegate { return Wall; });
        }

        // A poll at t seconds.
        public TrayView At(double t)
        {
            Now = TimeSpan.FromSeconds(t);
            Wall = new DateTime(2026, 11, 1, 9, 0, 0).AddSeconds(t);
            Sup.Poll();
            return Sup.Snapshot();
        }

        public TrayView View { get { return Sup.Snapshot(); } }

        // Adopts a healthy server at t = 0.
        public static Rig Adopted()
        {
            var r = new Rig();
            r.Ops.Running = 2;
            r.Ops.HealthNow = SupervisorTests.Ok();
            r.Sup.Startup();
            return r;
        }
    }

    public static class SupervisorTests
    {
        internal static HealthReport Ok() { return new HealthReport { Kind = HealthKind.Healthy, HttpStatus = 200, Status = "healthy" }; }
        internal static HealthReport Busy() { return new HealthReport { Kind = HealthKind.Answered, HttpStatus = 503, Status = "starting" }; }
        internal static HealthReport Down() { return HealthReport.Unreachable("refused"); }
        static TimeSpan S(double sec) { return TimeSpan.FromSeconds(sec); }

        // ---- I-1: WMI trouble never ends supervision -------------------------------------------

        [Test] public static void Supervisor_wmiFailsAtStartup_noStart_retried_startsOnlyAfterACleanScan() {
            var r = new Rig(); r.Ops.ListFails = true;            // /health unreachable (boot), WMI fails
            r.Sup.Startup();
            var v = r.View;
            Assert.Equal(0, r.Ops.Starts, "no start at startup");
            Assert.Equal(TrayColor.Red, v.Color, "red");
            Assert.False(v.Blocked, "not a manual block");
            Assert.True(v.Reason.StartsWith("プロセスの一覧を取れません"), "reason: " + v.Reason);
            r.At(9); Assert.Equal(0, r.Ops.Stops, "waits 10 s");
            r.At(10); Assert.Equal(1, r.Ops.Stops, "retried after 10 s");
            Assert.Equal(0, r.Ops.Starts, "the retry cannot tell either: no start"); Assert.False(r.View.Blocked, "still retried");
            r.Ops.ListFails = false;                               // WMI is back
            r.At(39); Assert.Equal(1, r.Ops.Stops, "the next retry waits 30 s");
            r.At(40); Assert.Equal(2, r.Ops.Stops, "retried after 30 s"); Assert.Equal(1, r.Ops.Starts, "started");
            Assert.Equal(0, r.Ops.StartsWithoutCleanScan, "started only after a clean scan");
            Assert.Equal(TrayColor.Yellow, r.At(45).Color, "starting");
        }
        [Test] public static void Supervisor_wmiTimesOutDuringStopAndStart_noStart_retriedLater() {
            var r = Rig.Adopted();
            Assert.Equal(TrayColor.Green, r.View.Color, "adopted");
            r.Ops.Alive = false; r.Ops.HealthNow = Down(); r.Ops.ListFails = true;   // the server died, WMI hangs
            r.At(5); r.At(14); Assert.Equal(0, r.Ops.Stops, "waits 10 s");
            var v = r.At(15);
            Assert.Equal(1, r.Ops.Stops, "stop tried"); Assert.Equal(0, r.Ops.Starts, "no start without a clean scan");
            Assert.Equal(TrayColor.Red, v.Color, "red"); Assert.False(v.Blocked, "retried, not blocked");
            Assert.True(v.Reason.StartsWith("プロセスの一覧を取れません"), "reason: " + v.Reason);
            r.At(44); Assert.Equal(1, r.Ops.Stops, "backoff 30 s");
            r.At(45); Assert.Equal(2, r.Ops.Stops, "retried"); Assert.Equal(0, r.Ops.Starts, "still no start");
            r.Ops.ListFails = false;
            r.At(104); Assert.Equal(2, r.Ops.Stops, "backoff 60 s");
            r.At(105); Assert.Equal(3, r.Ops.Stops, "retried"); Assert.Equal(1, r.Ops.Starts, "started");
            Assert.Equal(0, r.Ops.StartsWithoutCleanScan, "only after a clean scan");
        }
        [Test] public static void Supervisor_unreadableCountFails_isRetried_withTheListReason() {
            var r = new Rig(); r.Ops.UnreadableFails = true;       // nothing runs; the same-name count cannot be read
            r.Sup.Startup();
            var v = r.View;
            Assert.Equal(0, r.Ops.Starts, "no start"); Assert.False(v.Blocked, "retried, not blocked");
            Assert.True(v.Reason.StartsWith("プロセスの一覧を取れません"), "reason: " + v.Reason);
            Assert.False(v.Reason.Contains("パスを読めない"), "not the unreadable-path reason: " + v.Reason);
            r.Ops.UnreadableFails = false;
            r.At(10);
            Assert.Equal(1, r.Ops.Starts, "started on the retry"); Assert.Equal(0, r.Ops.StartsWithoutCleanScan, "after a clean scan");
        }
        [Test] public static void Supervisor_successfulScanSeesUnreadable_blocks_noRetryLoop() {
            var r = new Rig(); r.Ops.Unreadable = 1;
            r.Sup.Startup();
            var v = r.View;
            Assert.True(v.Blocked, "manual block"); Assert.Equal(TrayColor.Red, v.Color, "red");
            Assert.Equal("片付けられないプロセスがあります（パスを読めない）", v.Reason, "reason");
            for (int t = 5; t <= 3600; t += 5) r.At(t);
            Assert.Equal(0, r.Ops.Stops, "no retry"); Assert.Equal(0, r.Ops.Starts, "no start"); Assert.True(r.View.Blocked, "still blocked");
        }
        [Test] public static void Supervisor_refusedKill_blocks_noRetryLoop() {
            var r = Rig.Adopted();
            r.Ops.Alive = false; r.Ops.HealthNow = Down(); r.Ops.StopAnswer = StopResult.Blocked;
            r.At(5); r.At(15);
            Assert.Equal(1, r.Ops.Stops, "stop tried"); Assert.True(r.View.Blocked, "manual block");
            Assert.Equal("片付けられないプロセスがあります", r.View.Reason, "reason");
            for (int t = 20; t <= 3600; t += 5) r.At(t);
            Assert.Equal(1, r.Ops.Stops, "no retry"); Assert.Equal(0, r.Ops.Starts, "no start");
        }
        [Test] public static void Supervisor_missingExe_blocks_noRetryLoop() {
            var r = new Rig(); r.Ops.Exe = false;
            r.Sup.Startup();
            Assert.True(r.View.Blocked, "manual block");
            Assert.Equal("JVLinkServer.exe が見つかりません（ini の ServerExe）", r.View.Reason, "reason");
            for (int t = 5; t <= 3600; t += 5) r.At(t);
            Assert.Equal(0, r.Ops.Stops + r.Ops.Starts, "nothing tried");
        }
        [Test] public static void Supervisor_answersWhileUnknown_adoptsWithoutRestarting() {
            var r = new Rig(); r.Ops.ListFails = true;
            r.Sup.Startup();
            r.Ops.ListFails = false; r.Ops.Running = 2; r.Ops.HealthNow = Ok();   // the server answers after all
            var v = r.At(5);
            Assert.Equal(TrayColor.Green, v.Color, "green"); Assert.True(r.Ops.Watching, "watches the supervisor");
            for (int t = 10; t <= 600; t += 5) r.At(t);
            Assert.Equal(0, r.Ops.Stops + r.Ops.Starts, "never restarted");
            Assert.True(r.Log.Count("引き継ぎ: ") >= 1, "adoption logged: " + r.Log.All());
        }
        [Test] public static void Supervisor_userStopWithListFailure_staysGray_neverStarts() {
            var r = Rig.Adopted();
            r.Ops.ListFails = true;
            r.Now = S(5); r.Sup.StopFromMenu();
            var v = r.View;
            Assert.Equal(TrayColor.Gray, v.Color, "gray"); Assert.True(v.Stopped, "stopped");
            r.Ops.HealthNow = Down(); r.Ops.Alive = false;
            for (int t = 10; t <= 3600; t += 5) r.At(t);
            Assert.Equal(0, r.Ops.Starts, "the user's stop is kept"); Assert.Equal(1, r.Ops.Stops, "one stop");
        }
        [Test] public static void Supervisor_exitWithListFailure_stillExits() {
            var r = Rig.Adopted(); r.Ops.ListFails = true;
            r.Sup.Exit();
            Assert.Equal(0, r.Ops.Starts, "no start");
            Assert.True(r.Log.Count("止まったか確かめられません") == 1, "could not tell, logged: " + r.Log.All());
            Assert.True(r.Log.Count("終了: トレイを終わります") == 1, "logged: " + r.Log.All());
        }

        // ---- I-2: SessionEnding only holds restarts off for a while ------------------------------

        [Test] public static void Supervisor_holdRestarts_postponesUntilTheDeadline_thenCarriesOn() {
            var r = Rig.Adopted();
            r.Sup.HoldRestarts(S(65));                             // SessionEnding at t = 5
            r.Ops.Alive = false; r.Ops.HealthNow = Down();
            double firstStop = -1;
            for (int t = 10; t <= 300 && firstStop < 0; t += 5) { r.At(t); if (r.Ops.Stops > 0) firstStop = t; }
            Assert.Equal(65.0, firstStop, "restarted at the first poll after the hold");
            Assert.Equal(1, r.Ops.Starts, "started");
            Assert.Equal(1, r.Sup.Snapshot().Color == TrayColor.Yellow ? 1 : 0, "starting");
        }

        // ---- small items -------------------------------------------------------------------

        [Test] public static void Supervisor_startFromMenu_whenNotStoppedOrBlocked_doesNothing() {
            var r = Rig.Adopted();
            r.Sup.StartFromMenu();                                 // a second 起動する queued behind the first
            Assert.Equal(0, r.Ops.Stops + r.Ops.Starts, "no second restart");
            Assert.Equal(TrayColor.Green, r.View.Color, "still green");
        }
        [Test] public static void Supervisor_startFromMenu_fromGrayOrBlocked_restarts() {
            var r = Rig.Adopted();
            r.Sup.StopFromMenu(); Assert.True(r.View.Stopped, "gray");
            r.Sup.StartFromMenu();
            Assert.Equal(1, r.Ops.Starts, "started from gray");
            var b = new Rig(); b.Ops.Unreadable = 1; b.Sup.Startup(); Assert.True(b.View.Blocked, "blocked");
            b.Ops.Unreadable = 0; b.Sup.StartFromMenu();
            Assert.Equal(1, b.Ops.Starts, "started from blocked");
        }
        [Test] public static void Supervisor_runningTime_isTheSupervisorsAge_andSurvivesBlips() {
            var r = new Rig(); r.Ops.Running = 2; r.Ops.HealthNow = Ok(); r.Ops.Age = TimeSpan.FromHours(3);
            r.Now = S(100); r.Sup.Startup();
            TimeSpan since = S(100) - TimeSpan.FromHours(3);
            Assert.Equal((TimeSpan?)since, r.View.RunningSince, "from the supervisor's start time");
            r.Ops.HealthNow = Busy(); r.At(105); r.At(110);
            r.Ops.HealthNow = Ok(); var v = r.At(115);
            Assert.Equal(TrayColor.Green, v.Color, "green again"); Assert.Equal((TimeSpan?)since, v.RunningSince, "a 503 blip does not reset it");
            r.Ops.Alive = false; r.Ops.HealthNow = Down();
            r.At(120); r.At(130);                                  // restarted at 130
            Assert.Equal(1, r.Ops.Starts, "restarted");
            Assert.Equal((TimeSpan?)S(130), r.View.RunningSince, "a new server starts from zero");
        }
        [Test] public static void Supervisor_runningTime_httpOnly_fallsBackToTheStartOfWatching() {
            var r = new Rig(); r.Ops.Running = 0; r.Ops.HealthNow = Ok();   // answers, no supervisor process found
            r.Now = S(42); r.Sup.Startup();
            Assert.Equal((TimeSpan?)S(42), r.View.RunningSince, "start of watching");
        }
        [Test] public static void Supervisor_cleansOldLogsWhenTheDateChanges() {
            var r = Rig.Adopted();
            Assert.Equal(1, r.Log.Cleanups, "at startup");
            r.At(5); r.At(10);
            Assert.Equal(1, r.Log.Cleanups, "same day");
            r.At(15 * 3600);                                       // 09:00 + 15 h = next day
            Assert.Equal(2, r.Log.Cleanups, "the date changed");
            r.At(15 * 3600 + 5);
            Assert.Equal(2, r.Log.Cleanups, "once per day");
        }
        [Test] public static void Supervisor_exit_finishesEvenWhenStopThrows() {
            var r = Rig.Adopted(); r.Ops.StopThrows = true;
            try { r.Sup.Exit(); } catch (InvalidOperationException) { }   // TrayApp logs it; WorkGate finishes anyway
            Assert.True(r.Log.Count("終了: トレイを終わります") == 1, "the end is logged: " + r.Log.All());
        }
        [Test] public static void Supervisor_viewCarriesThePort() {
            var r = Rig.Adopted();
            Assert.Equal(8765, r.View.Port, "port");
            var c = new Supervisor(null, new TrayConfig(), "設定ファイルが見つかりません（x）", "x", "y", new FakeLog(),
                delegate { return TimeSpan.Zero; }, delegate { return DateTime.Now; });
            c.Startup();
            Assert.Equal(0, c.Snapshot().Port, "no port when the ini could not be read");
            Assert.False(c.Snapshot().Controllable, "controls no server");
        }
    }
}
