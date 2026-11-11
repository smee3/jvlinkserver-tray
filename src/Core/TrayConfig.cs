using System;
using System.IO;
using System.Text;

namespace JVLinkServerTray
{
    public sealed class TrayConfig
    {
        public string ServerExe { get; set; }
        public string ServerArgs { get; set; }
        public int PollSeconds { get; set; }
        public int UnreachableLimit { get; set; }
        public int StartupGraceSeconds { get; set; }
        public int ShutdownTimeoutSeconds { get; set; }

        public TrayConfig()
        {
            ServerExe = @"C:\JVLinkServer\JVLinkServer.exe";
            ServerArgs = @"--port 8765 --log-dir C:\ProgramData\JVLinkServer\logs --startup-delay-sec 30 --recovery-retry-interval-sec 15";
            PollSeconds = 5;
            UnreachableLimit = 6;
            StartupGraceSeconds = 60;
            ShutdownTimeoutSeconds = 20;
        }

        public int Port { get { return JVLinkServerTray.ServerArgs.Port(ServerArgs); } }
        public string LogDir { get { return JVLinkServerTray.ServerArgs.LogDir(ServerArgs, ServerExe); } }

        public static TrayConfig Parse(string iniText)
        {
            var c = new TrayConfig();
            if (string.IsNullOrEmpty(iniText)) return c;
            if (iniText[0] == '\uFEFF') iniText = iniText.Substring(1);
            foreach (string raw in iniText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    value = value.Substring(1, value.Length - 2);
                int n;
                switch (key.ToLowerInvariant())
                {
                    case "serverexe": if (value.Length > 0) c.ServerExe = value; break;
                    case "serverargs": c.ServerArgs = value; break;
                    case "pollseconds": if (Positive(value, out n)) c.PollSeconds = n; break;
                    case "unreachablelimit": if (Positive(value, out n)) c.UnreachableLimit = n; break;
                    case "startupgraceseconds": if (Positive(value, out n)) c.StartupGraceSeconds = n; break;
                    case "shutdowntimeoutseconds": if (Positive(value, out n)) c.ShutdownTimeoutSeconds = n; break;
                }
            }
            return c;
        }

        private static bool Positive(string s, out int n)
        {
            return int.TryParse(s, out n) && n >= 1;
        }

        public const string IniName = "JVLinkServerTray.ini";

        // The ini to read: the one --config names, else JVLinkServerTray.ini next to the EXE. Either way it must
        // exist: the tray never runs on the defaults, since they point at the real server (port 8765).
        // Returns null when the ini is there (path is then its full path), else the reason to show.
        public static string Locate(string configArg, string exeDir, out string path)
        {
            if (configArg == null)
            {
                path = Path.Combine(exeDir, IniName);
                return File.Exists(path) ? null : "設定ファイル " + IniName + " がありません";
            }
            path = configArg.Length == 0 ? "(--config の値がありません)" : Path.GetFullPath(configArg);
            return configArg.Length > 0 && File.Exists(path) ? null : "設定ファイルが見つかりません（" + path + "）";
        }

        // UTF-8 (with or without BOM); a file that is not valid UTF-8 is read as Shift_JIS (Notepad's "ANSI").
        public static TrayConfig LoadFile(string path)
        {
            if (!File.Exists(path)) return new TrayConfig();
            byte[] bytes = File.ReadAllBytes(path);
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.GetEncoding(932).GetString(bytes);
            }
            return Parse(text);
        }
    }
}
