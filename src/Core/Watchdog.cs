using System;

namespace JVLinkServerTray
{
    public enum TrayColor { Green, Yellow, Red, Gray }
    public enum WatchAction { None, StopAndStart }

    public sealed class Decision
    {
        public TrayColor Color;
        public string Reason;
        public WatchAction Action;

        public Decision(TrayColor color, string reason, WatchAction action)
        {
            Color = color; Reason = reason; Action = action;
        }
    }

    // Decides the icon colour and when to restart the server. Time is a monotonic TimeSpan
    // (Stopwatch.Elapsed), never a wall-clock DateTime.
    public sealed class Watchdog
    {
        static readonly TimeSpan GreenReset = TimeSpan.FromMinutes(10);
        static readonly TimeSpan YellowLimit = TimeSpan.FromMinutes(10);

        readonly TrayConfig _config;
        TimeSpan _startedAt;
        int _unreachableCount;
        TimeSpan? _yellowSince;
        TimeSpan? _failureAt;
        TimeSpan? _greenSince;
        bool _escalationUsed;
        bool _stopped;
        string _blocked;

        public int Attempt { get; private set; }

        public Watchdog(TrayConfig config)
        {
            _config = config;
        }

        public void ServerStarted(TimeSpan now)
        {
            _startedAt = now;
            _unreachableCount = 0;
            _yellowSince = null;
            _failureAt = null;
            _stopped = false;
            _blocked = null;
        }

        public void UserStopped() { _stopped = true; }

        public void Blocked(string reason) { _stopped = false; _blocked = reason ?? "起動できません"; }

        public Decision Step(bool processAlive, HealthReport health, TimeSpan now)
        {
            if (_stopped) return new Decision(TrayColor.Gray, "停止中", WatchAction.None);
            if (_blocked != null) return new Decision(TrayColor.Red, _blocked, WatchAction.None);

            if (health.Kind == HealthKind.Healthy)
            {
                _unreachableCount = 0;
                _yellowSince = null;
                _failureAt = null;
                _escalationUsed = false;
                if (_greenSince == null) _greenSince = now;
                if (now - _greenSince.Value >= GreenReset) Attempt = 0;
                return new Decision(TrayColor.Green, "正常に動いています", WatchAction.None);
            }
            _greenSince = null;

            if (!processAlive && health.Kind == HealthKind.Unreachable)
                return Fail("サーバーのプロセスが終わりました", now);

            if (health.Kind == HealthKind.Answered)
            {
                _unreachableCount = 0;
                _failureAt = null;
                if (_yellowSince == null) _yellowSince = now;
                if (!_escalationUsed && now - _yellowSince.Value >= YellowLimit)
                {
                    _escalationUsed = true;
                    return new Decision(TrayColor.Yellow, "10 分以上戻りません（" + health.Status + "）。作り直します", WatchAction.StopAndStart);
                }
                if (_escalationUsed || now - _yellowSince.Value >= YellowLimit)
                    return new Decision(TrayColor.Yellow, "10 分以上戻りません（" + health.Status + "）", WatchAction.None);
                return new Decision(TrayColor.Yellow, "サーバーが応答しましたが正常ではありません（" + health.Status + "）", WatchAction.None);
            }

            // Unreachable, process alive
            if (now - _startedAt < TimeSpan.FromSeconds(_config.StartupGraceSeconds))
                return new Decision(TrayColor.Yellow, "起動中です", WatchAction.None);
            _unreachableCount++;
            if (_unreachableCount < _config.UnreachableLimit)
                return new Decision(TrayColor.Yellow, "応答がありません（" + _unreachableCount + "/" + _config.UnreachableLimit + "）", WatchAction.None);
            return Fail(_unreachableCount + " 回続けて応答がありません", now);
        }

        Decision Fail(string reason, TimeSpan now)
        {
            if (_failureAt == null) _failureAt = now;
            TimeSpan due = _failureAt.Value + Backoff.Delay(Attempt);
            if (now >= due)
            {
                Attempt++;
                _failureAt = null;
                _unreachableCount = 0;
                return new Decision(TrayColor.Red, reason + "。起動し直します", WatchAction.StopAndStart);
            }
            int wait = (int)Math.Ceiling((due - now).TotalSeconds);
            return new Decision(TrayColor.Red, reason + "。あと " + wait + " 秒で起動し直します", WatchAction.None);
        }
    }
}
