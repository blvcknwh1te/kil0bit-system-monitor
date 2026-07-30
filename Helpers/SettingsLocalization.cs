using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kil0bitSystemMonitor.Services;
using ModernWpf.Controls;

namespace Kil0bitSystemMonitor.Helpers
{
    public static class SettingsLocalization
    {
        private static readonly Dictionary<string, string> TextToKey = new()
        {
            ["Welcome"] = "Settings.Home.Welcome",
            ["Kil0bit System Monitor is active and tracking your hardware performance with near-zero overhead."] = "Settings.Home.Subtitle",
            ["General"] = "Settings.Nav.General",
            ["Startup behavior, updates, and core system settings."] = "Settings.Home.General.Desc",
            ["Monitoring"] = "Settings.Nav.Monitoring",
            ["Select CPU, GPU, and Network hardware sensors."] = "Settings.Home.Monitoring.Desc",
            ["Appearance"] = "Settings.Nav.Appearance",
            ["Customize themes, colors, and font styles."] = "Settings.Home.Appearance.Desc",
            ["About"] = "Settings.Nav.About",
            ["Version info, documentation, and support links."] = "Settings.Home.About.Desc",
            ["Performance Note"] = "Settings.Home.PerfNote.Title",
            ["Your system is being monitored using low-level Win32 APIs for maximum precision."] = "Settings.Home.PerfNote.Body",
            ["Show Overlay"] = "Settings.General.ShowOverlay.Title",
            ["Enable or disable the monitor."] = "Settings.General.ShowOverlay.Desc",
            ["Lock Position"] = "Settings.General.LockPosition.Title",
            ["Prevent accidental dragging."] = "Settings.General.LockPosition.Desc",
            ["Run at Startup"] = "Settings.General.Startup.Title",
            ["Launch with Windows."] = "Settings.General.Startup.Desc",
            ["Hide in Fullscreen"] = "Settings.General.Fullscreen.Title",
            ["Hide during games/movies."] = "Settings.General.Fullscreen.Desc",
            ["Snap to Taskbar"] = "Settings.General.Snap.Title",
            ["Dock to the taskbar area."] = "Settings.General.Snap.Desc",
            ["Keep on Top"] = "Settings.General.Topmost.Title",
            ["Stay above other windows."] = "Settings.General.Topmost.Desc",
            ["Refresh Rate"] = "Settings.General.Refresh.Title",
            ["Set how often the monitor updates its metrics."] = "Settings.General.Refresh.Desc",
            ["Process List Sort"] = "Settings.General.Sort.Title",
            ["Default sort when opening the process list from the overlay."] = "Settings.General.Sort.Desc",
            ["Show Process Icons"] = "Settings.General.Icons.Title",
            ["Show application icons in the process list."] = "Settings.General.Icons.Desc",
            ["Overlay Click Action"] = "Settings.General.ClickMode.Title",
            ["Single click opens process list, or double-click opens Task Manager."] = "Settings.General.ClickMode.Desc",
            ["Click → Process list"] = "Settings.General.ClickMode.ProcessList",
            ["Double-click → Task Manager"] = "Settings.General.ClickMode.TaskManager",
            ["Language"] = "Settings.General.Language.Title",
            ["Interface language for settings, menus, and process list."] = "Settings.General.Language.Desc",
            ["Debug Log"] = "Settings.General.DebugLog.Title",
            ["Write diagnostic events (clicks, coordinates) to log/ folder. Off by default."] = "Settings.General.DebugLog.Desc",
            ["Clear log after"] = "Settings.General.DebugLog.Retention",
            ["1 day"] = "Settings.General.DebugLog.Day",
            ["1 week"] = "Settings.General.DebugLog.Week",
            ["1 month"] = "Settings.General.DebugLog.Month",
            ["Hardware Telemetry"] = "Settings.Monitoring.Telemetry",
            ["Processor & Memory"] = "Settings.Monitoring.CpuRam",
            ["CPU Usage"] = "Settings.Monitoring.CpuUsage",
            ["RAM Usage"] = "Settings.Monitoring.RamUsage",
            ["Graphics & Thermals"] = "Settings.Monitoring.GpuThermals",
            ["GPU Usage"] = "Settings.Monitoring.GpuUsage",
            ["GPU Temperature"] = "Settings.Monitoring.GpuTemp",
            ["Data & Connectivity"] = "Settings.Monitoring.DataConn",
            ["Upload Speed"] = "Settings.Monitoring.Upload",
            ["Download Speed"] = "Settings.Monitoring.Download",
            ["Storage Activity"] = "Settings.Monitoring.Storage",
            ["Used Space %"] = "Settings.Monitoring.UsedSpace",
            ["Real-time Activity"] = "Settings.Monitoring.Realtime",
            ["Hardware Selection"] = "Settings.Monitoring.HwSelection",
            ["Network Adapter"] = "Settings.Monitoring.NetAdapter",
            ["Graphics Card"] = "Settings.Monitoring.GpuCard",
            ["Storage Drives"] = "Settings.Monitoring.StorageDrives",
            ["Typography"] = "Settings.Appearance.Typography",
            ["Font Family"] = "Settings.Appearance.FontFamily",
            ["Display Mode"] = "Settings.Appearance.DisplayMode",
            ["Color Palette"] = "Settings.Appearance.ColorPalette",
            ["Metric Accent"] = "Settings.Appearance.MetricAccent",
            ["Select Accent"] = "Settings.Appearance.SelectAccent",
            ["Label Tone"] = "Settings.Appearance.LabelTone",
            ["Select Label"] = "Settings.Appearance.SelectLabel",
            ["Capsule Color"] = "Settings.Appearance.CapsuleColor",
            ["Select Capsule"] = "Settings.Appearance.SelectCapsule",
            ["Dashboard"] = "Settings.Appearance.Dashboard",
            ["Scaling (Size)"] = "Settings.Appearance.Scaling",
            ["Column Spacing"] = "Settings.Appearance.ColumnSpacing",
            ["Section Colors"] = "Settings.Appearance.SectionColors",
            ["Override label and metric colors per section. Leave unset to inherit global colors."] = "Settings.Appearance.SectionColors.Desc",
            ["Label"] = "Settings.Appearance.Label",
            ["Metric"] = "Settings.Appearance.Metric",
            ["Clear"] = "Settings.Appearance.Clear",
            ["Network"] = "Settings.Appearance.Network",
            ["CPU / RAM"] = "Settings.Appearance.CpuRam",
            ["GPU / Temp"] = "Settings.Appearance.GpuTemp",
            ["Disk"] = "Settings.Appearance.Disk",
            ["Reset Appearance"] = "Settings.Appearance.Reset",
            ["Kil0bit System Monitor"] = "Settings.About.AppName",
            ["A lightweight, high-performance system monitoring overlay designed for power users. Built with WPF and GDI+ rendering."] = "Settings.About.Desc",
            ["Connect with the Developer"] = "Settings.About.Connect",
            ["Built with ❤️ by KB - kil0bit"] = "Settings.About.BuiltWith",
            ["© 2026 Kil0bit System Monitor"] = "Settings.About.Copyright",
            ["Quit Application"] = "Settings.Footer.Quit",
            ["Reset All Settings"] = "Settings.Footer.ResetAll",
            ["Save & Close"] = "Settings.Footer.SaveClose",
            ["Name"] = "Settings.Sort.Name",
            ["CPU"] = "Settings.Sort.Cpu",
            ["Memory"] = "Settings.Sort.Memory",
            ["500ms (High Performance)"] = "Settings.General.Refresh.500",
            ["1000ms (Default)"] = "Settings.General.Refresh.1000",
            ["2000ms (Relaxed)"] = "Settings.General.Refresh.2000",
            ["5000ms (Power Saver)"] = "Settings.General.Refresh.5000",
            ["Default"] = "Settings.Monitoring.Default",
            ["Bold Text"] = "Settings.Appearance.BoldText",
            ["Enable Capsules"] = "Settings.Appearance.EnableCapsules",
            ["Background Plate"] = "Settings.Appearance.BackgroundPlate",
            ["Select Plate Color"] = "Settings.Appearance.SelectPlate",
            ["Text"] = "Settings.Appearance.DisplayMode.Text",
            ["Compact"] = "Settings.Appearance.DisplayMode.Compact",
            ["English"] = "Settings.General.Language.En",
            ["Русский"] = "Settings.General.Language.Ru",
        };

