using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace JVLinkServerTray
{
    // The tray (design 4.3-4.9). Threading: the UI thread owns the icon, the menu, the timer and the
    // busy flag. Everything that can block (HTTP, WMI, starting and stopping the server) runs as one
    // work item at a time on the thread pool; only that work item touches the watch state below, and
    // it hands an immutable View back to the UI thread.
    public sealed class TrayApp : ApplicationContext
    {
        const string Title = "JVLinkServer Tray";
        const string ExitQuestion = "サーバーも止まります。JVLinkServer につないでいるプログラムは、データを取れなくなります。終了しますか？";
        const string MissingExe = "JVLinkServer.exe が見つかりません（ini の ServerExe）";
        const string CannotClean = "片付けられないプロセスがあります";
        const string CannotCleanUnreadable = "片付けられないプロセスがあります（パスを読めない）";

        sealed class View
        {
            public TrayColor Color;
            public string Reason;
            public bool Stopped;          // the user chose 止める (gray)
            public bool Blocked;          // red, no restarts until 起動する
            public bool Controllable;     // false when the ini could not be read
            public TimeSpan? GreenSince;  // on _clock
            public DateTime StoppedAt;
            public string RestartLine;
        }

        // Set once.
        readonly TrayConfig _config;
        readonly string _configPath;
        readonly string _configError;
        readonly TrayLog _log;
        readonly HealthClient _client;     // null when the ini could not be read
        readonly ServerProcess _server;    // null when the ini could not be read
        readonly Watchdog _watchdog;
        readonly Stopwatch _clock;
        readonly int _healthTimeoutMs;

        // UI thread only.
        readonly Control _invoker;
        readonly NotifyIcon _icon;
        readonly ContextMenu _menu;
        readonly MenuItem _statusItem, _restartsItem, _openLogsItem, _rebuildItem, _stopStartItem, _exitItem;
        readonly System.Windows.Forms.Timer _timer;
        bool _busy, _exiting, _confirming, _ended;
        Action _pending;
        View _view;

        // The running work item only.
        Process _watched;                  // the supervisor; null = watch by HTTP only
        bool _stopped, _blocked;
        DateTime _stoppedAt;
        TrayColor? _lastColor;
        string _lastReason;
        TimeSpan? _greenSince;
        HealthReport _lastHealth;
        volatile bool _exitDone;

        public TrayApp(TrayConfig config, string configPath, string configError, TrayLog log)
        {
            _configPath = configPath;
            _configError = config == null && configError == null ? "設定を読めません" : configError;
            _config = config ?? new TrayConfig();     // only for timings when the ini could not be read
            _log = log;
            _clock = Stopwatch.StartNew();
            _watchdog = new Watchdog(_config);
            if (_configError == null)
            {
                _client = new HealthClient(_config.Port);
                _server = new ServerProcess(_config.ServerExe, _config.ServerArgs);
            }
            int poll = Math.Min(Math.Max(_config.PollSeconds, 1), 3600);
            _healthTimeoutMs = Math.Min(3000, poll * 1000 - 500);

            _invoker = new Control();
            IntPtr unused = _invoker.Handle;    // create the window now, on the UI thread, for BeginInvoke

            _statusItem = new MenuItem("状態: 確かめています");
            _statusItem.Enabled = false;
            _restartsItem = new MenuItem("");
            _restartsItem.Enabled = false;
            _restartsItem.Visible = false;
            _openLogsItem = new MenuItem("ログのフォルダを開く", OnOpenLogs);
            _rebuildItem = new MenuItem("サーバーを作り直す", OnRebuild);
            _stopStartItem = new MenuItem("サーバーを止める", OnStopStart);
            _exitItem = new MenuItem("終了…", OnExit);
            _menu = new ContextMenu(new[]
            {
                _statusItem, _restartsItem, new MenuItem("-"), _openLogsItem, _rebuildItem, _stopStartItem,
                new MenuItem("-"), _exitItem
            });
            _menu.Popup += OnMenuPopup;

            _icon = new NotifyIcon();
            _icon.ContextMenu = _menu;
            _view = new View();
            _view.Color = TrayColor.Yellow;
            _view.Reason = "起動しています";
            _view.Controllable = _server != null;
            Apply(_view);
            _icon.Visible = true;

            try { SystemEvents.SessionEnding += OnSessionEnding; }
            catch (Exception e) { _log.Write("起動: サインアウトの知らせを受け取れません（" + e.Message + "）"); }

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = poll * 1000;
            _timer.Tick += OnTick;
            Run(StartupWork);
            _timer.Start();
        }

        // ---- UI thread -------------------------------------------------------------------------

        void Run(Action work)
        {
            _busy = true;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    View v = null;
                    try { work(); }
                    catch (Exception e) { _log.Write("思わぬエラー: " + e.GetType().Name + ": " + e.Message); }
                    try { v = Snapshot(); }
                    catch (Exception e) { _log.Write("思わぬエラー: " + e.GetType().Name + ": " + e.Message); }
                    Post(delegate { Done(v); });
                });
            }
            catch (Exception e)
            {
                _busy = false;
                _log.Write("思わぬエラー: 作業を始められません（" + e.Message + "）");
            }
        }

        void Done(View v)
        {
            _busy = false;
            if (_ended) return;
            if (_exitDone)
            {
                Finish();
                return;
            }
            if (v != null)
            {
                _view = v;
                Apply(v);
            }
            if (_pending != null)
            {
                Action next = _pending;
                _pending = null;
                Run(next);
            }
        }

        // A user's request waits for the running work item; the latest request wins.
        void Request(Action work)
        {
            if (_ended) return;
            if (_busy) _pending = work;
            else Run(work);
        }

        void Post(Action a)
        {
            try { _invoker.BeginInvoke(a); }
            catch (Exception) { }   // the tray is closing
        }

        void OnTick(object sender, EventArgs e)
        {
            if (_busy || _exiting || _ended || _server == null) return;
            Run(PollWork);
        }

        void Apply(View v)
        {
            try
            {
                _icon.Icon = Icons.For(v.Color);
                _icon.Text = _exiting ? TrayText.Tooltip(TrayColor.Gray, "終了しています") : TrayText.Tooltip(v.Color, v.Reason);
                _statusItem.Text = MenuText(StatusText(v));
                _restartsItem.Visible = v.RestartLine != null;
                if (v.RestartLine != null) _restartsItem.Text = MenuText(v.RestartLine);
                bool off = v.Stopped || v.Blocked;
                _stopStartItem.Text = off ? "サーバーを起動する" : "サーバーを止める";
                _stopStartItem.Enabled = v.Controllable && !_exiting;
                _rebuildItem.Enabled = v.Controllable && !off && !_exiting;
                _exitItem.Enabled = !_exiting;
            }
            catch (Exception e)
            {
                _log.Write("画面: 表示を変えられません（" + e.Message + "）");
            }
        }

        string StatusText(View v)
        {
            if (_exiting) return "状態: 終了しています（サーバーを止めています）";
            TimeSpan greenFor = v.GreenSince.HasValue ? _clock.Elapsed - v.GreenSince.Value : TimeSpan.Zero;
            return TrayText.StatusLine(v.Color, v.Reason, greenFor, v.StoppedAt);
        }

        static string MenuText(string text)
        {
            return (text ?? "").Replace("&", "&&");
        }

        void OnMenuPopup(object sender, EventArgs e)
        {
            try { _statusItem.Text = MenuText(StatusText(_view)); }   // the running time moves on
            catch (Exception) { }
        }

        void OnOpenLogs(object sender, EventArgs e)
        {
            string dir = _log.Dir;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    Process p = Process.Start("explorer.exe", "\"" + dir + "\"");
                    if (p != null) p.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Write("メニュー: ログのフォルダを開けません（" + dir + "、" + ex.Message + "）");
                }
            });
        }

        void OnRebuild(object sender, EventArgs e)
        {
            if (_exiting || _server == null) return;
            Request(RebuildWork);
        }

        void OnStopStart(object sender, EventArgs e)
        {
            if (_exiting || _server == null) return;
            if (_view.Stopped || _view.Blocked) Request(StartWork);
            else Request(StopWork);
        }

        void OnExit(object sender, EventArgs e)
        {
            if (_exiting || _confirming || _ended) return;
            DialogResult answer;
            _confirming = true;
            try
            {
                answer = MessageBox.Show(ExitQuestion, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
            }
            catch (Exception ex)
            {
                _log.Write("終了: 確認の画面を出せません（" + ex.Message + "）");
                return;
            }
            finally
            {
                _confirming = false;
            }
            if (answer != DialogResult.Yes || _exiting || _ended) return;
            _exiting = true;
            _timer.Stop();
            Apply(_view);
            Request(ExitWork);
        }

        // 4.8: sign-out or Windows shutting down. Leave the server alone; just take the icon away and go.
        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            if (_invoker.InvokeRequired)
            {
                try { _invoker.Invoke(new Action(delegate { OnSessionEnding(sender, e); })); }
                catch (Exception) { }
                return;
            }
            if (_ended) return;
            _log.Write("終了: サインアウトか Windows の終了です。アイコンを消して終わります（サーバーは止めません）");
            _exiting = true;
            Finish();
        }

        void Finish()
        {
            if (_ended) return;
            _ended = true;
            try { _timer.Stop(); _timer.Dispose(); } catch (Exception) { }
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch (Exception) { }
            try { _icon.Visible = false; _icon.Dispose(); } catch (Exception) { }
            try { _menu.Dispose(); } catch (Exception) { }
            ExitThread();
        }

        // ---- the work item (thread pool, one at a time) -------------------------------------------

        void StartupWork()
        {
            _log.Write("起動: JVLinkServer Tray " + TrayVersion.Value + " を起動しました（設定 " + _configPath
                + (_configError == null ? "、ポート " + _config.Port : "") + "、記録 " + _log.Dir + "）");
            int deleted = _log.Cleanup(DateTime.Now, 14);
            if (deleted > 0) _log.Write("起動: 14 日より古い記録を " + deleted + " 個消しました");
            if (_configError != null)
            {
                Block(_configError);
                Observe(false);
                return;
            }
            if (!_server.ExeExists())
            {
                Block(MissingExe);
                Observe(false);
                return;
            }

            HealthReport h = _client.Get(2000);
            int count = -1;
            string listError = null;
            try { count = _server.FindAll().Count; }
            catch (Exception e) { listError = e.Message; }

            if (count < 0 && h.Kind == HealthKind.Unreachable)
            {
                Block("プロセスの一覧を取れません（" + listError + "）");
            }
            else
            {
                StartupPlan plan = count < 0 ? StartupPlan.Adopt : Startup.Decide(h.Kind, count);
                if (count < 0) _log.Write("起動: プロセスの一覧を取れません（" + listError + "）。/health が答えるので引き継ぎます");
                if (plan == StartupPlan.Spawn)
                {
                    StartServer("トレイの起動");
                }
                else
                {
                    Process sup = count > 0 ? FindSupervisor() : null;
                    SetWatched(sup);
                    Started();
                    string who = sup != null ? "見張り役 pid " + Pid(sup) : "見張り役が見つからないので HTTP だけで見ます";
                    if (plan == StartupPlan.Adopt)
                        _log.Write("引き継ぎ: 動いているサーバーを見張ります（/health: " + h.Status + "、" + who + "）");
                    else
                        _log.Write("引き継ぎ: 起動しかけのサーバーを待ちます（/health は答えません、プロセス " + count + " 個、" + who + "）");
                }
            }
            Observe(false);
        }

        void PollWork()
        {
            Observe(true);
        }

        // One look: /health, the watched process, Watchdog.Step. Restarts when told to (allowRestart).
        void Observe(bool allowRestart)
        {
            HealthReport h = _client != null ? _client.Get(_healthTimeoutMs) : HealthReport.Unreachable("設定を読めません");
            bool alive = WatchedAlive();
            if (!alive && h.Kind != HealthKind.Unreachable)
            {
                Rewatch();
                alive = true;
            }
            TimeSpan now = _clock.Elapsed;
            Decision d = _watchdog.Step(alive, h, now);
            _lastHealth = h;
            Note(d, now);
            if (allowRestart && d.Action == WatchAction.StopAndStart && _server != null)
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

        View Snapshot()
        {
            var v = new View();
            v.Color = _lastColor ?? TrayColor.Yellow;
            v.Reason = _lastReason ?? "起動しています";
            v.Stopped = _stopped;
            v.Blocked = _blocked && !_stopped;
            v.Controllable = _server != null;
            v.GreenSince = _greenSince;
            v.StoppedAt = _stoppedAt;
            v.RestartLine = TrayText.RestartLine(_lastHealth);
            return v;
        }

        void RebuildWork()
        {
            _log.Write("メニュー: サーバーを作り直します");
            if (!_server.ExeExists()) Block(MissingExe);
            else if (StopServer()) StartServer("メニューの「作り直す」");
            Observe(false);
        }

        // 起動する: also clears whatever is left (from a block or a stop) before starting.
        void StartWork()
        {
            _log.Write("メニュー: サーバーを起動します");
            if (!_server.ExeExists()) Block(MissingExe);
            else if (StopServer()) StartServer("メニューの「起動する」");
            Observe(false);
        }

        void StopWork()
        {
            _log.Write("メニュー: サーバーを止めます");
            UserStop();
            StopServer();     // Blocked turns the gray into red with the reason
            Observe(false);
        }

        void ExitWork()
        {
            if (_server != null)
            {
                _log.Write("終了: メニューから。サーバーを止めてからトレイを終わります");
                UserStop();
                StopResult r = _server.Stop(_client, _watched, _config.ShutdownTimeoutSeconds, _log);
                if (r == StopResult.Clean) SetWatched(null);
                else _log.Write("終了: " + CannotClean + "（そのまま終わります）");
            }
            _log.Write("終了: トレイを終わります");
            _exitDone = true;
        }

        // 4.5. True when nothing is left; otherwise Blocked (and never start: ServerStarted clears Blocked).
        bool StopServer()
        {
            StopResult r = _server.Stop(_client, _watched, _config.ShutdownTimeoutSeconds, _log);
            if (r == StopResult.Clean)
            {
                SetWatched(null);
                return true;
            }
            Block(CannotClean);
            return false;
        }

        bool StartServer(string why)
        {
            if (!_server.ExeExists())
            {
                Block(MissingExe);
                return false;
            }
            int unreadable = _server.CountUnreadableSameName();
            if (unreadable != 0)
            {
                _log.Write("起動: 同じ名前でパスを読めないプロセスがあります（" + (unreadable < 0 ? "一覧を取れません" : unreadable + " 個") + "）");
                Block(CannotCleanUnreadable);
                return false;
            }
            Process p;
            try
            {
                p = _server.Start();
            }
            catch (Exception e)
            {
                Block("サーバーを起動できません（" + e.Message + "）");
                return false;
            }
            if (p == null)
            {
                Block("サーバーを起動できません");
                return false;
            }
            SetWatched(p);
            Started();
            _log.Write("起動: サーバーを起動しました（" + why + "、pid " + Pid(p) + "）");
            return true;
        }

        Process FindSupervisor()
        {
            try { return _server.FindSupervisor(); }
            catch (Exception e)
            {
                _log.Write("見張り: 見張り役を探せません（" + e.Message + "）");
                return null;
            }
        }

        bool WatchedAlive()
        {
            if (_watched == null) return true;
            try { return !_watched.HasExited; }
            catch (Exception e)
            {
                _log.Write("見張り: 見張り役の様子を取れないので、HTTP だけで見ます（" + e.Message + "）");
                SetWatched(null);
                return true;
            }
        }

        // The watched supervisor has ended but /health answers: another supervisor serves the port.
        void Rewatch()
        {
            string old = Pid(_watched);
            Process p = _server != null ? FindSupervisor() : null;
            SetWatched(p);
            if (p != null)
                _log.Write("引き継ぎ直し: 見張り役（pid " + old + "）は終わりましたが /health が答えます。見張り役 pid " + Pid(p) + " を見張ります");
            else
                _log.Write("引き継ぎ直し: 見張り役（pid " + old + "）は終わりましたが /health が答えます。見張り役が見つからないので、HTTP だけで見ます");
        }

        void SetWatched(Process p)
        {
            if (_watched != null && _watched != p)
            {
                try { _watched.Dispose(); } catch (Exception) { }
            }
            _watched = p;
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
            _watchdog.ServerStarted(_clock.Elapsed);
            _blocked = false;
            _stopped = false;
        }

        void UserStop()
        {
            _watchdog.UserStopped();
            _stopped = true;
            _stoppedAt = DateTime.Now;
        }

        static string Pid(Process p)
        {
            if (p == null) return "?";
            try { return p.Id.ToString(); }
            catch (Exception) { return "?"; }
        }
    }
}
