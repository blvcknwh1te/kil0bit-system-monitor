using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Диагностический лог в log/ с ротацией по сроку и лимитом в 3 файла.
    /// </summary>
    public static class DebugLogger
    {
        private static readonly object Gate = new();
        private static bool _enabled;
        private static TimeSpan _retention = TimeSpan.FromDays(7);
        private static string _dir = "";
        private static string? _currentPath;
        private static DateTime _currentCreatedUtc;
        private static StreamWriter? _writer;
        private const int MaxFiles = 3;

        public static bool IsEnabled
        {
            get { lock (Gate) return _enabled; }
        }

        public static void Configure(bool enabled, string? retention)
        {
            lock (Gate)
            {
                _enabled = enabled;
                _retention = ParseRetention(retention);
                EnsureDirectory();
                if (!_enabled)
                {
                    CloseWriter_NoLock();
                    return;
                }
                RotateIfNeeded_NoLock(forceNew: false);
                Prune_NoLock();
            }
            Info("Logger", $"enabled={enabled}, retention={_retention.TotalDays:0}d, dir={_dir}");
        }

        public static void Info(string category, string message) => Write("INFO", category, message);
        public static void Warn(string category, string message) => Write("WARN", category, message);
        public static void Error(string category, string message) => Write("ERROR", category, message);

        public static void Write(string level, string category, string message)
        {
            if (!_enabled) return;
            try
            {
                lock (Gate)
                {
                    if (!_enabled) return;
                    EnsureDirectory();
                    RotateIfNeeded_NoLock(forceNew: false);
                    if (_writer == null) return;
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{category}] {message}";
                    _writer.WriteLine(line);
                }
            }
            catch { }
        }

        private static void EnsureDirectory()
        {
            if (!string.IsNullOrEmpty(_dir) && Directory.Exists(_dir)) return;

            string preferred = Path.Combine(AppContext.BaseDirectory, "log");
            try
            {
                Directory.CreateDirectory(preferred);
                string probe = Path.Combine(preferred, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                _dir = preferred;
                return;
            }
            catch { }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _dir = Path.Combine(appData, "kil0bit-system-monitor", "log");
            Directory.CreateDirectory(_dir);
        }

        private static void RotateIfNeeded_NoLock(bool forceNew)
        {
            bool needNew = forceNew || _writer == null || string.IsNullOrEmpty(_currentPath);
            if (!needNew && _currentCreatedUtc != default)
            {
                if (DateTime.UtcNow - _currentCreatedUtc >= _retention)
                    needNew = true;
            }

            if (!needNew) return;

            CloseWriter_NoLock();
            string name = $"debug-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            _currentPath = Path.Combine(_dir, name);
            _currentCreatedUtc = DateTime.UtcNow;
            _writer = new StreamWriter(new FileStream(_currentPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
            {
                AutoFlush = false
            };
            Prune_NoLock();
        }

        private static void Prune_NoLock()
        {
            try
            {
                if (string.IsNullOrEmpty(_dir) || !Directory.Exists(_dir)) return;
                var files = Directory.GetFiles(_dir, "debug-*.log")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ToList();

                for (int i = MaxFiles; i < files.Count; i++)
                {
                    try
                    {
                        if (string.Equals(files[i].FullName, _currentPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                        files[i].Delete();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void CloseWriter_NoLock()
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
            _currentPath = null;
        }

        private static TimeSpan ParseRetention(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "day" or "1day" or "1d" => TimeSpan.FromDays(1),
                "month" or "1month" or "1m" => TimeSpan.FromDays(30),
                _ => TimeSpan.FromDays(7)
            };
        }
    }
}
