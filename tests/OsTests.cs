using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace JVLinkServerTray.Tests
{
    // These tests start bin\FakeJVLinkServer.exe on ports 18765-18772 only. They never touch the real server.
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
                WaitFor(() => sp.FindAll().Count == 2, 5000);
                var sw = Stopwatch.StartNew();
                Assert.Equal(StopResult.Clean, sp.Stop(new HealthClient(18767), p, 20, new TrayLog(Path.GetTempPath())), "clean");
                Assert.Equal(0, sp.FindAll().Count, "nothing left"); Assert.True(sw.Elapsed.TotalSeconds < 10, "fast");
            } finally { Sweep(); }
        }
        [Test] public static void Os_stop_ignoredShutdown_forceKills() {
            try {
                var sp = new ServerProcess(Fake(), "--port 18768 --ignore-shutdown"); var p = sp.Start();
                WaitFor(() => sp.FindAll().Count == 2, 5000);
                Assert.Equal(StopResult.Clean, sp.Stop(new HealthClient(18768), p, 3, new TrayLog(Path.GetTempPath())), "clean");
                Assert.Equal(0, sp.FindAll().Count, "nothing left");
            } finally { Sweep(); }
        }
        [Test] public static void Os_health_hang_timesOut() {
            try {
                new ServerProcess(Fake(), "--port 18769 --hang-health").Start(); Thread.Sleep(1000);
                var sw = Stopwatch.StartNew(); var h = new HealthClient(18769).Get(2000);
                Assert.Equal(HealthKind.Unreachable, h.Kind, "kind"); Assert.True(sw.Elapsed.TotalSeconds < 4, "bounded");
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
