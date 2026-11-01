using System;
using System.Threading;

namespace JVLinkServerTray
{
    // What the tray shows. Made by the work item, read by the UI thread; never changed after it is made.
    public sealed class TrayView
    {
        public TrayColor Color;
        public string Reason;
        public bool Stopped;          // the user chose 止める (gray)
        public bool Blocked;          // red, no restarts until 起動する
        public bool Controllable;     // false when the ini could not be read
        public TimeSpan? RunningSince;  // when the watched server started, on the tray's clock
        public int Port;              // 0 when the ini could not be read
        public DateTime StoppedAt;
        public string RestartLine;
    }

    // The work items (design 4.3-4.11): startup, each look, the menu's actions and 終了. One runs at a time,
    // never on the UI thread (TrayApp and WorkGate see to that), and only they touch the state below.
    // IServerOps does the OS side, so the tests can drive this with a fake.
    //
    // A process list that fails or times out means "could not tell": red, and retried on the backoff schedule
    // through StopServer, which gives true only after a clean list, so a retry never starts a second server.
    // Real reasons not to start stay red until 起動する (spec 4.5, 4.11): a refused kill, a same-name process
    // with an unreadable path seen by a list that worked, a missing EXE.
    public sealed class Supervisor
    {
        const string MissingExe = "JVLinkServer.exe が見つかりません（ini の ServerExe）";
        const string CannotClean = "片付けられないプロセスがあります";
        const string CannotCleanUnreadable = "片付けられないプロセスがあります（パスを読めない）";
        const string NoList = "プロセスの一覧を取れません";

        readonly IServerOps _ops;          // null when the ini could not be read
        readonly TrayConfig _config;
        readonly string _configError;
        readonly string _configPath;
        readonly string _logDir;
        readonly ITrayLog _log;
        readonly Func<TimeSpan> _clock;    // monotonic (Stopwatch.Elapsed)
        readonly Func<DateTime> _wall;     // only for the record, 「HH:mm から」 and the day of the cleanup
        readonly Watchdog _watchdog;
        readonly int _healthTimeoutMs;
        long _holdUntilTicks;              // on _clock; written by any thread (HoldRestarts)

        bool _stopped, _blocked, _unknown;
        DateTime _stoppedAt;
        DateTime _cleanedOn;
        TrayColor? _lastColor;
        string _lastReason;
        TimeSpan? _runningSince;
        HealthReport _lastHealth;

        // configError != null: the ini could not be read; ops is then ignored and no server is controlled.
        public Supervisor(IServerOps ops, TrayConfig config, string configError, string configPath, string logDir,
            ITrayLog log, Func<TimeSpan> clock, Func<DateTime> wall)
        {
            _configError = configError;
            _ops = configError == null ? ops : null;
            _config = config ?? new TrayConfig();
            _configPath = configPath;
            _logDir = logDir;
            _log = log;
            _clock = clock;
            _wall = wall;
            _watchdog = new Watchdog(_config);
            int poll = PollSeconds(_config);
            _healthTimeoutMs = Math.Min(3000, poll * 1000 - 500);
        }

        // PollSeconds clamped to 1..3600 (the timer and the /health time-out use it).
        public static int PollSeconds(TrayConfig config)
        {
            return Math.Min(Math.Max(config.PollSeconds, 1), 3600);
        }

        // How long a work item may run before the UI thread stops waiting for it: a stop can take
        // ShutdownTimeoutSeconds plus the bounded process lists.
        public static TimeSpan StallLimit(TrayConfig config)
        {
            return TimeSpan.FromSeconds(Math.Min(Math.Max(config.ShutdownTimeoutSeconds, 0), 3600) + 60);
        }

        public bool Controllable { get { return _ops != null; } }

        // No restart before until (on the clock). Any thread: SessionEnding calls it, the work item reads it.
        public void HoldRestarts(TimeSpan until)
        {
            Interlocked.Exchange(ref _holdUntilTicks, until.Ticks);
        }

        public void Startup()
        {
            _log.Write("起動: JVLinkServer Tray " + TrayVersion.Value + " を起動しました（設定 " + _configPath
                + (_configError == null ? "、ポート " + _config.Port : "") + "、記録 " + _logDir + "）");
            CleanOldLogs("起動");
            if (_configError != null)
            {
                Block(_configError);
                Observe(false);
                return;
            }
            if (!_ops.ExeExists())
            {
                Block(MissingExe);
                Observe(false);
                return;
            }

            HealthReport h = _ops.Health(2000);
            int count = -1;
            string listError = null;
            try { count = _ops.CountRunning(); }
            catch (Exception e) { listError = e.Message; }

            if (count < 0 && h.Kind == HealthKind.Unreachable)
            {
                CouldNotTell("トレイの起動のとき、" + listError);
            }
            else
            {
                StartupPlan plan = count < 0 ? StartupPlan.Adopt : JVLinkServerTray.Startup.Decide(h.Kind, count);
                if (count < 0) _log.Write("起動: " + NoList + "（" + listError + "）。/health が答えるので引き継ぎます");
                if (plan == StartupPlan.Spawn)
                {
                    StartServer("トレイの起動");
                }
                else
                {
                    bool found = count > 0 && WatchSupervisor();
                    if (!found) _ops.Unwatch();
                    Started();
                    string who = found ? "見張り役 pid " + _ops.WatchedPid : "見張り役が見つからないので HTTP だけで見ます";
                    if (plan == StartupPlan.Adopt)
                        _log.Write("引き継ぎ: 動いているサーバーを見張ります（/health: " + h.Status + "、" + who + "）");
                    else
                        _log.Write("引き継ぎ: 起動しかけのサーバーを待ちます（/health は答えません、プロセス " + count + " 個、" + who + "）");
                }
            }
            Observe(false);
        }

