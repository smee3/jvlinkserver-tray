namespace JVLinkServerTray.Tests
{
    public static class ConfigTests
    {
        [Test] public static void Config_empty_isDefaults() {
            var c = TrayConfig.Parse("");
            Assert.Equal(@"C:\JVLinkServer\JVLinkServer.exe", c.ServerExe, "exe");
            Assert.Equal(8765, c.Port, "port"); Assert.Equal(5, c.PollSeconds, "poll");
            Assert.Equal(6, c.UnreachableLimit, "limit"); Assert.Equal(60, c.StartupGraceSeconds, "grace");
            Assert.Equal(20, c.ShutdownTimeoutSeconds, "shutdown");
            Assert.Equal(@"C:\ProgramData\JVLinkServer\logs", c.LogDir, "logdir");
        }
        [Test] public static void Config_notepadIni_parses() {
            var c = TrayConfig.Parse("\uFEFF; comment\r\n# also\r\n\r\nserverexe = \"D:\\JV Link\\JVLinkServer.exe\"\r\nServerArgs=--port 18765 --log-dir \"D:\\JV Link\\logs\"\r\nPollSeconds=2\r\nUnknown=1\r\n");
            Assert.Equal(@"D:\JV Link\JVLinkServer.exe", c.ServerExe, "exe");
            Assert.Equal(18765, c.Port, "port"); Assert.Equal(@"D:\JV Link\logs", c.LogDir, "logdir");
            Assert.Equal(2, c.PollSeconds, "poll");
        }
        [Test] public static void Config_badNumbers_fallBackToDefaults() {
            var c = TrayConfig.Parse("PollSeconds=abc\nUnreachableLimit=0\nStartupGraceSeconds=-5");
            Assert.Equal(5, c.PollSeconds, "poll"); Assert.Equal(6, c.UnreachableLimit, "limit");
            Assert.Equal(60, c.StartupGraceSeconds, "grace");
        }
        [Test] public static void Config_missingPortAndLogDir_useDefaults() {
            var c = TrayConfig.Parse(@"ServerExe=E:\x\JVLinkServer.exe" + "\nServerArgs=--startup-delay-sec 30");
            Assert.Equal(8765, c.Port, "port"); Assert.Equal(@"E:\x\logs", c.LogDir, "logdir");
        }
        [Test] public static void Config_missingFile_isDefaults() {
            Assert.Equal(8765, TrayConfig.LoadFile(@"C:\no\such\JVLinkServerTray.ini").Port, "port");
        }
        [Test] public static void ServerArgs_workerCommandLine_isRecognised() {
            Assert.True(ServerArgs.IsWorkerCommandLine("\"C:\\JVLinkServer\\JVLinkServer.exe\" --mode worker --port 52809 --log-level \"info\" --sid \"UNKNOWN\""), "worker");
            Assert.False(ServerArgs.IsWorkerCommandLine("C:\\JVLinkServer\\JVLinkServer.exe --port 8765 --log-dir C:\\logs"), "supervisor");
            Assert.False(ServerArgs.IsWorkerCommandLine(null), "null");
        }
    }
}
