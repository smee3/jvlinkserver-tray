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

        public static TrayConfig LoadFile(string path)
        {
            if (!File.Exists(path)) return new TrayConfig();
            return Parse(File.ReadAllText(path, new UTF8Encoding(false)));
        }
    }
}