        public void Poll()
        {
            if (_wall().Date != _cleanedOn) CleanOldLogs("記録");
            Observe(true);
        }

        public void Rebuild()
        {
            _log.Write("メニュー: サーバーを作り直します");
            if (!_ops.ExeExists()) Block(MissingExe);
            else if (StopServer()) StartServer("メニューの「作り直す」");
            Observe(false);
        }

        // 起動する: also clears whatever is left (from a block or a stop) before starting. A second 起動する
        // that waited behind the first finds the server neither stopped nor blocked and does nothing.
        public void StartFromMenu()
        {
            if (!_stopped && !_blocked)
            {
                _log.Write("メニュー: 「サーバーを起動する」は見送ります（止めても、止まってもいません）");
                Observe(false);
                return;
            }
            _log.Write("メニュー: サーバーを起動します");
            if (!_ops.ExeExists()) Block(MissingExe);
            else if (StopServer()) StartServer("メニューの「起動する」");
            Observe(false);
        }

        // 止める: gray, no restarts. A real block turns it red with the reason; a list that cannot be read
        // keeps it gray (the user's stop is kept; 起動する looks again before it starts anything).
        public void StopFromMenu()
        {
            _log.Write("メニュー: サーバーを止めます");
            UserStop();
            StopResult r = _ops.Stop(_config.ShutdownTimeoutSeconds);
            if (r == StopResult.Clean)
            {
                _ops.Unwatch();
                _runningSince = null;
            }
            else if (r == StopResult.Blocked)
            {
                Block(CannotClean);
            }
            else
            {
                _log.Write("停止: " + NoList + "。止まったか確かめられないまま、灰にします（「サーバーを起動する」で確かめ直します）");
            }
            Observe(false);
        }

        public void Exit()
        {
            try
            {
                if (_ops != null)
                {
                    _log.Write("終了: メニューから。サーバーを止めてからトレイを終わります");
                    UserStop();
                    StopResult r = _ops.Stop(_config.ShutdownTimeoutSeconds);
                    if (r == StopResult.Clean) _ops.Unwatch();
                    else if (r == StopResult.Unknown) _log.Write("終了: " + NoList + "。止まったか確かめられません（そのまま終わります）");
                    else _log.Write("終了: " + CannotClean + "（そのまま終わります）");
                }
            }
            finally
            {
                _log.Write("終了: トレイを終わります");
            }
        }

        public TrayView Snapshot()
        {
            var v = new TrayView();
            v.Color = _lastColor ?? TrayColor.Yellow;
            v.Reason = _lastReason ?? "起動しています";
            v.Stopped = _stopped;
            v.Blocked = _blocked && !_stopped;
            v.Controllable = _ops != null;
            v.RunningSince = _runningSince;
            v.Port = _ops != null ? _config.Port : 0;
            v.StoppedAt = _stoppedAt;
            v.RestartLine = TrayText.RestartLine(_lastHealth);
            return v;
        }

        // One look: /health, the watched process, Watchdog.Step. Restarts when told to (allowRestart).
        void Observe(bool allowRestart)
        {
            HealthReport h = _ops != null ? _ops.Health(_healthTimeoutMs) : HealthReport.Unreachable("設定を読めません");
            bool alive = WatchedAlive();
            if (_unknown && h.Kind != HealthKind.Unreachable)
            {
                // The list could not be read, but a server answers: watch it (spec 4.7), do not restart it.
                bool found = WatchSupervisor();
                Started();
                _log.Write("引き継ぎ: /health が答えるので、動いているサーバーを見張ります（/health: " + h.Status + "、"
                    + (found ? "見張り役 pid " + _ops.WatchedPid : "見張り役が見つからないので HTTP だけで見ます") + "）");
                alive = true;
            }
            else if (!alive && h.Kind != HealthKind.Unreachable)
            {
                Rewatch();
                alive = true;
            }
            TimeSpan now = _clock();
            _watchdog.HoldRestarts(new TimeSpan(Interlocked.Read(ref _holdUntilTicks)));
            Decision d = _watchdog.Step(alive, h, now);
            _lastHealth = h;
            Note(d);
            if (allowRestart && d.Action == WatchAction.StopAndStart && _ops != null)
            {
                _log.Write("起こし直し: " + d.Reason);
                if (StopServer()) StartServer("起こし直し");
                Observe(false);
            }
        }

