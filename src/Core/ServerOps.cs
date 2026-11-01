using System;

namespace JVLinkServerTray
{
    public enum StopResult { Clean, Blocked, Unknown }

    // The tray's own record (TrayLog in src/Os). Never throws.
    public interface ITrayLog
    {
        void Write(string message);
        int Cleanup(DateTime today, int keepDays);
    }

    // What the work items need from the OS: the server's /health, its processes and the watched supervisor's
    // handle. ServerOps (src/Os) is the real one; the tests drive Supervisor with a fake.
    public interface IServerOps
    {
        bool ExeExists();

        // Never throws; bounded by timeoutMs.
        HealthReport Health(int timeoutMs);

        // How many processes run ServerExe (supervisor and workers). Throws when the list cannot be read.
        int CountRunning();

        // Watches the supervisor among them (a ServerExe process without "--mode worker"). False when there is
        // none: then nothing is watched (HTTP only). Throws when the list cannot be read.
        bool WatchSupervisor();

        // False only when the watched supervisor has ended; true when nothing is watched. Throws when it cannot tell.
        bool WatchedAlive();

        // Drops the watched handle (HTTP only).
        void Unwatch();

        // Design 4.5 with the watched handle. Never throws.
        StopResult Stop(int timeoutSeconds);

        // Same-name processes whose path cannot be read; -1 when the list cannot be read. Never throws.
        int CountUnreadableSameName();

        // Starts ServerExe and watches it. False when no process came back; throws when it cannot start.
        bool Start();

        // The watched supervisor's pid for the record, "?" when none.
        string WatchedPid { get; }
    }
}
