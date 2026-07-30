using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Кэш иконок exe по пути (как в диспетчере задач).
    /// </summary>
    public static class ProcessIconCache
    {
        private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static ImageSource? Get(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return null;
            return Cache.GetOrAdd(exePath, Load);
        }

        private static ImageSource? Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;

                var src = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(16, 16));
                src.Freeze();
                return src;
            }
            catch
            {
                return null;
            }
        }
    }
}
