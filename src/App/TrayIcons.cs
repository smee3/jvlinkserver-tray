using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace JVLinkServerTray
{
    // The four tray icons, drawn for the taskbar as it is now (its theme gives the fill, its size the pixels)
    // and drawn again when that changes. UI thread only.
    public static class TrayIcons
    {
        const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        static Set _set;
        static Look _failed;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

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

        // Draws the icons again when the taskbar's theme or icon size changed, shows the new one for `shown`,
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

            public static Look Now()
            {
                var l = new Look();
                l.Light = IconLook.LightTaskbar(ThemeValue());
                l.Size = LegacySmallIconSize();
                return l;
            }

            public bool Same(Look o)
            {
                return o != null && Size == o.Size && Light == o.Light;
            }

            public override string ToString()
            {
                return Size + " px、" + (Light ? "明るい" : "暗い") + "タスクバー用";
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
