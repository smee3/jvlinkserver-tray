using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;

namespace JVLinkServerTray
{
    public sealed class ProcInfo
    {
        public int Pid;
        public string CommandLine;
    }

    // Starts, finds and stops the server. Processes are matched strictly by the full path of ServerExe.
    // Every process list (WMI) is bounded: each step of the enumeration by WmiTimeout, the whole query by
    // ScanLimitMs on a background thread (the connect phase has no time-out of its own). A list that fails
    // or times out is written to the record once per streak, and again when lists work again.
    public sealed class ServerProcess
    {
        const int ScanLimitMs = 15000;
        const int UnreadableRetryMs = 5000;
        static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(10);

        readonly string _exePath;
        readonly string _args;
        readonly TrayLog _log;                                // may be null
        readonly Action<List<ProcInfo>, List<int>> _scan;     // the WMI query, or a test's stand-in
        readonly int _scanLimitMs;
        readonly BoundedCall _bound;
        bool _listFailing;

        // Stop's last step waits this long for killed processes to go (tests shorten it).
        internal int KillWaitMs = 5000;

        public ServerProcess(string exePath, string args) : this(exePath, args, null) { }

        public ServerProcess(string exePath, string args, TrayLog log) : this(exePath, args, log, null, ScanLimitMs) { }

        // Tests: scan stands in for the WMI query (it fills the readable and unreadable lists).
        internal ServerProcess(string exePath, string args, TrayLog log, Action<List<ProcInfo>, List<int>> scan, int scanLimitMs)
        {
            _exePath = exePath;
            _args = args ?? "";
            _log = log;
            _scan = scan != null ? scan : new Action<List<ProcInfo>, List<int>>(WmiScan);
            _scanLimitMs = scanLimitMs;
            _bound = new BoundedCall(scanLimitMs);
        }

        public bool ExeExists()
        {
            try { return File.Exists(_exePath); }
            catch (Exception) { return false; }
        }

