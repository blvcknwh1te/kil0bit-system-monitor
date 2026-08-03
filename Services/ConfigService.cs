using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services
{
    public class ConfigService
    {
        private readonly string _configPath;
        private readonly object _saveGate = new();
        private CancellationTokenSource? _saveCts;
        private int _previewDepth;
        public AppConfig Config { get; private set; }

        public bool IsPreviewing
        {
            get { lock (_saveGate) return _previewDepth > 0; }
        }

        public void BeginPreview()
        {
            lock (_saveGate)
            {
                _previewDepth++;
                _saveCts?.Cancel();
            }
        }

        public void EndPreview(bool commit)
        {
            lock (_saveGate)
            {
                if (_previewDepth > 0)
                    _previewDepth--;
            }

            if (commit)
                SaveConfig();
        }

        public ConfigService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDir = Path.Combine(appData, "kil0bit-system-monitor");
            Directory.CreateDirectory(configDir);
            _configPath = Path.Combine(configDir, "config.json");

            Config = LoadConfig();

            StartupService.SetStartup(Config.LaunchOnStartup);

            Config.PropertyChanged += (s, e) =>
            {
                if (!IsPreviewing)
                    ScheduleSave();
                if (e.PropertyName == nameof(AppConfig.LaunchOnStartup))
                    StartupService.SetStartup(Config.LaunchOnStartup);
                if (e.PropertyName == nameof(AppConfig.Language))
                    LocalizationService.Instance.SetLanguage(Config.Language);
                if (e.PropertyName == nameof(AppConfig.DebugLogEnabled) || e.PropertyName == nameof(AppConfig.DebugLogRetention))
                    DebugLogger.Configure(Config.DebugLogEnabled, Config.DebugLogRetention);
            };

            DebugLogger.Configure(Config.DebugLogEnabled, Config.DebugLogRetention);
        }

        private AppConfig LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    string json = File.ReadAllText(_configPath);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch { }
            }
            return new AppConfig();
        }

        public event Action? SettingsChanged;

        private void ScheduleSave()
        {
            lock (_saveGate)
            {
                _saveCts?.Cancel();
                _saveCts = new CancellationTokenSource();
                var token = _saveCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(250, token);
                        if (!token.IsCancellationRequested)
                            SaveConfig();
                    }
                    catch (TaskCanceledException) { }
                }, token);
            }
        }

        public void SaveConfig()
        {
            try
            {
                string json;
                lock (_saveGate)
                {
                    // Preview не должен попасть на диск даже от опоздавшего ScheduleSave.
                    if (_previewDepth > 0)
                        return;
                    json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_configPath, json);
                }
                SettingsChanged?.Invoke();
            }
            catch { }
        }
    }
}
