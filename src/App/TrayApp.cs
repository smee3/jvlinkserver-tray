using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace JVLinkServerTray
{
    // The tray (design 4.3-4.9). Threading: the UI thread owns the icon, the menu, the timer and the
    // WorkGate. Everything that can block (HTTP, WMI, starting and stopping the server) is a Supervisor work
    // item run one at a time on the thread pool; it hands an immutable TrayView back to the UI thread.
    // The timer keeps ticking while exiting: it is also the stall guard (WorkGate), so a stuck work item
    // shows red and never keeps 終了 from finishing.
    public sealed class TrayApp : ApplicationContext
    {
        const string Title = "JVLinkServer Tray";
        const string StallReason = "トレイの作業が終わりません";
        static readonly TimeSpan SessionEndHold = TimeSpan.FromSeconds(60);

        // Set once.
        readonly TrayLog _log;
        readonly Supervisor _sup;
        readonly Stopwatch _clock;

        // UI thread only.
        readonly WorkGate _gate;
        readonly Control _invoker;
        readonly NotifyIcon _icon;
        readonly ContextMenu _menu;
        readonly MenuItem _statusItem, _restartsItem, _openLogsItem, _rebuildItem, _stopStartItem, _exitItem;
        readonly System.Windows.Forms.Timer _timer;
        bool _confirming, _ended;
        TrayView _view;

        public TrayApp(TrayConfig config, string configPath, string configError, TrayLog log)
        {
            if (config == null && configError == null) configError = "設定を読めません";
            if (config == null) config = new TrayConfig();     // only for timings when the ini could not be read
            _log = log;
            _clock = Stopwatch.StartNew();
            IServerOps ops = configError == null ? new ServerOps(config, log) : null;
            _sup = new Supervisor(ops, config, configError, configPath, log.Dir, log,
                delegate { return _clock.Elapsed; }, delegate { return DateTime.Now; });
            _gate = new WorkGate(_sup.Controllable, Supervisor.StallLimit(config));
            int poll = Supervisor.PollSeconds(config);

            try
            {
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
                _view = new TrayView();
                _view.Color = TrayColor.Yellow;
                _view.Reason = "起動しています";
                _view.Controllable = _sup.Controllable;
                Apply(_view);
                _icon.Visible = true;

                try
                {
                    SystemEvents.SessionEnding += OnSessionEnding;
                    SystemEvents.SessionEnded += OnSessionEnded;
                }
                catch (Exception e) { _log.Write("起動: サインアウトの知らせを受け取れません（" + e.Message + "）"); }

                _timer = new System.Windows.Forms.Timer();
                _timer.Interval = poll * 1000;
                _timer.Tick += OnTick;
                Begin(_gate.Request(Work.Startup, _clock.Elapsed));
                _timer.Start();
            }
            catch (Exception)
            {
                CleanUp();     // never leave an icon behind
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CleanUp();
            base.Dispose(disposing);
        }

        // ---- UI thread -------------------------------------------------------------------------

        // Starts the work the gate handed out (null: nothing to start).
        void Begin(Work? w)
        {
            if (w == null) return;
            Action work = WorkFor(w.Value);
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    TrayView v = null;
                    try { work(); }
                    catch (Exception e) { _log.Write("思わぬエラー: " + e.GetType().Name + ": " + e.Message); }
                    try { v = _sup.Snapshot(); }
                    catch (Exception e) { _log.Write("思わぬエラー: " + e.GetType().Name + ": " + e.Message); }
                    Post(delegate { Done(v); });
                });
            }
            catch (Exception e)
            {
                _gate.Abandon();
                _log.Write("思わぬエラー: 作業を始められません（" + e.Message + "）");
                if (_gate.ShouldFinish) Finish();
            }
        }

        Action WorkFor(Work w)
        {
            switch (w)
            {
                case Work.Startup: return _sup.Startup;
                case Work.Poll: return _sup.Poll;
                case Work.Rebuild: return _sup.Rebuild;
                case Work.Start: return _sup.StartFromMenu;
                case Work.Stop: return _sup.StopFromMenu;
            }
            return _sup.Exit;
        }

        static string WorkName(Work w)
        {
            switch (w)
            {
                case Work.Startup: return "起動";
                case Work.Poll: return "様子見";
                case Work.Rebuild: return "作り直す";
                case Work.Start: return "起動する";
                case Work.Stop: return "止める";
            }
            return "終了";
        }

        void Done(TrayView v)
        {
            if (_ended) return;
            TimeSpan now = _clock.Elapsed;
            if (_gate.Stalled)
                _log.Write("見張り: トレイの作業（" + WorkName(_gate.Running) + "）が " + (int)_gate.BusyFor(now).TotalSeconds + " 秒かかって終わりました");
            Work? next = _gate.Done(now);
            if (_gate.ShouldFinish)
            {
                Finish();
                return;
            }
            if (v != null)
            {
                _view = v;
                Apply(v);
            }
            Begin(next);
        }

        void Post(Action a)
        {
            try { _invoker.BeginInvoke(a); }
            catch (Exception) { }   // the tray is closing
        }

        void OnTick(object sender, EventArgs e)
        {
            if (_ended) return;
            TimeSpan now = _clock.Elapsed;
            bool wasStalled = _gate.Stalled;
            Work running = _gate.Running;
            TimeSpan busyFor = _gate.BusyFor(now);
            Work? w = _gate.Tick(now);
            if (!wasStalled && _gate.Stalled)
            {
                _log.Write("見張り: トレイの作業（" + WorkName(running) + "）が " + (int)busyFor.TotalSeconds
                    + " 秒たっても終わりません。終了はこの作業を待ちません");
                var v = new TrayView();
                v.Color = TrayColor.Red;
                v.Reason = StallReason;
                v.Stopped = _view.Stopped;
                v.Blocked = _view.Blocked;
                v.Controllable = _view.Controllable;
                v.Port = _view.Port;
                v.StoppedAt = _view.StoppedAt;
                v.RestartLine = _view.RestartLine;
                _view = v;
                Apply(v);
            }
            if (_gate.ShouldFinish)
            {
                _log.Write("終了: トレイの作業が終わらないので、待たずに終わります（サーバーは止めません）");
                Finish();
                return;
            }
            Begin(w);
        }

        void Apply(TrayView v)
        {
            try
            {
                bool exiting = _gate.Exiting;
                _icon.Icon = Icons.For(v.Color);
                _icon.Text = exiting ? TrayText.Tooltip(TrayColor.Gray, "終了しています") : TrayText.Tooltip(v.Color, v.Reason);
                _statusItem.Text = MenuText(StatusText(v));
                _restartsItem.Visible = v.RestartLine != null;
                if (v.RestartLine != null) _restartsItem.Text = MenuText(v.RestartLine);
                bool off = v.Stopped || v.Blocked;
                _stopStartItem.Text = off ? "サーバーを起動する" : "サーバーを止める";
                _stopStartItem.Enabled = v.Controllable && !exiting;
                _rebuildItem.Enabled = v.Controllable && !off && !exiting;
                _exitItem.Enabled = !exiting;
            }
            catch (Exception e)
            {
                _log.Write("画面: 表示を変えられません（" + e.Message + "）");
            }
        }

        string StatusText(TrayView v)
        {
            if (_gate.Exiting) return "状態: 終了しています（サーバーを止めています）";
            TimeSpan runningFor = v.RunningSince.HasValue ? _clock.Elapsed - v.RunningSince.Value : TimeSpan.Zero;
            return TrayText.StatusLine(v.Color, v.Reason, runningFor, v.StoppedAt, v.Port);
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
            if (_ended || !_sup.Controllable) return;
            Begin(_gate.Request(Work.Rebuild, _clock.Elapsed));
        }

        void OnStopStart(object sender, EventArgs e)
        {
            if (_ended || !_sup.Controllable) return;
            Work w = _view.Stopped || _view.Blocked ? Work.Start : Work.Stop;
            Begin(_gate.Request(w, _clock.Elapsed));
        }

        void OnExit(object sender, EventArgs e)
        {
            if (_gate.Exiting || _confirming || _ended) return;
            DialogResult answer;
            _confirming = true;
            try
            {
                answer = MessageBox.Show(TrayText.ExitQuestion(_sup.Controllable), Title, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
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
            if (answer != DialogResult.Yes || _gate.Exiting || _ended) return;
            Work? w = _gate.Request(Work.Exit, _clock.Elapsed);
            if (_gate.ShouldFinish)
            {
                _log.Write("終了: トレイの作業が終わらないので、待たずに終わります（サーバーは止めません）");
                Finish();
                return;
            }
            Apply(_view);
            Begin(w);
        }

        // WM_QUERYENDSESSION: Windows asks; the sign-out or shutdown may still be cancelled. Stay, but hold
        // restarts off for a while (the server may be ended first). A cancelled one simply carries on.
        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            if (_invoker.InvokeRequired)
            {
                try { _invoker.Invoke(new Action(delegate { OnSessionEnding(sender, e); })); }
                catch (Exception) { }
                return;
            }
            if (_ended) return;
            _log.Write("終了の知らせ: " + SessionWord(e.Reason) + "が始まりました。" + (int)SessionEndHold.TotalSeconds
                + " 秒は起こし直しません（取りやめになれば、そのまま見張ります）");
            _sup.HoldRestarts(_clock.Elapsed + SessionEndHold);
        }

        // 4.8, WM_ENDSESSION (wParam != 0): the session really ends. Leave the server alone; take the icon away and go.
        void OnSessionEnded(object sender, SessionEndedEventArgs e)
        {
            if (_invoker.InvokeRequired)
            {
                try { _invoker.Invoke(new Action(delegate { OnSessionEnded(sender, e); })); }
                catch (Exception) { }
                return;
            }
            if (_ended) return;
            _log.Write("終了: " + SessionWord(e.Reason) + "です。アイコンを消して終わります（サーバーは止めません）");
            Finish();
        }

        static string SessionWord(SessionEndReasons r)
        {
            return r == SessionEndReasons.Logoff ? "サインアウト" : "Windows の終了";
        }

        void Finish()
        {
            if (_ended) return;
            _ended = true;
            _gate.End();
            CleanUp();
            ExitThread();
        }

        void CleanUp()
        {
            try { if (_timer != null) { _timer.Stop(); _timer.Dispose(); } } catch (Exception) { }
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch (Exception) { }
            try { SystemEvents.SessionEnded -= OnSessionEnded; } catch (Exception) { }
            try { if (_icon != null) { _icon.Visible = false; _icon.Dispose(); } } catch (Exception) { }
            try { if (_menu != null) _menu.Dispose(); } catch (Exception) { }
        }
    }
}
