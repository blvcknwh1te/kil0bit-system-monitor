using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// Единый расчёт геометрии popup относительно оверлея (process list / Task Manager).
    /// </summary>
    public static class OverlayPopupLayout
    {
        public const string OverlayWindowClass = "Kil0bitOverlayWndClass_Main";

        public const double WidthDip = 620;
        /// <summary>Task Manager чуть шире popup; центр тот же (panelCx − width/2).</summary>
        public const int TaskManagerWidthExtraPx = 50;
        public const int GapPx = 4;
        /// <summary>Зазор между popup и оверлеем.</summary>
        public const int OverlayGapPx = 10;
        public const double PreferredWorkAreaHeightFraction = 0.5;
        public const int MinHeightPx = 120;
        public const int PreferredMinHeightPx = 160;
        public const int PreferAboveMinSpacePx = 160;

        public readonly struct Placement
        {
            public int Left { get; init; }
            public int Top { get; init; }
            public int WidthPx { get; init; }
            public int HeightPx { get; init; }
            public double DpiScale { get; init; }
            public double WidthDip { get; init; }
            public double HeightDip { get; init; }
        }

        public static IntPtr FindOverlayHwnd()
            => Win32Helper.FindWindow(OverlayWindowClass, null);

        public static bool TryCompute(IntPtr overlayHwnd, out Placement placement)
            => TryCompute(overlayHwnd, IntPtr.Zero, 0, out placement);

        public static bool TryCompute(IntPtr overlayHwnd, IntPtr dpiReferenceHwnd, out Placement placement)
            => TryCompute(overlayHwnd, dpiReferenceHwnd, 0, out placement);

        public static bool TryComputeForTaskManager(IntPtr overlayHwnd, out Placement placement)
            => TryCompute(overlayHwnd, IntPtr.Zero, TaskManagerWidthExtraPx, out placement);

        /// <param name="dpiReferenceHwnd">Окно для DPI; если Zero — берётся overlay.</param>
        /// <param name="widthExtraPx">Добавка к ширине в px (центр сохраняется).</param>
        public static bool TryCompute(IntPtr overlayHwnd, IntPtr dpiReferenceHwnd, int widthExtraPx, out Placement placement)
        {
            placement = default;
            try
            {
                if (overlayHwnd == IntPtr.Zero)
                    return false;
                if (!Win32Helper.GetWindowRect(overlayHwnd, out Win32Helper.RECT wr))
                    return false;

                IntPtr hMon = MonitorFromWindow(overlayHwnd, 1);
                var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(hMon, ref mi))
                    return false;

                IntPtr dpiHwnd = dpiReferenceHwnd != IntPtr.Zero ? dpiReferenceHwnd : overlayHwnd;
                double dpi = OsCompat.GetDpiScale(dpiHwnd);

                int workLeft = mi.rcWork.Left + GapPx;
                int workTop = mi.rcWork.Top + GapPx;
                int workRight = mi.rcWork.Right - GapPx;
                int workBottom = mi.rcWork.Bottom - GapPx;

                int workH = Math.Max(MinHeightPx, workBottom - workTop);
                int preferredH = Math.Max(PreferredMinHeightPx, (int)Math.Round(workH * PreferredWorkAreaHeightFraction));
                int popupW = Math.Max(1, (int)Math.Round(WidthDip * dpi) + Math.Max(0, widthExtraPx));
                double widthDip = WidthDip + (dpi > 0 ? Math.Max(0, widthExtraPx) / dpi : 0);

                int panelCx = (wr.Left + wr.Right) / 2;
                int spaceAbove = Math.Max(0, wr.Top - workTop - OverlayGapPx);
                int spaceBelow = Math.Max(0, workBottom - wr.Bottom - OverlayGapPx);

                bool placeAbove = spaceAbove >= spaceBelow || spaceAbove >= PreferAboveMinSpacePx;
                int avail = placeAbove ? spaceAbove : spaceBelow;
                if (avail < MinHeightPx)
                    avail = workH;

                int popupH = Math.Min(preferredH, avail);
                popupH = Math.Max(MinHeightPx, popupH);

                int left = panelCx - popupW / 2;
                int maxLeft = workRight - popupW;
                if (maxLeft < workLeft) maxLeft = workLeft;
                left = SoftClamp(left, workLeft, maxLeft);

                int top = placeAbove
                    ? wr.Top - popupH - OverlayGapPx
                    : wr.Bottom + OverlayGapPx;

                int maxTop = workBottom - popupH;
                if (maxTop < workTop) maxTop = workTop;
                top = SoftClamp(top, workTop, maxTop);

                if (top + popupH > workBottom)
                {
                    popupH = Math.Max(MinHeightPx, workBottom - top);
                    if (top + popupH > workBottom)
                        top = Math.Max(workTop, workBottom - popupH);
                }

                placement = new Placement
                {
                    Left = left,
                    Top = top,
                    WidthPx = popupW,
                    HeightPx = popupH,
                    DpiScale = dpi,
                    WidthDip = widthDip,
                    HeightDip = popupH / dpi
                };
                return true;
            }
            catch
            {
                placement = default;
                return false;
            }
        }

        private static int SoftClamp(int v, int min, int max)
        {
            if (max < min) return (min + max) / 2;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public Win32Helper.RECT rcMonitor;
            public Win32Helper.RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
