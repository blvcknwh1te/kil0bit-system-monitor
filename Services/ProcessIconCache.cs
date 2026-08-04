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
    /// Кэш иконок exe по пути. LRU-cap — без бесконечного роста за долгую сессию.
    /// </summary>
    public static class ProcessIconCache
    {
        private const int MaxEntries = 512;

        private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<string> InsertionOrder = new();

        public static ImageSource? Get(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return null;
            return Cache.GetOrAdd(exePath, path =>
            {
                InsertionOrder.Enqueue(path);
                TrimIfNeeded();
                return Load(path);
            });
        }

        private static void TrimIfNeeded()
        {
            while (Cache.Count > MaxEntries && InsertionOrder.TryDequeue(out var old))
                Cache.TryRemove(old, out _);
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
