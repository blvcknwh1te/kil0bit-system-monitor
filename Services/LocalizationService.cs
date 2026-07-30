using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Kil0bitSystemMonitor.Services
{
    public sealed class LocalizationService : INotifyPropertyChanged
    {
        public static LocalizationService Instance { get; } = new();

        private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
        private string _language = "en";

        public string Language
        {
            get => _language;
            private set
            {
                if (_language == value) return;
                _language = value;
                OnPropertyChanged(nameof(Language));
                OnPropertyChanged("Item[]");
                LanguageChanged?.Invoke();
            }
        }

        public event Action? LanguageChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[string key] => Get(key);

        public string Get(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            if (_strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;
            return key;
        }

        public string Format(string key, params object[] args)
        {
            try { return string.Format(Get(key), args); }
            catch { return Get(key); }
        }

        public void Initialize(string? language)
        {
            Load(Normalize(language));
        }

        public void SetLanguage(string? language)
        {
            Load(Normalize(language));
        }

        private static string Normalize(string? language)
        {
            if (string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase))
                return "ru";
            return "en";
        }

        private void Load(string language)
        {
            var map = ReadFile(language);
            if (map.Count == 0 && language != "en")
                map = ReadFile("en");

            _strings = map;
            Language = language;
            // Явно уведомляем даже при повторной загрузке того же языка (первый Init)
            OnPropertyChanged("Item[]");
            LanguageChanged?.Invoke();
        }

        private static Dictionary<string, string> ReadFile(string language)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Localization", $"{language}.json");
                if (!File.Exists(path))
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                string json = File.ReadAllText(path);
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return raw != null
                    ? new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
