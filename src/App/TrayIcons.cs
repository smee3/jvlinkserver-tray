using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace JVLinkServerTray
{
    // The four tray icons, drawn for the taskbar as it is now (its theme gives the fill, its DPI the size)
    // and drawn again when that changes. UI thread only.
    //
    // The size comes from the taskbar's own DPI, not the system DPI: the system DPI a process sees stays at
    // its value from when the process started (a tray started at sign-in keeps it), but a Remote Desktop
    // client can change the display's scale later, and the taskbar follows.
    // The process is per-monitor DPI aware (app.manifest), so Windows reports the real values.
    public static class TrayIcons
    {
        const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const int SmCxSmIcon = 49;
        const uint MonitorDefaultToPrimary = 1;
        const int MdtEffectiveDpi = 0;

        static Set _set;
        static Look _failed;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr window);                   // Windows 10 1607
        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();                                 // Windows 10 1607
        [DllImport("user32.dll")]
        static extern int GetSystemMetricsForDpi(int index, uint dpi);        // Windows 10 1607
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);   // Windows 8.1
        [DllImport("user32.dll")]
        static extern IntPtr GetThreadDpiAwarenessContext();                  // Windows 10 1607
        [DllImport("user32.dll")]
        static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);  // Windows 10 1607

        // Falls back to the stock application icon if drawing fails.
        public static Icon For(TrayColor c)
        {
            try
            {
                if (_set == null) _set = Set.Draw(Look.Now());
                return _set.Items[(int)c];
            }
            catch (Exception)
            {
                return SystemIcons.Application;
            }
        }

        // What the icons in use were drawn for (for the log).
        public static string Describe()
        {
            return _set == null ? "標準のアイコン（描けませんでした）" : _set.Look.ToString();
        }

        // Draws the icons again when the taskbar's theme or DPI changed, shows the new one for `shown`,
        // and only then destroys the old ones (NotifyIcon hands its handle to the shell again on every text
        // change). Returns the line for the log, or null when nothing changed.
        public static string Refresh(NotifyIcon target, TrayColor shown)
        {
            Look now = Look.Now();
            if (_set != null && _set.Look.Same(now)) return null;
            if (_failed != null && _failed.Same(now)) return null;   // tried already; wait for the next change
            Set fresh;
            try
            {
                fresh = Set.Draw(now);
            }
            catch (Exception e)
            {
                _failed = now;
                return "アイコン: 描けません（" + now + "、" + e.Message + "）";
            }
            _failed = null;
            Set old = _set;
            _set = fresh;
            try
            {
                target.Icon = fresh.Items[(int)shown];
            }
            finally
            {
                if (old != null) old.Dispose();
            }
            return "アイコン: 描き直しました（" + now + "）";
        }

        public static void DisposeAll()
        {
            if (_set == null) return;
            _set.Dispose();
            _set = null;
        }

        sealed class Look
        {
            public int Size;
            public bool Light;
            public int Dpi;          // 0: unknown (SystemInformation.SmallIconSize gave the size)
            public string DpiFrom;

            public static Look Now()
            {
                var l = new Look();
                l.Light = IconLook.LightTaskbar(ThemeValue());
                l.Dpi = TaskbarDpi(out l.DpiFrom);
                l.Size = l.Dpi > 0 ? IconLook.SmallIconSize(l.Dpi, MetricForDpi) : LegacySmallIconSize();
                return l;
            }

            public bool Same(Look o)
            {
                return o != null && Size == o.Size && Light == o.Light;
            }

            // "24 px、明るいタスクバー用、DPI 144（タスクバー）、システム 96、PerMonitorV2"
            public override string ToString()
            {
                string dpi = Dpi > 0 ? "DPI " + Dpi + "（" + DpiFrom + "）" : "DPI 不明";
                return Size + " px、" + (Light ? "明るい" : "暗い") + "タスクバー用、" + dpi + "、システム " + SystemDpi() + "、" + Awareness();
            }
        }

        // The DPI of the taskbar (where the icon is shown), then of the primary monitor, then the system's.
        static int TaskbarDpi(out string from)
        {
            IntPtr bar = IntPtr.Zero;
            try { bar = FindWindow("Shell_TrayWnd", null); }
            catch (Exception) { }
            if (bar != IntPtr.Zero)
            {
                try
                {
                    uint d = GetDpiForWindow(bar);
                    if (d > 0) { from = "タスクバー"; return (int)d; }
                }
                catch (Exception) { }
                try
                {
                    IntPtr monitor = MonitorFromWindow(bar, MonitorDefaultToPrimary);
                    uint x, y;
                    if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out x, out y) == 0 && x > 0) { from = "モニター"; return (int)x; }
                }
                catch (Exception) { }
            }
            int system = SystemDpi();
            from = system > 0 ? "システム" : null;
            return system;
        }

        static int SystemDpi()
        {
            try { return (int)GetDpiForSystem(); }
            catch (Exception) { return 0; }
        }

        static int MetricForDpi(int dpi)
        {
            return GetSystemMetricsForDpi(SmCxSmIcon, (uint)dpi);
        }

        static string Awareness()
        {
            try
            {
                IntPtr c = GetThreadDpiAwarenessContext();
                if (AreDpiAwarenessContextsEqual(c, new IntPtr(-4))) return "PerMonitorV2";
                if (AreDpiAwarenessContextsEqual(c, new IntPtr(-3))) return "PerMonitor";
                if (AreDpiAwarenessContextsEqual(c, new IntPtr(-2))) return "System";
                if (AreDpiAwarenessContextsEqual(c, new IntPtr(-1))) return "Unaware";
                return "DPI 認識 不明";
            }
            catch (Exception)
            {
                return "DPI 認識 不明";
            }
        }

        static object ThemeValue()
        {
            try
            {
                return Registry.GetValue(PersonalizeKey, "SystemUsesLightTheme", null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static int LegacySmallIconSize()
        {
            try
            {
                Size s = SystemInformation.SmallIconSize;
                int n = Math.Max(s.Width, s.Height);
                if (n >= 16 && n <= 256) return n;
            }
            catch (Exception)
            {
            }
            return 16;
        }

        sealed class Set : IDisposable
        {
            public Look Look;
            public readonly Icon[] Items = new Icon[4];
            public readonly IntPtr[] Handles = new IntPtr[4];

            public static Set Draw(Look look)
            {
                var set = new Set();
                set.Look = look;
                try
                {
                    Color fill = Icons.Fill(look.Light);
                    for (int i = 0; i < 4; i++)
                    {
                        using (Bitmap b = Icons.Draw(look.Size, (TrayColor)i, fill))
                        {
                            set.Handles[i] = b.GetHicon();
                            set.Items[i] = Icon.FromHandle(set.Handles[i]);   // does not own the handle: DestroyIcon in Dispose
                        }
                    }
                    return set;
                }
                catch (Exception)
                {
                    set.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                for (int i = 0; i < 4; i++)
                {
                    try { if (Items[i] != null) Items[i].Dispose(); } catch (Exception) { }
                    try { if (Handles[i] != IntPtr.Zero) DestroyIcon(Handles[i]); } catch (Exception) { }
                    Items[i] = null;
                    Handles[i] = IntPtr.Zero;
                }
            }
        }
    }
}
