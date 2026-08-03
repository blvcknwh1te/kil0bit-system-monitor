using System.Globalization;
using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;

namespace Kil0bitSystemMonitor.Helpers
{
    public static class ColorHex
    {
        public static bool TryParse(string? hex, out MediaColor color)
        {
            color = MediaColors.White;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            string h = hex.Trim().TrimStart('#');
            try
            {
                if (h.Length == 8)
                {
                    color = MediaColor.FromArgb(
                        byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber),
                        byte.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
                        byte.Parse(h.Substring(4, 2), NumberStyles.HexNumber),
                        byte.Parse(h.Substring(6, 2), NumberStyles.HexNumber));
                    return true;
                }
                if (h.Length == 6)
                {
                    color = MediaColor.FromRgb(
                        byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber),
                        byte.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
                        byte.Parse(h.Substring(4, 2), NumberStyles.HexNumber));
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static string ToArgbHex(MediaColor c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        public static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;

            if (d <= 0)
            {
                h = 0;
                return;
            }

            if (max == rd) h = ((gd - bd) / d + (gd < bd ? 6 : 0)) * 60;
            else if (max == gd) h = ((bd - rd) / d + 2) * 60;
            else h = ((rd - gd) / d + 4) * 60;
        }

        public static MediaColor HsvToColor(double h, double s, double v, byte a = 255)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            double r1, g1, b1;

            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            return MediaColor.FromArgb(a,
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }
    }
}
