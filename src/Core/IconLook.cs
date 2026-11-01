using System;

namespace JVLinkServerTray
{
    // What the tray icon should look like for the taskbar as it is now. Pure functions: the app reads the
    // theme from the registry and the DPI from Windows, and passes them in.
    public static class IconLook
    {
        public const int FillOnLightTaskbar = 0x1d3557;   // dark navy horse on a light taskbar
        public const int FillOnDarkTaskbar = 0xe8eef7;    // light horse on a dark taskbar
        const int BaseDpi = 96;
        const int BaseSize = 16;

        // HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize, SystemUsesLightTheme (DWORD):
        // 0 = dark taskbar. Missing, unreadable or anything else = light.
        public static bool LightTaskbar(object systemUsesLightTheme)
        {
            return !(systemUsesLightTheme is int) || (int)systemUsesLightTheme != 0;
        }

        // 0xRRGGBB.
        public static int FillFor(bool lightTaskbar)
        {
            return lightTaskbar ? FillOnLightTaskbar : FillOnDarkTaskbar;
        }

        // The small-icon size in pixels at a DPI: Windows' own metric (GetSystemMetricsForDpi(SM_CXSMICON, dpi))
        // when it gives a sane value, otherwise 16 scaled by dpi / 96. A DPI outside 48..960 counts as 96.
        public static int SmallIconSize(int dpi, Func<int, int> metricForDpi)
        {
            if (dpi < 48 || dpi > 960) dpi = BaseDpi;
            if (metricForDpi != null)
            {
                try
                {
                    int m = metricForDpi(dpi);
                    if (m >= 16 && m <= 256) return m;
                }
                catch (Exception)
                {
                    // GetSystemMetricsForDpi needs Windows 10 1607
                }
            }
            return Math.Max(BaseSize, (BaseSize * dpi + BaseDpi / 2) / BaseDpi);
        }
    }
}
