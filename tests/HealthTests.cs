using System;

namespace JVLinkServerTray.Tests
{
    public static class HealthTests
    {
        const string Healthy = "{\"status\":\"healthy\",\"ready\":true,\"components\":{\"worker_process\":{\"status\":\"running\",\"restart_count\":2,\"last_restart_reason\":\"post-stream health check for /query/realtime: unhealthy\"}}}";
        [Test] public static void Health_200healthy_isHealthy() {
            var h = HealthParser.Parse(200, Healthy);
            Assert.Equal(HealthKind.Healthy, h.Kind, "kind"); Assert.Equal(2L, h.RestartCount, "restarts");
            Assert.True(h.LastRestartReason.StartsWith("post-stream"), "reason");
        }
        [Test] public static void Health_503starting_isAnswered() {
            var h = HealthParser.Parse(503, "{\"status\":\"starting\",\"components\":{\"worker_process\":{\"restart_count\":0,\"last_restart_reason\":null}}}");
            Assert.Equal(HealthKind.Answered, h.Kind, "kind"); Assert.Equal("starting", h.Status, "status");
            Assert.Equal(503, h.HttpStatus, "http"); Assert.Null(h.LastRestartReason, "reason");
        }
        [Test] public static void Health_200butNotHealthy_isAnswered() {
            Assert.Equal(HealthKind.Answered, HealthParser.Parse(200, "{\"status\":\"unhealthy\"}").Kind, "kind");
        }
        [Test] public static void Health_nonJson404_isAnswered() {
            var h = HealthParser.Parse(404, "<html>Not Found</html>");
            Assert.Equal(HealthKind.Answered, h.Kind, "kind"); Assert.Equal("http 404", h.Status, "status");
            Assert.Equal(-1L, h.RestartCount, "restarts");
        }
        [Test] public static void Health_garbage_neverThrows() {
            string[] bodies = { null, "", "[1,2]", "42", "null", "{\"status\":5}", "{\"status\":\"healthy\",\"components\":[1]}",
                "{\"status\":\"healthy\",\"components\":{\"worker_process\":{\"restart_count\":\"x\",\"last_restart_reason\":7}}}", "{" };
            foreach (string b in bodies) {
                var h = HealthParser.Parse(200, b);
                Assert.NotNull(h, "report for " + b);
                Assert.True(h.Kind != HealthKind.Unreachable, "kind for " + b);
            }
            Assert.Equal(HealthKind.Answered, HealthParser.Parse(200, "[1,2]").Kind, "array");
            Assert.Equal(-1L, HealthParser.Parse(200, "{\"status\":\"healthy\",\"components\":{\"worker_process\":{\"restart_count\":\"x\"}}}").RestartCount, "bad count");
        }
        [Test] public static void Health_restartCount_decimalAndLong() {
            var h = HealthParser.Parse(200, "{\"status\":\"healthy\",\"components\":{\"worker_process\":{\"restart_count\":3.0}}}");
            Assert.Equal(3L, h.RestartCount, "decimal");
            h = HealthParser.Parse(200, "{\"status\":\"healthy\",\"components\":{\"worker_process\":{\"restart_count\":5000000000}}}");
            Assert.Equal(5000000000L, h.RestartCount, "long");
        }
        [Test] public static void Health_unreachable() {
            var h = HealthReport.Unreachable("timeout");
            Assert.Equal(HealthKind.Unreachable, h.Kind, "kind"); Assert.Equal(0, h.HttpStatus, "http");
            Assert.Equal("timeout", h.Error, "error"); Assert.Equal(-1L, h.RestartCount, "restarts");
        }
        [Test] public static void Backoff_delays() {
            int[] want = { 10, 30, 60, 120, 300, 300 };
            for (int i = 0; i < want.Length; i++) Assert.Equal(TimeSpan.FromSeconds(want[i]), Backoff.Delay(i), "attempt " + i);
            Assert.Equal(TimeSpan.FromSeconds(10), Backoff.Delay(-1), "negative");
        }
        [Test] public static void Startup_decides() {
            Assert.Equal(StartupPlan.Adopt, Startup.Decide(HealthKind.Healthy, 0), "healthy");
            Assert.Equal(StartupPlan.Adopt, Startup.Decide(HealthKind.Answered, 2), "503");
            Assert.Equal(StartupPlan.WaitForStarting, Startup.Decide(HealthKind.Unreachable, 1), "starting");
            Assert.Equal(StartupPlan.Spawn, Startup.Decide(HealthKind.Unreachable, 0), "none");
        }
    }
}
