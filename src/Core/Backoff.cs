using System;

namespace JVLinkServerTray
{
    public static class Backoff
    {
        public static TimeSpan Delay(int attempt)
        {
            switch (attempt)
            {
                case 0: return TimeSpan.FromSeconds(10);
                case 1: return TimeSpan.FromSeconds(30);
                case 2: return TimeSpan.FromSeconds(60);
                case 3: return TimeSpan.FromSeconds(120);
            }
            return attempt < 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(300);
        }
    }
}
