using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace JVLinkServerTray.Tests
{
    // These tests start bin\FakeJVLinkServer.exe on ports 18765-18773 only. They never touch the real server.
    public static class OsTests
    {
        static string Fake()
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Assembly.GetExecutingAssembly().Location)), "FakeJVLinkServer.exe");
        }

        // Kills every process running the fake EXE (matched by full path), then waits until none is left.
        static void Sweep()
        {
            var sp = new ServerProcess(Fake(), "");
            foreach (ProcInfo pi in sp.FindAll())
            {
                try
                {
                    using (Process p = Process.GetProcessById(pi.Pid))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception) { }
            }
            WaitFor(() => sp.FindAll().Count == 0, 5000);
        }

        static string TempDir()
        {
            return Path.Combine(Path.GetTempPath(), "jvtray-test-" + Guid.NewGuid().ToString("N"));
        }

        static string ReadLogs(string dir)
        {
            var sb = new StringBuilder();
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*.log")) sb.Append(File.ReadAllText(f));
            return sb.ToString();
        }

        // A raw TCP connect (no request sent), bounded to 1 s.
        static bool CanConnect(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1000)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool WaitFor(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (cond()) return true;
                if (sw.ElapsedMilliseconds >= ms) return cond();
                Thread.Sleep(150);
            }
        }

        [Test] public static void Os_guard_neverTouchesTheRealServer() {
            Assert.False(Fake().Equals(@"C:\JVLinkServer\JVLinkServer.exe", StringComparison.OrdinalIgnoreCase), "exe");
        }
        [Test] public static void Os_start_noWindow_healthAnswers() {
            try {
                var p = new ServerProcess(Fake(), "--port 18765").Start();
                Assert.True(WaitFor(() => new HealthClient(18765).Get(1000).Kind == HealthKind.Healthy, 5000), "healthy");
                p.Refresh(); Assert.Equal(IntPtr.Zero, p.MainWindowHandle, "no window");  // over SSH this is always 0; Task 6 smoke (1) is the real proof
            } finally { Sweep(); }
        }
        [Test] public static void Os_findAll_seesSupervisorAndWorker() {
            try {
                var sp = new ServerProcess(Fake(), "--port 18766"); var p = sp.Start();
                Assert.True(WaitFor(() => sp.FindAll().Count == 2, 5000), "two processes");
                Assert.Equal(p.Id, sp.FindSupervisor().Id, "supervisor");
            } finally { Sweep(); }
        }
        [Test] public static void Os_stop_viaShutdown_leavesNothing() {
            try {
                var sp = new ServerProcess(Fake(), "--port 18767"); var p = sp.Start();
                Assert.True(WaitFor(() => sp.FindAll().Count == 2, 5000), "two processes");
                var dir = TempDir();
                var sw = Stopwatch.StartNew();
                Assert.Equal(StopResult.Clean, sp.Stop(new HealthClient(18767), p, 20, new TrayLog(dir)), "clean");
                Assert.Equal(0, sp.FindAll().Count, "nothing left"); Assert.True(sw.Elapsed.TotalSeconds < 10, "fast");
                var log = ReadLogs(dir);
                Assert.True(log.Contains("停止 2/4: 見張り役（pid " + p.Id + "）が終わりました"), "graceful exit logged");
                Assert.False(log.Contains("強制終了"), "no force kill");
            } finally { Sweep(); }
        }
        [Test] public static void Os_stop_ignoredShutdown_forceKills() {
            try {
                var sp = new ServerProcess(Fake(), "--port 18768 --ignore-shutdown"); var p = sp.Start();
                Assert.True(WaitFor(() => sp.FindAll().Count == 2, 5000), "two processes");
                var dir = TempDir();
                Assert.Equal(StopResult.Clean, sp.Stop(new HealthClient(18768), p, 3, new TrayLog(dir)), "clean");
                Assert.Equal(0, sp.FindAll().Count, "nothing left");
                var log = ReadLogs(dir);
                Assert.True(log.Contains("停止 3/4: 残っている 2 個を強制終了します"), "force kill logged");
                Assert.True(log.Contains("停止: pid " + p.Id + " を強制終了しました"), "supervisor killed");
            } finally { Sweep(); }
        }
        [Test] public static void Os_stop_withoutSupervisorHandle_leavesNothing() {
            try {
                var sp = new ServerProcess(Fake(), "--port 18773"); sp.Start();
                Assert.True(WaitFor(() => sp.FindAll().Count == 2, 5000), "two processes");
                Assert.Equal(0, sp.CountUnreadableSameName(), "own processes have readable paths");
                var dir = TempDir();
                Assert.Equal(StopResult.Clean, sp.Stop(new HealthClient(18773), null, 20, new TrayLog(dir)), "clean");
                Assert.Equal(0, sp.FindAll().Count, "nothing left");
                Assert.True(ReadLogs(dir).Contains("停止 2/4: 見張り役のハンドルがないので"), "no-handle path logged");
            } finally { Sweep(); }
        }
        // Spec 4.5: a same-name process whose path cannot be read (another user's / elevated) is never killed,
        // but it may hold the port, so Stop must not report Clean. Such a process cannot be produced in tests.
        [Test] public static void Os_decideStop_cleanOnlyWhenNothingIsLeft() {
            Assert.Equal(StopResult.Clean, ServerProcess.DecideStop(0, 0), "0/0");
            Assert.Equal(StopResult.Blocked, ServerProcess.DecideStop(1, 0), "readable left");
            Assert.Equal(StopResult.Blocked, ServerProcess.DecideStop(0, 1), "unreadable same name");
            Assert.Equal(StopResult.Unknown, ServerProcess.DecideStop(-1, 0), "readable unknown");
            Assert.Equal(StopResult.Unknown, ServerProcess.DecideStop(0, -1), "unreadable unknown");
        }

        // ---- the process list (WMI) is bounded; trouble with it means "could not tell" -----------

        static int Count(string text, string part)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
            return n;
        }

        [Test] public static void Os_boundedCall_timesOut_andLeavesOneLateCallAtATime() {
            var b = new BoundedCall(300);
            var gate = new ManualResetEvent(false);
            var sw = Stopwatch.StartNew();
            try { b.Run(delegate { gate.WaitOne(5000); }); throw new Exception("TimeoutException expected"); }
            catch (TimeoutException) { }
            Assert.True(sw.ElapsedMilliseconds >= 250 && sw.ElapsedMilliseconds < 2000, "bounded: " + sw.ElapsedMilliseconds + " ms");
            bool ran = false;
            sw.Restart();
            try { b.Run(delegate { ran = true; }); throw new Exception("TimeoutException expected while the late call runs"); }
            catch (TimeoutException) { }
            Assert.False(ran, "no second call while the late one runs"); Assert.True(sw.ElapsedMilliseconds < 200, "fails at once");
            gate.Set();
            Assert.True(WaitFor(() => { try { b.Run(delegate { ran = true; }); return true; } catch (TimeoutException) { return false; } }, 3000), "works again");
            Assert.True(ran, "ran");
        }
        [Test] public static void Os_boundedCall_passesTheCallsExceptionOn() {
            var b = new BoundedCall(1000);
            try { b.Run(delegate { throw new InvalidOperationException("wmi"); }); throw new Exception("expected the call's exception"); }
            catch (InvalidOperationException e) { Assert.Equal("wmi", e.Message, "message"); }
        }
        [Test] public static void Os_stop_listHangs_isUnknown_bounded_loggedOncePerStreak() {
            var dir = TempDir(); var log = new TrayLog(dir);
            var sp = new ServerProcess(Fake(), "", log, delegate(List<ProcInfo> r, List<int> u) { Thread.Sleep(1500); }, 200);
            var sw = Stopwatch.StartNew();
            Assert.Equal(StopResult.Unknown, sp.Stop(null, null, 2, log), "could not tell");
            Assert.True(sw.Elapsed.TotalSeconds < 12, "bounded: " + sw.Elapsed.TotalSeconds + " s");
            string text = ReadLogs(dir);
            Assert.Equal(1, Count(text, "プロセスの一覧を取れません（"), "logged once per streak: " + text);
        }
        [Test] public static void Os_stop_listThrows_isUnknown() {
            var log = new TrayLog(TempDir());
            var sp = new ServerProcess(Fake(), "", log, delegate(List<ProcInfo> r, List<int> u) { throw new InvalidOperationException("WMI failed"); }, 1000);
            Assert.Equal(StopResult.Unknown, sp.Stop(null, null, 0, log), "could not tell");
        }
        [Test] public static void Os_stop_listSeesUnreadable_isBlocked_evenIfLaterScansFail() {
            var log = new TrayLog(TempDir());
            var always = new ServerProcess(Fake(), "", log, delegate(List<ProcInfo> r, List<int> u) { u.Add(4242); }, 1000);
            Assert.Equal(StopResult.Blocked, always.Stop(null, null, 0, log), "unreadable seen");
            int calls = 0;
            var once = new ServerProcess(Fake(), "", log, delegate(List<ProcInfo> r, List<int> u) {
                if (++calls > 1) throw new InvalidOperationException("WMI failed");
                u.Add(4242);
            }, 1000);
            Assert.Equal(StopResult.Blocked, once.Stop(null, null, 0, log), "seen by a successful scan, then the list failed");
            var none = new ServerProcess(Fake(), "", log, delegate(List<ProcInfo> r, List<int> u) { }, 1000);
            Assert.Equal(StopResult.Clean, none.Stop(null, null, 0, log), "nothing left");
        }
        [Test] public static void Os_stop_slowList_looksOnceMoreAfterAKill() {
            // A list slower than the kill wait must not end Stop right after a kill (that read as Blocked).
            int calls = 0;
            var log = new TrayLog(TempDir());
            var sp = new ServerProcess(Fake(), "", log, delegate(List<ProcInfo> r, List<int> u) {
                Thread.Sleep(400);
                if (++calls == 2) { var pi = new ProcInfo(); pi.Pid = 999999; pi.CommandLine = "x --mode worker"; r.Add(pi); }
            }, 5000);
            sp.KillWaitMs = 200;
            Assert.Equal(StopResult.Clean, sp.Stop(null, null, 0, log), "looked again after the kill");
            Assert.Equal(3, calls, "lists");
        }
        [Test] public static void Os_countUnreadable_retriesUntilTheListComes() {
            int calls = 0;
            var sp = new ServerProcess(Fake(), "", null, delegate(List<ProcInfo> r, List<int> u) {
                if (++calls < 3) throw new InvalidOperationException("WMI failed");
                u.Add(7);
            }, 1000);
            Assert.Equal(1, sp.CountUnreadableSameName(), "retried until the list came");
            Assert.Equal(3, calls, "calls");
        }
        [Test] public static void Os_countUnreadable_givesUpAfterAFewSeconds() {
            var sp = new ServerProcess(Fake(), "", null, delegate(List<ProcInfo> r, List<int> u) { throw new InvalidOperationException("WMI failed"); }, 1000);
            var sw = Stopwatch.StartNew();
            Assert.Equal(-1, sp.CountUnreadableSameName(), "could not tell");
            Assert.True(sw.Elapsed.TotalSeconds >= 3 && sw.Elapsed.TotalSeconds < 10, "retried for a few seconds: " + sw.Elapsed.TotalSeconds + " s");
        }

        [Test] public static void Os_health_hang_timesOut() {
            try {
                new ServerProcess(Fake(), "--port 18769 --hang-health").Start();
                Assert.True(WaitFor(() => CanConnect(18769), 5000), "port open");
                var sw = Stopwatch.StartNew(); var h = new HealthClient(18769).Get(2000);
                Assert.Equal(HealthKind.Unreachable, h.Kind, "kind");
                Assert.True(sw.Elapsed.TotalSeconds >= 1.5, "waited for the timeout"); Assert.True(sw.Elapsed.TotalSeconds < 4, "bounded");
            } finally { Sweep(); }
        }
        [Test] public static void Os_health_503_isAnswered() {
            try {
                new ServerProcess(Fake(), "--port 18770 --answer 503").Start();
                Assert.True(WaitFor(() => new HealthClient(18770).Get(1000).Kind == HealthKind.Answered, 5000), "answered");
                Assert.Equal("starting", new HealthClient(18770).Get(1000).Status, "status");
            } finally { Sweep(); }
        }
        [Test] public static void Os_health_nothingListening_isUnreachable() {
            Assert.Equal(HealthKind.Unreachable, new HealthClient(18771).Get(1000).Kind, "kind");
        }
        [Test] public static void Os_missingExe_detected() {
            Assert.False(new ServerProcess(@"C:\no\such\JVLinkServer.exe", "").ExeExists(), "missing");
        }
        [Test] public static void Os_trayLog_writesAndCleansUp() {
            var dir = Path.Combine(Path.GetTempPath(), "jvtray-test-" + Guid.NewGuid().ToString("N"));
            var log = new TrayLog(dir); log.Write("hello");
            var today = Path.Combine(dir, "JVLinkServerTray_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            Assert.True(File.ReadAllText(today).Contains("hello"), "written");
            File.WriteAllText(Path.Combine(dir, "JVLinkServerTray_20200101.log"), "old");
            Assert.Equal(1, log.Cleanup(DateTime.Now, 14), "deleted"); Assert.True(File.Exists(today), "kept");
        }
        [Test] public static void Os_trayLog_badDir_doesNotThrow() {
            var file = Path.GetTempFileName();
            new TrayLog(Path.Combine(file, "sub")).Write("x");   // a file stands where the folder should be
        }
        [Test] public static void Os_singleInstance_secondAcquireFails() {
            var first = SingleInstance.TryAcquire(18772);
            Assert.NotNull(first, "first");
            Mutex second = null;
            var t = new Thread(() => { second = SingleInstance.TryAcquire(18772); }); t.Start(); t.Join();
            Assert.Null(second, "second");
            first.ReleaseMutex(); first.Dispose();
        }
    }
}