        // No window, no redirected standard streams, working directory = the EXE's folder.
        public Process Start()
        {
            string full = Path.GetFullPath(_exePath);
            var psi = new ProcessStartInfo(full, _args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(full);
            return Process.Start(psi);
        }

        // Every running process whose ExecutablePath is ServerExe (supervisor and worker alike).
        // Throws when the list fails or times out.
        public List<ProcInfo> FindAll()
        {
            var result = new List<ProcInfo>();
            Scan(result, null, null);
            return result;
        }

        // How many processes have ServerExe's file name but a path WMI cannot read (another user's or
        // elevated). They are never killed, but they may hold the port. A failed list is retried for a few
        // seconds; -1 when it never worked ("could not tell"). Never throws.
        public int CountUnreadableSameName()
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var unreadable = new List<int>();
                    Scan(new List<ProcInfo>(), unreadable, null);
                    return unreadable.Count;
                }
                catch (Exception)
                {
                }
                if (sw.ElapsedMilliseconds >= UnreadableRetryMs) return -1;
                Thread.Sleep(500);
            }
        }

        // The end of Stop, from the last list: Clean only when nothing with ServerExe's path is left and no
        // same-name process with an unreadable path exists. A negative count means the list failed: Unknown.
        public static StopResult DecideStop(int readableLeft, int unreadableSameName)
        {
            if (readableLeft < 0 || unreadableSameName < 0) return StopResult.Unknown;
            return readableLeft == 0 && unreadableSameName == 0 ? StopResult.Clean : StopResult.Blocked;
        }

        // One bounded list. Throws when it fails or times out. The lists are filled only when it worked
        // (a late query never writes into them).
        void Scan(List<ProcInfo> readable, List<int> unreadable, TrayLog fallbackLog)
        {
            var r = new List<ProcInfo>();
            var u = new List<int>();
            try
            {
                _bound.Run(delegate { _scan(r, u); });
            }
            catch (Exception e)
            {
                if (!_listFailing)
                    Log(_log ?? fallbackLog, "プロセスの一覧を取れません（" + e.Message + "）。取れるようになるまで、くり返しては書きません");
                _listFailing = true;
                throw;
            }
            if (_listFailing) Log(_log ?? fallbackLog, "プロセスの一覧をまた取れるようになりました");
            _listFailing = false;
            readable.AddRange(r);
            if (unreadable != null) unreadable.AddRange(u);
        }

        // One WMI query by file name. Rows whose path equals ServerExe go to readable; rows whose path cannot be
        // read or evaluated go to unreadable. Other paths are ignored. Throws when WMI fails or times out.
        void WmiScan(List<ProcInfo> readable, List<int> unreadable)
        {
            string full = Path.GetFullPath(_exePath);
            string name = Path.GetFileName(full).Replace("\\", "\\\\").Replace("'", "\\'");
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '" + name + "'"))
            {
                var options = new EnumerationOptions();
                options.Timeout = WmiTimeout;
                options.ReturnImmediately = true;
                options.Rewindable = false;
                searcher.Options = options;
                using (ManagementObjectCollection found = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in found)
                    {
                        using (mo)
                        {
                            int pid;
                            try { pid = Convert.ToInt32(mo["ProcessId"]); }
                            catch (Exception) { continue; }
                            try
                            {
                                string path = mo["ExecutablePath"] as string;
                                if (string.IsNullOrEmpty(path))
                                {
                                    unreadable.Add(pid);
                                    continue;
                                }
                                if (!string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase)) continue;
                                var pi = new ProcInfo();
                                pi.Pid = pid;
                                pi.CommandLine = mo["CommandLine"] as string;
                                readable.Add(pi);
                            }
                            catch (Exception)
                            {
                                unreadable.Add(pid);
                            }
                        }
                    }
                }
            }
        }

        // The supervisor (a ServerExe process that is not "--mode worker"), or null.
        public Process FindSupervisor()
        {
            foreach (ProcInfo pi in FindAll())
            {
                if (ServerArgs.IsWorkerCommandLine(pi.CommandLine)) continue;
                Process p = Open(pi.Pid);
                if (p != null) return p;
            }
            return null;
        }

        // Design 4.5: POST /shutdown, wait, then kill whatever still runs ServerExe. Never throws.
        // Clean: a list that worked shows nothing left. Blocked: something is really left (a refused kill, a
        // same-name process with an unreadable path seen by a list that worked, a process that will not go).
        // Unknown: the last list failed or timed out, so the tray cannot tell.
        public StopResult Stop(HealthClient client, Process supervisor, int timeoutSeconds, TrayLog log)
        {
            try
            {
                return StopCore(client, supervisor, timeoutSeconds, log);
            }
            catch (Exception e)
            {
                Log(log, "停止: 思わぬエラーで止めきれませんでした（" + e.Message + "）");
                return StopResult.Unknown;
            }
        }

        StopResult StopCore(HealthClient client, Process supervisor, int timeoutSeconds, TrayLog log)
        {
            if (timeoutSeconds < 0) timeoutSeconds = 0;
            if (timeoutSeconds > 3600) timeoutSeconds = 3600;

            // 1. Ask the server to stop.
            bool answered = client != null && client.Shutdown(3000);
            Log(log, "停止 1/4: POST /shutdown を送りました（" + (answered ? "答えあり" : "答えなし") + "）");

            // 2. Wait for the supervisor (or, without a handle, for every ServerExe process) to end.
            List<ProcInfo> left;
            List<int> unreadable;
            bool sawUnreadable = false, refused = false;
            if (supervisor != null)
            {
                string pid = PidText(supervisor);
                bool exited = WaitExit(supervisor, timeoutSeconds * 1000);
                Log(log, exited
                    ? "停止 2/4: 見張り役（pid " + pid + "）が終わりました"
                    : "停止 2/4: 見張り役（pid " + pid + "）が " + timeoutSeconds + " 秒たっても終わりません");
            }
            else
            {
                Log(log, "停止 2/4: 見張り役のハンドルがないので、プロセスがなくなるのを最長 " + timeoutSeconds + " 秒待ちます");
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    bool listed = TryScan(log, out left, out unreadable);
                    if (listed && unreadable.Count > 0) sawUnreadable = true;
                    if (listed && left.Count == 0) break;
                    if (sw.Elapsed.TotalSeconds >= timeoutSeconds) break;
                    Thread.Sleep(1000);
                }
            }

            // 3. Kill whatever is left (supervisors first, so none restarts a worker); 4. wait up to KillWaitMs,
            // and always look once more after a kill (a slow list must not end the wait before it).
            // Same-name processes with an unreadable path are never killed, but they keep the result Blocked.
            var tried = new HashSet<int>();
            var wait = Stopwatch.StartNew();
            bool first = true, warnedUnreadable = false, ok;
            while (true)
            {
                ok = TryScan(log, out left, out unreadable);
                if (ok && unreadable.Count > 0) sawUnreadable = true;
                if (DecideStop(ok ? left.Count : -1, ok ? unreadable.Count : -1) == StopResult.Clean)
                {
                    if (first) Log(log, "停止 3/4: 残っているプロセスはありません");
                    Log(log, "停止 4/4: きれいに止まりました");
                    return StopResult.Clean;
                }
                if (ok && unreadable.Count > 0 && !warnedUnreadable)
                {
                    Log(log, "停止 3/4: 同じ名前でパスを読めないプロセスがあります（pid " + string.Join(", ", ToText(unreadable)) + "）。強制終了しません");
                    warnedUnreadable = true;
                }
                bool killed = false;
                if (ok)
                {
                    var fresh = new List<ProcInfo>();
                    foreach (ProcInfo pi in left) if (!tried.Contains(pi.Pid)) fresh.Add(pi);
                    if (fresh.Count > 0)
                    {
                        Log(log, "停止 3/4: 残っている " + fresh.Count + " 個を強制終了します（pid " + PidList(fresh) + "）");
                        fresh.Sort(delegate(ProcInfo a, ProcInfo b)
                        {
                            return ServerArgs.IsWorkerCommandLine(a.CommandLine).CompareTo(ServerArgs.IsWorkerCommandLine(b.CommandLine));
                        });
                        foreach (ProcInfo pi in fresh)
                        {
                            tried.Add(pi.Pid);
                            if (KillRefused(pi.Pid, log)) refused = true;
                        }
                        killed = true;
                    }
                }
                first = false;
                long waited = wait.ElapsedMilliseconds;
                if (waited >= KillWaitMs && (!killed || waited >= KillWaitMs + _scanLimitMs)) break;
                Thread.Sleep(500);
            }
            StopResult result = DecideStop(ok ? left.Count : -1, ok ? unreadable.Count : -1);
            if (result == StopResult.Unknown && (refused || sawUnreadable))
            {
                Log(log, "停止 4/4: 片付けられないプロセスがあります（" + (refused ? "強制終了を拒まれました" : "パスを読めない同じ名前のプロセスがありました")
                    + "。そのあとプロセスの一覧を取れません）");
                return StopResult.Blocked;
            }
            if (result == StopResult.Unknown)
            {
                Log(log, "停止 4/4: プロセスの一覧を取れないので、止まったか確かめられません");
                return StopResult.Unknown;
            }
            Log(log, "停止 4/4: 片付けられないプロセスがあります（" + Describe(left, unreadable) + "）");
            return StopResult.Blocked;
        }

        // A failed list is written to the record by Scan, once per streak.
        bool TryScan(TrayLog log, out List<ProcInfo> readable, out List<int> unreadable)
        {
            readable = new List<ProcInfo>();
            unreadable = new List<int>();
            try
            {
                Scan(readable, unreadable, log);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string Describe(List<ProcInfo> left, List<int> unreadable)
        {
            var parts = new List<string>();
            if (left.Count > 0) parts.Add("pid " + PidList(left));
            if (unreadable.Count > 0) parts.Add("パスを読めない pid " + string.Join(", ", ToText(unreadable)));
            return string.Join("、", parts.ToArray());
        }

        static string[] ToText(List<int> pids)
        {
            var parts = new string[pids.Count];
            for (int i = 0; i < pids.Count; i++) parts[i] = pids[i].ToString();
            return parts;
        }

        // True only when Windows refused the kill (another user's or an elevated process).
        bool KillRefused(int pid, TrayLog log)
        {
            Process p = Open(pid);
            if (p == null) return false;   // already gone
            using (p)
            {
                try
                {
                    p.Kill();
                    Log(log, "停止: pid " + pid + " を強制終了しました");
                }
                catch (Win32Exception e)
                {
                    Log(log, "停止: pid " + pid + " の強制終了を拒まれました（" + e.Message + "）");
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // exited meanwhile
                }
                catch (Exception e)
                {
                    Log(log, "停止: pid " + pid + " を強制終了できません（" + e.Message + "）");
                }
            }
            return false;
        }

        // Opens a pid from FindAll, guarding against pid reuse: the process name must still be ServerExe's.
        Process Open(int pid)
        {
            Process p = null;
            try
            {
                p = Process.GetProcessById(pid);
                string expected = Path.GetFileNameWithoutExtension(_exePath);
                if (string.Equals(p.ProcessName, expected, StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch (Exception)
            {
            }
            if (p != null) p.Dispose();
            return null;
        }

        static bool WaitExit(Process p, int ms)
        {
            try { return p.WaitForExit(ms); }
            catch (Exception) { return false; }
        }

        static string PidText(Process p)
        {
            try { return p.Id.ToString(); }
            catch (Exception) { return "?"; }
        }

        static string PidList(List<ProcInfo> list)
        {
            var parts = new List<string>();
            foreach (ProcInfo pi in list) parts.Add(pi.Pid.ToString());
            return string.Join(", ", parts.ToArray());
        }

        static void Log(TrayLog log, string message)
        {
            if (log != null) log.Write(message);
        }
    }
}
