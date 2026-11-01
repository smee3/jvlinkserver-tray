using System;
using System.Globalization;

namespace JVLinkServerTray
{
    // The words on the tray icon and its menu, and the tray's own command line. Pure functions.
    public static class TrayText
    {
        public const int TooltipLimit = 63;   // NotifyIcon.Text throws above this on .NET Framework
        const int ShortReasonLimit = 40;
        const int RestartReasonLimit = 48;

        public static string ColorWord(TrayColor c)
        {
            switch (c)
            {
                case TrayColor.Green: return "動いています";
                case TrayColor.Yellow: return "確かめています";
                case TrayColor.Red: return "止まっています";
            }
            return "止めています";
        }

        public static string ColorName(TrayColor c)
        {
            switch (c)
            {
                case TrayColor.Green: return "緑";
                case TrayColor.Yellow: return "黄";
                case TrayColor.Red: return "赤";
            }
            return "灰";
        }

        // "JVLinkServer: <colour word> <reason>", never longer than 63 characters.
        public static string Tooltip(TrayColor c, string reason)
        {
            string text = "JVLinkServer: " + ColorWord(c);
            if (!string.IsNullOrEmpty(reason)) text += " " + reason;
            return Cut(text, TooltipLimit);
        }

        // At most max characters; a cut text ends with "…" and never splits a surrogate pair.
        public static string Cut(string s, int max)
        {
            if (s == null) return "";
            if (max < 1) return "";
            if (s.Length <= max) return s;
            int n = max - 1;
            if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
            return s.Substring(0, n) + "…";
        }

        // "1 分未満", "12 分", "3 時間 12 分", "2 日 4 時間".
        public static string Duration(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1) return "1 分未満";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + " 分";
            if (t.TotalDays < 1) return (int)t.TotalHours + " 時間 " + t.Minutes + " 分";
            return (int)t.TotalDays + " 日 " + t.Hours + " 時間";
        }

        // The first menu line. greenFor is used only for green, stoppedAt (wall clock) only for gray.
        public static string StatusLine(TrayColor c, string reason, TimeSpan greenFor, DateTime stoppedAt)
        {
            switch (c)
            {
                case TrayColor.Green:
                    return "状態: " + ColorWord(c) + "（" + Duration(greenFor) + "）";
                case TrayColor.Yellow:
                    return "状態: " + ColorWord(c);
                case TrayColor.Red:
                    if (!string.IsNullOrEmpty(reason) && reason.Length <= ShortReasonLimit) return "状態: " + reason;
                    return "状態: " + ColorWord(c);
            }
            return "状態: " + ColorWord(c) + "（" + stoppedAt.ToString("HH:mm", CultureInfo.InvariantCulture) + " から）";
        }

        // The second menu line, or null when /health gave no restart information.
        public static string RestartLine(HealthReport h)
        {
            if (h == null || h.RestartCount < 0) return null;
            string line = "JV-Link の自動再起動 " + h.RestartCount + " 回";
            string reason = ShortRestartReason(h.LastRestartReason);
            if (reason.Length > 0) line += " · 最後（" + reason + "）";
            return line;
        }

        // The last ": "-separated part of the server's reason, cut to fit a menu line.
        public static string ShortRestartReason(string reason)
        {
            if (reason == null) return "";
            string r = reason.Trim();
            int i = r.LastIndexOf(": ", StringComparison.Ordinal);
            if (i >= 0 && r.Substring(i + 2).Trim().Length > 0) r = r.Substring(i + 2).Trim();
            return Cut(r, RestartReasonLimit);
        }

        // The value after --config: null when the option is absent, "" when it has no value.
        public static string ConfigArg(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase)) continue;
                return i + 1 < args.Length && args[i + 1] != null ? args[i + 1] : "";
            }
            return null;
        }
    }
}
