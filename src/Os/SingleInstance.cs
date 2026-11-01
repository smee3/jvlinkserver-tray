using System;
using System.Threading;

namespace JVLinkServerTray
{
    // One tray per port in this logon session. Keep the returned Mutex alive for the tray's lifetime.
    public static class SingleInstance
    {
        // Returns the owned mutex, or null when another tray for this port holds it.
        public static Mutex TryAcquire(int port)
        {
            Mutex m = null;
            try
            {
                bool createdNew;
                m = new Mutex(true, @"Local\JVLinkServerTray_" + port, out createdNew);
                if (createdNew) return m;
                bool owned;
                try { owned = m.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }   // the previous owner died; it is ours now
                if (owned) return m;
                m.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                if (m != null) m.Dispose();
                return null;
            }
        }
    }
}