        // Обратный словарь: любой уже переведённый текст → ключ
        private static readonly Dictionary<string, string> AnyTextToKey = new();

        static SettingsLocalization()
        {
            foreach (var kv in TextToKey)
                AnyTextToKey[kv.Key] = kv.Value;
        }

        public static void RegisterTranslated(string key, string translated)
        {
            if (!string.IsNullOrEmpty(translated))
                AnyTextToKey[translated] = key;
        }

        public static void Apply(Window window, NavigationView? nav = null)
        {
            var L = LocalizationService.Instance;
            window.Title = L["Settings.Title"];

            if (nav != null)
            {
                foreach (var obj in nav.MenuItems)
                {
                    if (obj is not NavigationViewItem item) continue;
                    item.Content = (item.Tag as string) switch
                    {
                        "Home" => L["Settings.Nav.Home"],
                        "General" => L["Settings.Nav.General"],
                        "Monitoring" => L["Settings.Nav.Monitoring"],
                        "Appearance" => L["Settings.Nav.Appearance"],
                        "About" => L["Settings.Nav.About"],
                        _ => item.Content
                    };
                }
            }

            ApplyElement(window, L);
        }

        private static void ApplyElement(DependencyObject parent, LocalizationService L)
        {
            switch (parent)
            {
                case TextBlock tb:
                    ApplyText(tb, () => tb.Text, v => tb.Text = v, L);
                    break;
                case System.Windows.Controls.Button btn when btn.Content is string:
                    ApplyText(btn, () => btn.Content?.ToString() ?? "", v => btn.Content = v, L);
                    break;
                case HyperlinkButton hb when hb.Content is string:
                    ApplyText(hb, () => hb.Content?.ToString() ?? "", v => hb.Content = v, L);
                    break;
                case ComboBoxItem cbi:
                    ApplyText(cbi, () => cbi.Content?.ToString() ?? "", v => cbi.Content = v, L);
                    break;
                case ToggleSwitch ts:
                    if (!string.IsNullOrEmpty(ts.Header?.ToString()))
                    {
                        string header = ts.Header.ToString()!;
                        if (TryResolveKey(header, out var key))
                        {
                            string translated = L[key];
                            RegisterTranslated(key, translated);
                            ts.Header = translated;
                        }
                    }
                    break;
                case System.Windows.Controls.ComboBox cb:
                    // Items часто нет в visual tree до открытия — локализуем явно
                    foreach (var item in cb.Items)
                    {
                        if (item is DependencyObject d)
                            ApplyElement(d, L);
                    }
                    break;
            }

            if (parent is System.Windows.Controls.Button { Content: DependencyObject contentDo })
                ApplyElement(contentDo, L);

            int count = VisualTreeHelper.GetChildrenCount(parent);
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                    ApplyElement(VisualTreeHelper.GetChild(parent, i), L);
            }
            else if (parent is not System.Windows.Controls.ComboBox)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(parent))
                {
                    if (child is DependencyObject d)
                        ApplyElement(d, L);
                }
            }
        }

        private static readonly HashSet<string> PreserveTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "Name", "Cpu", "Memory", "Disk", "Network",
            "en", "ru", "Home", "General", "Monitoring", "Appearance", "About",
            "ProcessList", "TaskManager", "Day", "Week", "Month",
            "Text", "Compact", "Default",
            "NetLabel", "NetAccent", "CpuRamLabel", "CpuRamAccent",
            "GpuLabel", "GpuAccent", "DiskLabel", "DiskAccent"
        };

        private static void ApplyText(DependencyObject owner, System.Func<string> get, System.Action<string> set, LocalizationService L)
        {
            string current = get();
            if (string.IsNullOrWhiteSpace(current)) return;

            // Уже помечен ключом локализации
            if (owner is FrameworkElement fe && fe.Tag is string tag && tag.StartsWith("loc:"))
            {
                string key = tag[4..];
                string translated = L[key];
                RegisterTranslated(key, translated);
                set(translated);
                return;
            }

            // Tag используется для binding (Sort/Language) — не трогаем
            bool preserveTag = owner is FrameworkElement feTag &&
                               feTag.Tag is string existingTag &&
                               PreserveTags.Contains(existingTag);

            if (TryResolveKey(current, out var resolved))
            {
                if (owner is FrameworkElement fe2 && !preserveTag)
                    fe2.Tag = "loc:" + resolved;
                string translated = L[resolved];
                RegisterTranslated(resolved, translated);
                set(translated);
            }
        }

        private static bool TryResolveKey(string text, out string key)
        {
            if (AnyTextToKey.TryGetValue(text, out key!))
                return true;
            if (TextToKey.TryGetValue(text, out key!))
                return true;
            key = "";
            return false;
        }
    }
}
