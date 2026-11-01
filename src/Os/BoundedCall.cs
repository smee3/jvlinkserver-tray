using System;
using System.Threading;

namespace JVLinkServerTray
{
    // Runs a call on a background thread and waits at most limitMs for it. A call that does not come back in
    // time is left to finish on its own; until it has, Run fails at once, so a hung WMI leaves one thread
    // behind, not one per call. One caller at a time (the work item).
    public sealed class BoundedCall
    {
        readonly int _limitMs;
        Thread _late;

        public BoundedCall(int limitMs)
        {
            _limitMs = limitMs;
        }

        // Throws TimeoutException when the call does not return in time, or while an earlier late call still
        // runs; otherwise passes the call's own exception on.
        public void Run(Action call)
        {
            if (_late != null)
            {
                if (_late.IsAlive) throw new TimeoutException("前の問い合わせがまだ返りません");
                _late = null;
            }
            Exception error = null;
            var t = new Thread(delegate()
            {
                try { call(); }
                catch (Exception e) { error = e; }
            });
            t.IsBackground = true;
            t.Start();
            if (!t.Join(_limitMs))
            {
                _late = t;
                throw new TimeoutException((_limitMs >= 1000 ? _limitMs / 1000 + " 秒" : _limitMs + " ミリ秒") + "たっても返りません");
            }
            if (error != null) throw error;
        }
    }
}
