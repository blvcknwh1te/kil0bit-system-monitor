using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Linq;
using System.IO;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Helpers;
using ModernWpf.Controls;

namespace Kil0bitSystemMonitor
{
    public class DiskSelectionItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = "";
        private bool _isSelected;

        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    public partial class SettingsWindow : Window
    {
        private MainViewModel _viewModel = null!;
        private ConfigService _config = null!;

        public System.Collections.ObjectModel.ObservableCollection<DiskSelectionItem> DiskItems { get; } = new();
        private System.Collections.Generic.List<string> _diskSelectionOrder = new();
        private bool _isNavigating = false;

        public SettingsWindow(MainViewModel viewModel, ConfigService config)
        {
            _viewModel = viewModel;
            _config = config;
            try 
            {
                this.InitializeComponent();
                
                // Set custom icon
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (!System.IO.File.Exists(iconPath)) iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.png");
                Kil0bitSystemMonitor.Helpers.Win32Helper.SetAppIcon(new System.Windows.Interop.WindowInteropHelper(this).Handle, iconPath);

                this.DataContext = _viewModel;
                
                // Load heavy hardware lists in background to keep UI snappy
                LoadHardwareDataAsync();

                LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
                Closed += (_, _) => LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SettingsWindow Init Error: {ex.Message}");
            }
        }

        private void OnLanguageChanged()
        {
            Dispatcher.BeginInvoke(ApplyLanguage);
        }

        private void ApplyLanguage()
        {
            try
            {
                var L = LocalizationService.Instance;
                SettingsLocalization.Apply(this, SettingsNav, SettingsRoot);

                // Footer — явно: ModernWpf/AccessText (&) ломают обход дерева.
                QuitBtn.Content = L["Settings.Footer.Quit"];
                SaveCloseBtn.Content = L["Settings.Footer.SaveClose"];
                if (ResetAllText != null)
                    ResetAllText.Text = L["Settings.Footer.ResetAll"];

                SyncComboSelections();
                DebugLogger.Info("Settings.Loc", $"Applied language={L.Language} footerQuit={QuitBtn.Content}");
            }
            catch (Exception ex)
            {
                DebugLogger.Error("Settings.Loc", ex.ToString());
            }
        }

        private void SyncComboSelections()
        {
            SelectComboByTag(ProcessSortCombo, _config.Config.ProcessListSortColumn);
            SelectComboByTag(OverlayClickModeCombo, _config.Config.OverlayClickMode);
            SelectComboByTag(LanguageCombo, _config.Config.Language);
            SelectComboByTag(DebugLogRetentionCombo, _config.Config.DebugLogRetention);
        }

        private static void SelectComboByTag(System.Windows.Controls.ComboBox? combo, string? tag)
        {
            if (combo == null || string.IsNullOrEmpty(tag)) return;
            foreach (var obj in combo.Items)
            {
                if (obj is System.Windows.Controls.ComboBoxItem item &&
                    string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void ProcessSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (ProcessSortCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag)
                _config.Config.ProcessListSortColumn = tag;
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Binding обновит Config.Language; Apply сработает через LanguageChanged
            if (!IsLoaded) return;
            Dispatcher.BeginInvoke(ApplyLanguage, System.Windows.Threading.DispatcherPriority.Background);
        }

        private async void LoadHardwareDataAsync()
        {
            try
            {
                // Defer heavy WMI/PerfCounter calls to background thread
                var gpus = await System.Threading.Tasks.Task.Run(() => TelemetryService.GetAvailableGpus());
                var disks = await System.Threading.Tasks.Task.Run(() => TelemetryService.GetAvailableDisks());
                var adapters = await System.Threading.Tasks.Task.Run(() => TelemetryService.GetAvailableNetworkAdapters());

                Dispatcher.Invoke(() => {
                    PopulateGpuList(gpus);
                    PopulateDiskList(disks);
                    PopulateNetworkList(adapters);
                    EnsureValidSelections();
                });
            }
            catch { }
        }

        private void PopulateNetworkList(System.Collections.Generic.List<string> adapters)
        {
            try
            {
                NetAdapterCombo.Items.Clear();
                NetAdapterCombo.Items.Add(new ComboBoxItem
                {
                    Content = LocalizationService.Instance["Settings.Monitoring.Default"],
                    Tag = "Default"
                });
                foreach (var adapter in adapters)
                {
                    NetAdapterCombo.Items.Add(new ComboBoxItem { Content = adapter, Tag = adapter });
                }
            }
            catch { }
        }

        private void PopulateGpuList(System.Collections.Generic.List<string> gpus)
        {
            try
            {
                GpuAdapterCombo.Items.Clear();
                GpuAdapterCombo.Items.Add(new ComboBoxItem
                {
                    Content = LocalizationService.Instance["Settings.Monitoring.Default"],
                    Tag = "Default"
                });
                foreach (var gpu in gpus)
                {
                    GpuAdapterCombo.Items.Add(new ComboBoxItem { Content = gpu, Tag = gpu });
                }
            }
            catch { }
        }

        private void PopulateDiskList(System.Collections.Generic.List<string> disks)
        {
            try
            {
                DiskItems.Clear();
                var selectedArray = _config.Config.SelectedDisks?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? new string[] { "All" };
                
                _diskSelectionOrder.Clear();
                if (!selectedArray.Contains("All") && !selectedArray.Contains("None"))
                    _diskSelectionOrder.AddRange(selectedArray);

                foreach (var disk in disks)
                {
                    if (disk == "_Total") continue;

                    var item = new DiskSelectionItem { 
                        Name = disk, 
                        IsSelected = selectedArray.Contains(disk) || selectedArray.Contains("All")
                    };
                    item.PropertyChanged += (s, e) => {
                        if (e.PropertyName == nameof(DiskSelectionItem.IsSelected)) UpdateSelectedDisks(item);
                    };
                    DiskItems.Add(item);
                }
            }
            catch { }
        }

        private void UpdateSelectedDisks(DiskSelectionItem item)
        {
            if (item.IsSelected)
            {
                if (!_diskSelectionOrder.Contains(item.Name)) _diskSelectionOrder.Add(item.Name);
            }
            else
            {
                _diskSelectionOrder.Remove(item.Name);
            }

            if (_diskSelectionOrder.Count == 0) _config.Config.SelectedDisks = "None";
            else _config.Config.SelectedDisks = string.Join(";", _diskSelectionOrder);
        }

        private void EnsureValidSelections()
        {
            try
            {
                if (_config.Config.NetworkAdapter == "All") _config.Config.NetworkAdapter = "Default";
                if (_config.Config.GpuAdapter == "All") _config.Config.GpuAdapter = "Default";
                if (_config.Config.SelectedDisks == "Default") _config.Config.SelectedDisks = "All";

                if (NetAdapterCombo.SelectedIndex == -1 && NetAdapterCombo.Items.Count > 0) NetAdapterCombo.SelectedIndex = 0;
                if (GpuAdapterCombo.SelectedIndex == -1 && GpuAdapterCombo.Items.Count > 0) GpuAdapterCombo.SelectedIndex = 0;

            }
            catch { }
        }

        private void SettingsRoot_Loaded(object sender, RoutedEventArgs e)
        {
            try 
            {
                var hWnd = new WindowInteropHelper(this).Handle;
                
                // Set taskbar icon
                string iconPng = Path.Combine(AppContext.BaseDirectory, "icon.png");
                Win32Helper.SetAppIcon(hWnd, iconPng);

                // Force Immersive Dark Mode for the title bar system buttons
                int darkMode = 1;
                OsCompat.SafeDwmSetWindowAttribute(hWnd, Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode);

                ApplyLanguage();
            }
            catch { }
        }



        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            var item = e.AddedItems[0] as ComboBoxItem;
            if (item == null) return;

            string themeName = item.Content?.ToString() ?? "Default";
            
            switch (themeName)
            {
                case "Cyberpunk":
                    _config.Config.AccentColorHex = "#FF00FF";
                    _config.Config.LabelColorHex = "#FFFF00";
                    _config.Config.BackgroundColorHex = "#B4200020";
                    _config.Config.IsTextBold = true;
                    break;
                case "Matrix":
                    _config.Config.AccentColorHex = "#32CD32";
                    _config.Config.LabelColorHex = "#00FF00";
                    _config.Config.BackgroundColorHex = "#B4001000";
                    _config.Config.FontFamily = "Consolas";
                    _config.Config.IsTextBold = true;
                    break;
                case "Stealth":
                    _config.Config.AccentColorHex = "#AAAAAA";
                    _config.Config.LabelColorHex = "#444444";
                    _config.Config.BackgroundColorHex = "#64101010";
                    _config.Config.IsTextBold = false;
                    break;
                case "Synthwave":
                    _config.Config.AccentColorHex = "#BD00FF";
                    _config.Config.LabelColorHex = "#00E0FF";
                    _config.Config.BackgroundColorHex = "#B4100520";
                    _config.Config.IsTextBold = true;
                    break;
                case "Midnight Gold":
                    _config.Config.AccentColorHex = "#D4AF37";
                    _config.Config.LabelColorHex = "#F5F5F5";
                    _config.Config.BackgroundColorHex = "#B4050505";
                    _config.Config.IsTextBold = true;
                    break;
                case "Frost":
                    _config.Config.AccentColorHex = "#E0FFFF";
                    _config.Config.LabelColorHex = "#00BFFF";
                    _config.Config.BackgroundColorHex = "#B41A2533";
                    _config.Config.IsTextBold = true;
                    break;
                case "Inferno":
                    _config.Config.AccentColorHex = "#FF4500";
                    _config.Config.LabelColorHex = "#990000";
                    _config.Config.BackgroundColorHex = "#B4100505";
                    _config.Config.IsTextBold = true;
                    break;
                case "Toxic":
                    _config.Config.AccentColorHex = "#9400D3";
                    _config.Config.LabelColorHex = "#00FF00";
                    _config.Config.BackgroundColorHex = "#B4051000";
                    _config.Config.IsTextBold = true;
                    break;
                case "Nordic":
                    _config.Config.AccentColorHex = "#4682B4";
                    _config.Config.LabelColorHex = "#FFFAFA";
                    _config.Config.BackgroundColorHex = "#B4101A25";
                    _config.Config.IsTextBold = false;
                    _config.Config.FontFamily = "Inter";
                    break;
                case "Default":
                    _config.Config.AccentColorHex = "#FFFFFF";
                    _config.Config.LabelColorHex = "#00CCFF";
                    _config.Config.BackgroundColorHex = "#B4141414";
                    _config.Config.FontFamily = "Segoe UI";
                    _config.Config.IsTextBold = true;
                    break;
            }
        }



        private void ResetToDefaults_Click(object sender, RoutedEventArgs e)
        {
            var c = _config.Config;
            c.DisplayStyle = "Text";
            c.FontFamily = "Segoe UI";
            c.AccentColorHex = "#FFFFFF";
            c.LabelColorHex = "#00CCFF";
            c.BackgroundColorHex = "#B4141414";
            c.PodColorHex = "#0FFFFFFF";
            c.ScaleFactor = 1.0;
            c.ColumnSpacing = 6;
            c.IsTextBold = true;
            c.ShowPods = true;
            c.ShowBackground = false;
            c.NetLabelColorHex = null; c.CpuRamLabelColorHex = null; c.GpuLabelColorHex = null; c.DiskLabelColorHex = null;
            c.NetAccentColorHex = null; c.CpuRamAccentColorHex = null; c.GpuAccentColorHex = null; c.DiskAccentColorHex = null;
            _config.SaveConfig();
        }

        /// <summary>Шкала 0–100; thumb не уезжает за warning max = critical − 5%.</summary>
        private void WarningThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is not Slider s || _config?.Config == null) return;
            double min = MetricAlertDefaults.WarningPercentMin;
            double max = _config.Config.WarningThresholdPercentMax;
            double v = Math.Clamp(e.NewValue, min, max);
            if (Math.Abs(s.Value - v) > 0.01)
                s.Value = v;
        }

