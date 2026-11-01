using System;

namespace JVLinkServerTray
{
    public enum Work { Startup, Poll, Rebuild, Start, Stop, Exit }

    // The UI thread's bookkeeping for the work items: one runs at a time, and a user's request waits for the
    // running one (the latest request wins). Pure: the caller passes the time and starts what it returns.
    public sealed class WorkGate
    {
        readonly bool _polls;
        bool _busy, _exiting, _ended;
        Work? _pending;

        // polls: false for the tray that controls no server (the ini could not be read).
        public WorkGate(bool polls)
        {
            _polls = polls;
        }

        public bool Busy { get { return _busy; } }
        public bool Exiting { get { return _exiting; } }
        public bool Ended { get { return _ended; } }

        // Startup, a menu action or 終了 (Work.Exit). Returns the work to start now, or null when it waits.
        public Work? Request(Work w, TimeSpan now)
        {
            if (_ended) return null;
            if (_exiting && w != Work.Exit) return null;
            if (w == Work.Exit) _exiting = true;
            if (_busy)
            {
                _pending = w;
                return null;
            }
            return Begin(w, now);
        }

        // The running work item has finished. Returns the next work to start, or null.
        public Work? Done(TimeSpan now)
        {
            _busy = false;
            if (_ended || _pending == null) return null;
            Work next = _pending.Value;
            _pending = null;
            return Begin(next, now);
        }

        // The work item could not be started at all.
        public void Abandon()
        {
            _busy = false;
        }

        // The timer. Returns Work.Poll to start, or null.
        public Work? Tick(TimeSpan now)
        {
            if (_busy || _exiting || _ended || !_polls) return null;
            return Begin(Work.Poll, now);
        }

        public void End()
        {
            _ended = true;
        }

        Work? Begin(Work w, TimeSpan now)
        {
            _busy = true;
            return w;
        }
    }
}
