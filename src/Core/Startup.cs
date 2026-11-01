namespace JVLinkServerTray
{
    public enum StartupPlan { Adopt, WaitForStarting, Spawn }

    public static class Startup
    {
        public static StartupPlan Decide(HealthKind health, int sameExeProcessCount)
        {
            if (health != HealthKind.Unreachable) return StartupPlan.Adopt;
            return sameExeProcessCount >= 1 ? StartupPlan.WaitForStarting : StartupPlan.Spawn;
        }
    }
}
