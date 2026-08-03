using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// Версия ОС и безопасные обёртки API, которые отличаются на Win10/Win11.
    /// </summary>
    public static class OsCompat
    {
        /// <summary>Windows 11 = build 22000+.</summary>
        public static bool IsWindows11 { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

        public static double GetDpiScale(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi > 0)
                        return dpi / 96.0;
                }
            }
            catch { }

            try
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                if (screen != IntPtr.Zero)
                {
                    try
                    {
                        int px = GetDeviceCaps(screen, LogPixelsX);
                        if (px > 0)
                            return px / 96.0;
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, screen);
                    }
                }
            }
            catch { }

            return 1.0;
        }

        public static void SafeDwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                Win32Helper.DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));
            }
            catch { }
        }

        private const int LogPixelsX = 88;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
    }
}
