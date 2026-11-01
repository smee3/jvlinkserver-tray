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

    public enum StopResult { Clean, Blocked }

    // Starts, finds and stops the server. Processes are matched strictly by the full path of ServerExe.
    public sealed class ServerProcess
    {
        const int KillWaitMs = 5000;

        readonly string _exePath;
        readonly string _args;

        public ServerProcess(string exePath, string args)
        {
            _exePath = exePath;
            _args = args ?? "";
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
        // Throws when WMI itself fails.
        public List<ProcInfo> FindAll()
        {
            var result = new List<ProcInfo>();
            Scan(result, null);
            return result;
        }

        // How many processes have ServerExe's file name but a path WMI cannot read (another user's or
        // elevated). They are never killed, but they may hold the port. -1 when WMI fails. Never throws.
        public int CountUnreadableSameName()
        {
            try
            {
                var unreadable = new List<int>();
                Scan(new List<ProcInfo>(), unreadable);
                return unreadable.Count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // The end of Stop: Clean only when nothing with ServerExe's path is left and no same-name process
        // with an unreadable path exists. A negative count means "could not tell" and also blocks.
        public static StopResult DecideStop(int readableLeft, int unreadableSameName)
        {
            return readableLeft == 0 && unreadableSameName == 0 ? StopResult.Clean : StopResult.Blocked;
        }

        // One WMI query by file name. Rows whose path equals ServerExe go to readable; rows whose path cannot
        // be read or evaluated go to unreadable (may be null). Other paths are ignored. Throws when WMI fails.
        void Scan(List<ProcInfo> readable, List<int> unreadable)
        {
            string full = Path.GetFullPath(_exePath);
            string name = Path.GetFileName(full).Replace("\\", "\\\\").Replace("'", "\\'");
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '" + name + "'"))
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
                                if (unreadable != null) unreadable.Add(pid);
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
                            if (unreadable != null) unreadable.Add(pid);
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
        public StopResult Stop(HealthClient client, Process supervisor, int timeoutSeconds, TrayLog log)
        {
            try
            {
                return StopCore(client, supervisor, timeoutSeconds, log);
            }
            catch (Exception e)
            {
                Log(log, "停止: 思わぬエラーで止めきれませんでした（" + e.Message + "）");
                return StopResult.Blocked;
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
                    if (TryScan(log, out left, out unreadable) && left.Count == 0) break;
                    if (sw.Elapsed.TotalSeconds >= timeoutSeconds) break;
                    Thread.Sleep(1000);
                }
            }

            // 3. Kill whatever is left (supervisors first, so none restarts a worker); 4. wait up to 5 s.
            // Same-name processes with an unreadable path are never killed, but they keep the result Blocked.
            var tried = new HashSet<int>();
            var wait = Stopwatch.StartNew();
            bool first = true, warnedUnreadable = false, ok;
            while (true)
            {
                ok = TryScan(log, out left, out unreadable);
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
                            Kill(pi.Pid, log);
                        }
                    }
                }
                first = false;
                if (wait.ElapsedMilliseconds >= KillWaitMs) break;
                Thread.Sleep(500);
            }
            Log(log, "停止 4/4: 片付けられないプロセスがあります（" + Describe(ok, left, unreadable) + "）");
            return StopResult.Blocked;
        }

        bool TryScan(TrayLog log, out List<ProcInfo> readable, out List<int> unreadable)
        {
            readable = new List<ProcInfo>();
            unreadable = new List<int>();
            try
            {
                Scan(readable, unreadable);
                return true;
            }
            catch (Exception e)
            {
                Log(log, "停止: プロセスの一覧を取れません（" + e.Message + "）");
                return false;
            }
        }

        static string Describe(bool ok, List<ProcInfo> left, List<int> unreadable)
        {
            if (!ok) return "プロセスの一覧を取れません";
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

        void Kill(int pid, TrayLog log)
        {
            Process p = Open(pid);
            if (p == null) return;   // already gone
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
