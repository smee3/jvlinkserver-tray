using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JVLinkServerTray
{
    public static class ServerArgs
    {
        public const int DefaultPort = 8765;

        // Splits on whitespace; whitespace inside "..." does not split, and the quotes are removed.
        public static List<string> Split(string args)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(args)) return result;
            var cur = new StringBuilder();
            bool inQuote = false, has = false;
            foreach (char ch in args)
            {
                if (ch == '"') { inQuote = !inQuote; has = true; }
                else if (!inQuote && char.IsWhiteSpace(ch))
                {
                    if (has) { result.Add(cur.ToString()); cur.Length = 0; has = false; }
                }
                else { cur.Append(ch); has = true; }
            }
            if (has) result.Add(cur.ToString());
            return result;
        }

        private static string ValueAfter(string args, string option)
        {
            List<string> parts = Split(args);
            for (int i = 0; i + 1 < parts.Count; i++)
                if (string.Equals(parts[i], option, StringComparison.Ordinal)) return parts[i + 1];
            return null;
        }

        public static int Port(string args)
        {
            string v = ValueAfter(args, "--port");
            int n;
            if (v != null && int.TryParse(v, out n)) return n;
            return DefaultPort;
        }

        public static string LogDir(string args, string serverExe)
        {
            string v = ValueAfter(args, "--log-dir");
            if (v != null) return v;
            return Path.Combine(Path.GetDirectoryName(serverExe), "logs");
        }

        public static bool IsWorkerCommandLine(string commandLine)
        {
            if (commandLine == null) return false;
            return string.Equals(ValueAfter(commandLine, "--mode"), "worker", StringComparison.Ordinal);
        }
    }
}
