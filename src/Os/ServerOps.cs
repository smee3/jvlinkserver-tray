using System;
using System.Diagnostics;

namespace JVLinkServerTray
{
    // IServerOps on this PC: ServerProcess (WMI, processes) and HealthClient. Holds the watched supervisor's
    // handle. Used by the work item only (one at a time).
    public sealed class ServerOps : IServerOps
    {
        readonly ServerProcess _server;
        readonly HealthClient _client;
        readonly TrayLog _log;
        Process _watched;

        public ServerOps(TrayConfig config, TrayLog log)
        {
            _server = new ServerProcess(config.ServerExe, config.ServerArgs);
            _client = new HealthClient(config.Port);
            _log = log;
        }

        public bool ExeExists() { return _server.ExeExists(); }

        public HealthReport Health(int timeoutMs) { return _client.Get(timeoutMs); }

        public int CountRunning() { return _server.FindAll().Count; }

        public bool WatchSupervisor()
        {
            Process p = _server.FindSupervisor();
            SetWatched(p);
            return p != null;
        }

        public bool WatchedAlive()
        {
            return _watched == null || !_watched.HasExited;
        }

        public void Unwatch() { SetWatched(null); }

        public StopResult Stop(int timeoutSeconds)
        {
            return _server.Stop(_client, _watched, timeoutSeconds, _log);
        }

        public int CountUnreadableSameName() { return _server.CountUnreadableSameName(); }

        public bool Start()
        {
            Process p = _server.Start();
            if (p == null) return false;
            SetWatched(p);
            return true;
        }

        public string WatchedPid
        {
            get
            {
                if (_watched == null) return "?";
                try { return _watched.Id.ToString(); }
                catch (Exception) { return "?"; }
            }
        }

        void SetWatched(Process p)
        {
            if (_watched != null && _watched != p)
            {
                try { _watched.Dispose(); } catch (Exception) { }
            }
            _watched = p;
        }
    }
}