        void Note(Decision d)
        {
            if (_lastColor == null || _lastColor.Value != d.Color)
            {
                _log.Write("色: " + (_lastColor == null ? "" : TrayText.ColorName(_lastColor.Value) + " → ")
                    + TrayText.ColorName(d.Color) + "（" + d.Reason + "）");
            }
            _lastColor = d.Color;
            _lastReason = d.Reason;
        }

        // 4.5. True only when a list that worked shows nothing left. Blocked: red until 起動する. Unknown: retried.
        bool StopServer()
        {
            StopResult r = _ops.Stop(_config.ShutdownTimeoutSeconds);
            if (r == StopResult.Clean)
            {
                _ops.Unwatch();
                _runningSince = null;
                return true;
            }
            if (r == StopResult.Unknown) CouldNotTell("止まったか確かめられません");
            else Block(CannotClean);
            return false;
        }

        bool StartServer(string why)
        {
            if (!_ops.ExeExists())
            {
                Block(MissingExe);
                return false;
            }
            int unreadable = _ops.CountUnreadableSameName();
            if (unreadable < 0)
            {
                CouldNotTell("起動の前に、同じ名前のプロセスを確かめられません");
                return false;
            }
            if (unreadable > 0)
            {
                _log.Write("起動: 同じ名前でパスを読めないプロセスがあります（" + unreadable + " 個）");
                Block(CannotCleanUnreadable);
                return false;
            }
            bool started;
            try
            {
                started = _ops.Start();
            }
            catch (Exception e)
            {
                Block("サーバーを起動できません（" + e.Message + "）");
                return false;
            }
            if (!started)
            {
                Block("サーバーを起動できません");
                return false;
            }
            Started();
            _log.Write("起動: サーバーを起動しました（" + why + "、pid " + _ops.WatchedPid + "）");
            return true;
        }

        // False when no supervisor is found or the list cannot be read (then nothing is watched: HTTP only).
        bool WatchSupervisor()
        {
            try { return _ops.WatchSupervisor(); }
            catch (Exception e)
            {
                _log.Write("見張り: 見張り役を探せません（" + e.Message + "）");
                _ops.Unwatch();
                return false;
            }
        }

        bool WatchedAlive()
        {
            if (_ops == null) return true;
            try { return _ops.WatchedAlive(); }
            catch (Exception e)
            {
                _log.Write("見張り: 見張り役の様子を取れないので、HTTP だけで見ます（" + e.Message + "）");
                _ops.Unwatch();
                return true;
            }
        }

        // The watched supervisor has ended but /health answers: another supervisor serves the port.
        void Rewatch()
        {
            string old = _ops.WatchedPid;
            if (WatchSupervisor())
                _log.Write("引き継ぎ直し: 見張り役（pid " + old + "）は終わりましたが /health が答えます。見張り役 pid " + _ops.WatchedPid + " を見張ります");
            else
                _log.Write("引き継ぎ直し: 見張り役（pid " + old + "）は終わりましたが /health が答えます。見張り役が見つからないので、HTTP だけで見ます");
            Watching();
        }

        // The menu's running time: from the watched supervisor's start time, read once and put on the clock
        // (so a later clock change does not move it); without one, from now (the start of watching).
        void Watching()
        {
            TimeSpan age = TimeSpan.Zero;
            try
            {
                TimeSpan? a = _ops != null ? _ops.WatchedAge : null;
                if (a.HasValue && a.Value > TimeSpan.Zero) age = a.Value;
            }
            catch (Exception) { }
            _runningSince = _clock() - age;
        }

        void CleanOldLogs(string tag)
        {
            DateTime now = _wall();
            _cleanedOn = now.Date;
            int deleted = _log.Cleanup(now, 14);
            if (deleted > 0) _log.Write(tag + ": 14 日より古い記録を " + deleted + " 個消しました");
        }

        // Could not tell whether a server is left: red, retried on the backoff schedule (Watchdog.Unknown).
        void CouldNotTell(string why)
        {
            _watchdog.Unknown(NoList, _clock());
            _unknown = true;
            _blocked = false;
            _stopped = false;
            _log.Write("起こし直しを待ちます: " + NoList + "（" + why + "）。"
                + (int)Backoff.Delay(_watchdog.Attempt).TotalSeconds + " 秒後に確かめ直します");
        }

        void Block(string reason)
        {
            _watchdog.Blocked(reason);
            _blocked = true;
            _stopped = false;
            _unknown = false;
            _log.Write("起こし直しをやめます: " + reason + "（メニューの「サーバーを起動する」で確かめ直します）");
        }

        void Started()
        {
            _watchdog.ServerStarted(_clock());
            _blocked = false;
            _stopped = false;
            _unknown = false;
            Watching();
        }

        void UserStop()
        {
            _watchdog.UserStopped();
            _stopped = true;
            _unknown = false;
            _stoppedAt = _wall();
        }
    }
}
