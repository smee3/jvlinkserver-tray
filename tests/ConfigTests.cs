using System;
using System.IO;
using System.Text;

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
        [Test] public static void ServerArgs_portOutOfRange_isDefault() {
            Assert.Equal(8765, ServerArgs.Port("--port 0"), "0");
            Assert.Equal(8765, ServerArgs.Port("--port -1"), "negative");
            Assert.Equal(8765, ServerArgs.Port("--port 65536"), "too big");
            Assert.Equal(1, ServerArgs.Port("--port 1"), "lowest");
            Assert.Equal(65535, ServerArgs.Port("--port 65535"), "highest");
        }
        [Test] public static void Config_ansiIni_readsJapanesePaths() {
            var path = Path.Combine(NewDir(), "JVLinkServerTray.ini");
            File.WriteAllBytes(path, Encoding.GetEncoding(932).GetBytes("ServerExe=D:\\競馬\\JVLinkServer.exe\r\nServerArgs=--port 18765 --log-dir D:\\競馬\\ログ\r\n"));
            var c = TrayConfig.LoadFile(path);
            Assert.Equal(@"D:\競馬\JVLinkServer.exe", c.ServerExe, "exe"); Assert.Equal(@"D:\競馬\ログ", c.LogDir, "logdir");
        }
        [Test] public static void Config_utf8Ini_readsJapanesePaths() {
            var path = Path.Combine(NewDir(), "JVLinkServerTray.ini");
            const string text = "ServerExe=D:\\競馬\\JVLinkServer.exe\r\n";
            File.WriteAllText(path, text, new UTF8Encoding(true));
            Assert.Equal(@"D:\競馬\JVLinkServer.exe", TrayConfig.LoadFile(path).ServerExe, "with BOM");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            Assert.Equal(@"D:\競馬\JVLinkServer.exe", TrayConfig.LoadFile(path).ServerExe, "without BOM");
        }
        [Test] public static void Config_locate_noIniNextToExe_isAnError() {
            string dir = NewDir(), path;
            Assert.Equal("設定ファイル JVLinkServerTray.ini がありません", TrayConfig.Locate(null, dir, out path), "error");
            Assert.Equal(Path.Combine(dir, "JVLinkServerTray.ini"), path, "path");
        }
        [Test] public static void Config_locate_iniNextToExe_isUsed() {
            string dir = NewDir(), path;
            File.WriteAllText(Path.Combine(dir, "JVLinkServerTray.ini"), "ServerExe=x");
            Assert.Null(TrayConfig.Locate(null, dir, out path), "error");
            Assert.Equal(Path.Combine(dir, "JVLinkServerTray.ini"), path, "path");
        }
        [Test] public static void Config_locate_configArg_mustExist() {
            string dir = NewDir(), path;
            string ini = Path.Combine(dir, "smoke.ini");
            Assert.Equal("設定ファイルが見つかりません（" + ini + "）", TrayConfig.Locate(ini, dir, out path), "missing");
            File.WriteAllText(ini, "");
            Assert.Null(TrayConfig.Locate(ini, NewDir(), out path), "present");
            Assert.Equal(ini, path, "path");
            Assert.Equal("設定ファイルが見つかりません（(--config の値がありません)）", TrayConfig.Locate("", dir, out path), "no value");
        }
        static string NewDir() {
            var dir = Path.Combine(Path.GetTempPath(), "jvtray-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
        [Test] public static void ServerArgs_workerCommandLine_isRecognised() {
            Assert.True(ServerArgs.IsWorkerCommandLine("\"C:\\JVLinkServer\\JVLinkServer.exe\" --mode worker --port 52809 --log-level \"info\" --sid \"UNKNOWN\""), "worker");
            Assert.False(ServerArgs.IsWorkerCommandLine("C:\\JVLinkServer\\JVLinkServer.exe --port 8765 --log-dir C:\\logs"), "supervisor");
            Assert.False(ServerArgs.IsWorkerCommandLine(null), "null");
        }
    }
}
