using System;

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
        public TimeSpan? GreenSince;  // on the tray's clock
        public DateTime StoppedAt;
        public string RestartLine;
    }

    // The work items (design 4.3-4.11): startup, each look, the menu's actions and 終了. One runs at a time,
    // never on the UI thread (TrayApp and WorkGate see to that), and only they touch the state below.
    // IServerOps does the OS side, so the tests can drive this with a fake.
    public sealed class Supervisor
    {
        const string MissingExe = "JVLinkServer.exe が見つかりません（ini の ServerExe）";
        const string CannotClean = "片付けられないプロセスがあります";
        const string CannotCleanUnreadable = "片付けられないプロセスがあります（パスを読めない）";

        readonly IServerOps _ops;          // null when the ini could not be read
        readonly TrayConfig _config;
        readonly string _configError;
        readonly string _configPath;
        readonly string _logDir;
        readonly ITrayLog _log;
        readonly Func<TimeSpan> _clock;    // monotonic (Stopwatch.Elapsed)
        readonly Func<DateTime> _wall;     // only for the record and 「HH:mm から」
        readonly Watchdog _watchdog;
        readonly int _healthTimeoutMs;

        bool _stopped, _blocked;
        DateTime _stoppedAt;
        TrayColor? _lastColor;
        string _lastReason;
        TimeSpan? _greenSince;
        HealthReport _lastHealth;
        volatile bool _exitDone;

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

        public bool Controllable { get { return _ops != null; } }

        // True once Exit has run.
        public bool ExitDone { get { return _exitDone; } }

        public void Startup()
        {
            _log.Write("起動: JVLinkServer Tray " + TrayVersion.Value + " を起動しました（設定 " + _configPath
                + (_configError == null ? "、ポート " + _config.Port : "") + "、記録 " + _logDir + "）");
            int deleted = _log.Cleanup(_wall(), 14);
            if (deleted > 0) _log.Write("起動: 14 日より古い記録を " + deleted + " 個消しました");
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
                Block("プロセスの一覧を取れません（" + listError + "）");
            }
            else
            {
                StartupPlan plan = count < 0 ? StartupPlan.Adopt : JVLinkServerTray.Startup.Decide(h.Kind, count);
                if (count < 0) _log.Write("起動: プロセスの一覧を取れません（" + listError + "）。/health が答えるので引き継ぎます");
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
            Observe(true);
        }

        public void Rebuild()
        {
            _log.Write("メニュー: サーバーを作り直します");
            if (!_ops.ExeExists()) Block(MissingExe);
            else if (StopServer()) StartServer("メニューの「作り直す」");
            Observe(false);
        }

        // 起動する: also clears whatever is left (from a block or a stop) before starting.
        public void StartFromMenu()
        {
            _log.Write("メニュー: サーバーを起動します");
            if (!_ops.ExeExists()) Block(MissingExe);
            else if (StopServer()) StartServer("メニューの「起動する」");
            Observe(false);
        }

        public void StopFromMenu()
        {
            _log.Write("メニュー: サーバーを止めます");
            UserStop();
            StopServer();     // Blocked turns the gray into red with the reason
            Observe(false);
        }

        public void Exit()
        {
            if (_ops != null)
            {
                _log.Write("終了: メニューから。サーバーを止めてからトレイを終わります");
                UserStop();
                StopResult r = _ops.Stop(_config.ShutdownTimeoutSeconds);
                if (r == StopResult.Clean) _ops.Unwatch();
                else _log.Write("終了: " + CannotClean + "（そのまま終わります）");
            }
            _log.Write("終了: トレイを終わります");
            _exitDone = true;
        }

        public TrayView Snapshot()
        {
            var v = new TrayView();
            v.Color = _lastColor ?? TrayColor.Yellow;
            v.Reason = _lastReason ?? "起動しています";
            v.Stopped = _stopped;
            v.Blocked = _blocked && !_stopped;
            v.Controllable = _ops != null;
            v.GreenSince = _greenSince;
            v.StoppedAt = _stoppedAt;
            v.RestartLine = TrayText.RestartLine(_lastHealth);
            return v;
        }

        // One look: /health, the watched process, Watchdog.Step. Restarts when told to (allowRestart).
        void Observe(bool allowRestart)
        {
            HealthReport h = _ops != null ? _ops.Health(_healthTimeoutMs) : HealthReport.Unreachable("設定を読めません");
            bool alive = WatchedAlive();
            if (!alive && h.Kind != HealthKind.Unreachable)
            {
                Rewatch();
                alive = true;
            }
            TimeSpan now = _clock();
            Decision d = _watchdog.Step(alive, h, now);
            _lastHealth = h;
            Note(d, now);
            if (allowRestart && d.Action == WatchAction.StopAndStart && _ops != null)
            {
                _log.Write("起こし直し: " + d.Reason);
                if (StopServer()) StartServer("起こし直し");
                Observe(false);
            }
        }

        void Note(Decision d, TimeSpan now)
        {
            if (_lastColor == null || _lastColor.Value != d.Color)
            {
                _log.Write("色: " + (_lastColor == null ? "" : TrayText.ColorName(_lastColor.Value) + " → ")
                    + TrayText.ColorName(d.Color) + "（" + d.Reason + "）");
                _greenSince = d.Color == TrayColor.Green ? (TimeSpan?)now : null;
            }
            _lastColor = d.Color;
            _lastReason = d.Reason;
        }

        // 4.5. True when nothing is left; otherwise Blocked (and never start: ServerStarted clears Blocked).
        bool StopServer()
        {
            StopResult r = _ops.Stop(_config.ShutdownTimeoutSeconds);
            if (r == StopResult.Clean)
            {
                _ops.Unwatch();
                return true;
            }
            Block(CannotClean);
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
            if (unreadable != 0)
            {
                _log.Write("起動: 同じ名前でパスを読めないプロセスがあります（" + (unreadable < 0 ? "一覧を取れません" : unreadable + " 個") + "）");
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
        }

        void Block(string reason)
        {
            _watchdog.Blocked(reason);
            _blocked = true;
            _stopped = false;
            _log.Write("起こし直しをやめます: " + reason + "（メニューの「サーバーを起動する」で確かめ直します）");
        }

        void Started()
        {
            _watchdog.ServerStarted(_clock());
            _blocked = false;
            _stopped = false;
        }

        void UserStop()
        {
            _watchdog.UserStopped();
            _stopped = true;
            _stoppedAt = _wall();
        }
    }
}
