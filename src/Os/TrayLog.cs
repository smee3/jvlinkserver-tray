using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace JVLinkServerTray
{
    // The tray's own record: <dir>\JVLinkServerTray_yyyyMMdd.log, one line per event. Never throws.
    public sealed class TrayLog
    {
        const string Prefix = "JVLinkServerTray_";
        const string Suffix = ".log";
        static readonly object Gate = new object();
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        readonly string _dir;

        public TrayLog(string dir)
        {
            _dir = dir;
        }

        public string Dir { get { return _dir; } }

        public void Write(string message)
        {
            try
            {
                DateTime now = DateTime.Now;
                string text = (message ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                string line = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text + "\r\n";
                byte[] bytes = Utf8.GetBytes(line);
                string path = Path.Combine(_dir, Prefix + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + Suffix);
                lock (Gate)
                {
                    Directory.CreateDirectory(_dir);
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // Deletes logs whose file-name date is before today.Date - keepDays. Returns how many were deleted.
        public int Cleanup(DateTime today, int keepDays)
        {
            int deleted = 0;
            try
            {
                if (!Directory.Exists(_dir)) return 0;
                DateTime cutoff = today.Date.AddDays(-keepDays);
                foreach (string path in Directory.GetFiles(_dir, Prefix + "*" + Suffix))
                {
                    try
                    {
                        string name = Path.GetFileName(path);
                        if (name.Length != Prefix.Length + 8 + Suffix.Length) continue;
                        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;
                        DateTime date;
                        if (!DateTime.TryParseExact(name.Substring(Prefix.Length, 8), "yyyyMMdd",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                        if (date >= cutoff) continue;
                        File.Delete(path);
                        deleted++;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
            return deleted;
        }
    }
}