        /// <summary>Шкала 0–100; critical только в [10, 95].</summary>
        private void CriticalThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is not Slider s) return;
            double v = Math.Clamp(e.NewValue, MetricAlertDefaults.CriticalPercentMin, MetricAlertDefaults.CriticalPercentMax);
            if (Math.Abs(s.Value - v) > 0.01)
                s.Value = v;
        }

        private async void ResetApp_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog resetDialog = new ContentDialog
            {
                Title = LocalizationService.Instance["Settings.Dialog.ResetTitle"],
                Content = LocalizationService.Instance["Settings.Dialog.ResetBody"],
                PrimaryButtonText = LocalizationService.Instance["Settings.Dialog.ResetPrimary"],
                CloseButtonText = LocalizationService.Instance["Settings.Dialog.ResetClose"],
                DefaultButton = ContentDialogButton.Close
            };

            // Set the dialog theme to dark to match the app
            ModernWpf.ThemeManager.SetRequestedTheme(resetDialog, ModernWpf.ElementTheme.Dark);

            ContentDialogResult result = await resetDialog.ShowAsync();

            if (result != ContentDialogResult.Primary) return;

            var c = _config.Config;
            c.ShowOverlay = true;
            c.LockPosition = false;
            c.LaunchOnStartup = false;
            c.HideOnFullscreen = true;
            c.StickToTaskbar = true;
            c.AlwaysOnTop = true;
            
            c.ShowCpu = true;
            c.ShowRam = true;
            c.ShowGpu = true;
            c.ShowTemp = true;
            c.ShowDisk = true;
            c.ShowDiskSpeed = true;
            c.ShowNetUp = true;
            c.ShowNetDown = true;
            
            c.NetworkAdapter = "Default";
            c.GpuAdapter = "Default";
            c.SelectedDisks = "All";
            
            c.DisplayStyle = "Text";
            c.FontFamily = "Segoe UI";
            c.AccentColorHex = "#FFFFFF";
            c.LabelColorHex = "#00CCFF";
            c.BackgroundColorHex = "#B4141414";
            c.PodColorHex = "#0FFFFFFF";
            c.ScaleFactor = 1.0;
            c.ColumnSpacing = 6;
            c.IsTextBold = true;
            c.ShowPods = true;
            c.ShowBackground = false;
            c.NetLabelColorHex = null; c.CpuRamLabelColorHex = null; c.GpuLabelColorHex = null; c.DiskLabelColorHex = null;
            c.NetAccentColorHex = null; c.CpuRamAccentColorHex = null; c.GpuAccentColorHex = null; c.DiskAccentColorHex = null;
            c.WarningThreshold = MetricAlertDefaults.WarningThreshold;
            c.CriticalThreshold = MetricAlertDefaults.CriticalThreshold;
            c.WarningColorHex = MetricAlertDefaults.WarningColorHex;
            c.CriticalColorHex = MetricAlertDefaults.CriticalColorHex;
            c.ProcessListSortColumn = "Name";
            c.ProcessListSortAscending = true;
            c.Language = "en";
            c.OverlayClickMode = "ProcessList";
            c.ProcessListShowIcons = true;
            c.DebugLogEnabled = false;
            c.DebugLogRetention = "Week";
            
            StartupService.SetStartup(false);
            _config.SaveConfig();
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag)
                return;

            // Snapshot до preview: null у section-цветов = inherit.
            string? snapshot = GetStoredHex(tag);
            string displayHex = snapshot
                ?? (IsSectionColorTag(tag)
                    ? (tag.Contains("Accent", StringComparison.Ordinal) ? _config.Config.AccentColorHex : _config.Config.LabelColorHex)
                    : "#FFFFFFFF");

            if (!ColorHex.TryParse(displayHex, out var initial))
                initial = System.Windows.Media.Colors.White;

            if (displayHex.TrimStart('#').Length == 6 && tag == "Background")
                initial.A = 0xB4;

            _config.BeginPreview();
            var picker = new ColorPickerWindow(initial) { Owner = this };
            picker.ColorChanged += c => ApplyColorHex(tag, ColorHex.ToArgbHex(c));

            bool committed = picker.ShowDialog() == true;
            if (committed)
            {
                ApplyColorHex(tag, ColorHex.ToArgbHex(picker.SelectedColor));
                _config.EndPreview(commit: true);
            }
            else
            {
                RestoreColorHex(tag, snapshot);
                _config.EndPreview(commit: false);
            }
        }

        private static bool IsSectionColorTag(string tag) => tag is
            "NetLabel" or "CpuRamLabel" or "GpuLabel" or "DiskLabel" or
            "NetAccent" or "CpuRamAccent" or "GpuAccent" or "DiskAccent";

        private string? GetStoredHex(string tag) => tag switch
        {
            "Accent" => _config.Config.AccentColorHex,
            "Label" => _config.Config.LabelColorHex,
            "Background" => _config.Config.BackgroundColorHex,
            "Pod" => _config.Config.PodColorHex,
            "NetLabel" => _config.Config.NetLabelColorHex,
            "CpuRamLabel" => _config.Config.CpuRamLabelColorHex,
            "GpuLabel" => _config.Config.GpuLabelColorHex,
            "DiskLabel" => _config.Config.DiskLabelColorHex,
            "NetAccent" => _config.Config.NetAccentColorHex,
            "CpuRamAccent" => _config.Config.CpuRamAccentColorHex,
            "GpuAccent" => _config.Config.GpuAccentColorHex,
            "DiskAccent" => _config.Config.DiskAccentColorHex,
            "Warning" => _config.Config.WarningColorHex,
            "Critical" => _config.Config.CriticalColorHex,
            _ => null
        };

        private void ApplyColorHex(string tag, string hex)
        {
            // Без лишнего PropertyChanged, если значение не менялось (в т.ч. повтор OK после preview).
            if (GetStoredHex(tag) == hex) return;

            switch (tag)
            {
                case "Accent": _config.Config.AccentColorHex = hex; break;
                case "Label": _config.Config.LabelColorHex = hex; break;
                case "Background": _config.Config.BackgroundColorHex = hex; break;
                case "Pod": _config.Config.PodColorHex = hex; break;
                case "NetLabel": _config.Config.NetLabelColorHex = hex; break;
                case "CpuRamLabel": _config.Config.CpuRamLabelColorHex = hex; break;
                case "GpuLabel": _config.Config.GpuLabelColorHex = hex; break;
                case "DiskLabel": _config.Config.DiskLabelColorHex = hex; break;
                case "NetAccent": _config.Config.NetAccentColorHex = hex; break;
                case "CpuRamAccent": _config.Config.CpuRamAccentColorHex = hex; break;
                case "GpuAccent": _config.Config.GpuAccentColorHex = hex; break;
                case "DiskAccent": _config.Config.DiskAccentColorHex = hex; break;
                case "Warning": _config.Config.WarningColorHex = hex; break;
                case "Critical": _config.Config.CriticalColorHex = hex; break;
            }
        }

        private void RestoreColorHex(string tag, string? snapshot)
        {
            switch (tag)
            {
                case "Accent": _config.Config.AccentColorHex = snapshot ?? "#FFFFFFFF"; break;
                case "Label": _config.Config.LabelColorHex = snapshot ?? "#FF00CCFF"; break;
                case "Background": _config.Config.BackgroundColorHex = snapshot ?? "#B4141414"; break;
                case "Pod": _config.Config.PodColorHex = snapshot ?? "#0FFFFFFF"; break;
                case "NetLabel": _config.Config.NetLabelColorHex = snapshot; break;
                case "CpuRamLabel": _config.Config.CpuRamLabelColorHex = snapshot; break;
                case "GpuLabel": _config.Config.GpuLabelColorHex = snapshot; break;
                case "DiskLabel": _config.Config.DiskLabelColorHex = snapshot; break;
                case "NetAccent": _config.Config.NetAccentColorHex = snapshot; break;
                case "CpuRamAccent": _config.Config.CpuRamAccentColorHex = snapshot; break;
                case "GpuAccent": _config.Config.GpuAccentColorHex = snapshot; break;
                case "DiskAccent": _config.Config.DiskAccentColorHex = snapshot; break;
                case "Warning": _config.Config.WarningColorHex = snapshot ?? MetricAlertDefaults.WarningColorHex; break;
                case "Critical": _config.Config.CriticalColorHex = snapshot ?? MetricAlertDefaults.CriticalColorHex; break;
            }
        }

        private void ClearSectionColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                switch (tag)
                {
                    case "NetLabel":     _config.Config.NetLabelColorHex = null; break;
                    case "CpuRamLabel": _config.Config.CpuRamLabelColorHex = null; break;
                    case "GpuLabel":    _config.Config.GpuLabelColorHex = null; break;
                    case "DiskLabel":   _config.Config.DiskLabelColorHex = null; break;
                    case "NetAccent":    _config.Config.NetAccentColorHex = null; break;
                    case "CpuRamAccent": _config.Config.CpuRamAccentColorHex = null; break;
                    case "GpuAccent":    _config.Config.GpuAccentColorHex = null; break;
                    case "DiskAccent":   _config.Config.DiskAccentColorHex = null; break;
                }
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            StartupService.SetStartup(_config.Config.LaunchOnStartup);
            _config.SaveConfig();
            this.Close();
        }

        private void HomeCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                SelectSection(tag);
            }
        }

        private void SettingsNav_SelectionChanged(object sender, ModernWpf.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is ModernWpf.Controls.NavigationViewItem item)
            {
                SelectSection(item.Tag?.ToString() ?? string.Empty);
            }
        }

        public void SelectSection(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName) || _isNavigating) return;

            _isNavigating = true;
            try
            {
                HomeSection.Visibility = Visibility.Collapsed;
                GeneralSection.Visibility = Visibility.Collapsed;
                MonitoringSection.Visibility = Visibility.Collapsed;
                AppearanceSection.Visibility = Visibility.Collapsed;
                AboutSection.Visibility = Visibility.Collapsed;

                switch (sectionName)
                {
                    case "Home": HomeSection.Visibility = Visibility.Visible; break;
                    case "General": GeneralSection.Visibility = Visibility.Visible; break;
                    case "Monitoring": MonitoringSection.Visibility = Visibility.Visible; break;
                    case "Appearance": AppearanceSection.Visibility = Visibility.Visible; break;
                    case "About": AboutSection.Visibility = Visibility.Visible; break;
                }

                // Sync Nav selection safely
                foreach (var item in SettingsNav.MenuItems.OfType<ModernWpf.Controls.NavigationViewItem>())
                {
                    if (item.Tag?.ToString() == sectionName)
                    {
                        if (SettingsNav.SelectedItem != item)
                            SettingsNav.SelectedItem = item;
                        break;
                    }
                }
            }
            finally
            {
                _isNavigating = false;
            }
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            App.Quit();
        }
    }
}
