using System;

namespace JVLinkServerTray
{
    public enum Work { Startup, Poll, Rebuild, Start, Stop, Exit }

    // The UI thread's bookkeeping for the work items. Pure: the caller passes the time and starts what it returns.
    // - One runs at a time. A user's request waits for the running one (the latest request wins); a tick never
    //   queues a poll.
    // - 終了 (Work.Exit) ends the menu work. Once the Exit item has returned (whatever happened in it), ShouldFinish.
    // - Stall guard: an item running longer than stallLimit is reported once (Stalled). From then on 終了 does not
    //   wait for it (ShouldFinish at once). If the item comes back after all, the tray carries on.
    public sealed class WorkGate
    {
        readonly bool _polls;
        readonly TimeSpan _stallLimit;
        bool _busy, _exiting, _ended, _stalled, _finish;
        Work _running;
        TimeSpan _since;
        Work? _pending;

        // polls: false for the tray that controls no server (the ini could not be read).
        public WorkGate(bool polls, TimeSpan stallLimit)
        {
            _polls = polls;
            _stallLimit = stallLimit;
        }

        public bool Busy { get { return _busy; } }
        public bool Exiting { get { return _exiting; } }
        public bool Stalled { get { return _stalled; } }
        public bool ShouldFinish { get { return _finish; } }
        public Work Running { get { return _running; } }

        // How long the running item has run.
        public TimeSpan BusyFor(TimeSpan now) { return _busy ? now - _since : TimeSpan.Zero; }

        // Startup, a menu action or 終了 (Work.Exit). Returns the work to start now, or null.
        public Work? Request(Work w, TimeSpan now)
        {
            if (_ended || _finish) return null;
            if (_exiting) return null;
            if (w == Work.Exit)
            {
                _exiting = true;
                if (_busy && _stalled)
                {
                    _finish = true;
                    _pending = null;
                    return null;
                }
            }
            if (_busy)
            {
                _pending = w;
                return null;
            }
            return Begin(w, now);
        }

        // The running work item has returned. Returns the next work to start, or null.
        public Work? Done(TimeSpan now)
        {
            if (_busy && _running == Work.Exit) _finish = true;
            _busy = false;
            _stalled = false;
            if (_ended || _finish || _pending == null) return null;
            Work next = _pending.Value;
            _pending = null;
            return Begin(next, now);
        }

        // The work item could not be started at all.
        public void Abandon()
        {
            if (_busy && _running == Work.Exit) _finish = true;
            _busy = false;
            _stalled = false;
        }

        // The timer. Returns Work.Poll to start, or null. Also trips the stall guard.
        public Work? Tick(TimeSpan now)
        {
            if (_ended || _finish) return null;
            if (_busy)
            {
                if (!_stalled && now - _since > _stallLimit)
                {
                    _stalled = true;
                    if (_exiting)
                    {
                        _finish = true;
                        _pending = null;
                    }
                }
                return null;
            }
            if (_exiting || !_polls) return null;
            return Begin(Work.Poll, now);
        }

        public void End()
        {
            _ended = true;
        }

        Work? Begin(Work w, TimeSpan now)
        {
            _busy = true;
            _running = w;
            _since = now;
            return w;
        }
    }
}
