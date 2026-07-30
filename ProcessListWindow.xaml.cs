using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace Kil0bitSystemMonitor
{
    public partial class ProcessListWindow : Window
    {
        private readonly ConfigService _config;
        private readonly ProcessListService _service = new();
        private readonly ObservableCollection<ProcessInfoItem> _items = new();
        private readonly DispatcherTimer _timer;
        private readonly IntPtr _ownerOverlay;
        private ICollectionView? _view;
        private bool _closing;
        private bool _suppressDeactivate;
        private bool _ready;
        private bool _refreshInFlight;
        private bool _repositionQueued;
        private string _sortColumn;
        private bool _sortAscending;
        private int _closeGeneration;
        private IntPtr _mouseHook;
        private LowLevelMouseProc? _mouseProc;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        public ProcessListWindow(ConfigService config, IntPtr ownerOverlay)
        {
            InitializeComponent();
            _config = config;
            _ownerOverlay = ownerOverlay;

            _sortColumn = NormalizeSort(_config.Config.ProcessListSortColumn);
            _sortAscending = _config.Config.ProcessListSortAscending;

            ProcessList.ItemsSource = _items;
            _view = CollectionViewSource.GetDefaultView(_items);
            ApplySort();
            ApplyLanguage();
            ApplyIconsVisibility();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(250, _config.Config.UpdateInterval)) };
            _timer.Tick += (_, _) => _ = RefreshSnapshotAsync();

            _config.Config.PropertyChanged += Config_PropertyChanged;
            LocalizationService.Instance.LanguageChanged += ApplyLanguage;

            SourceInitialized += (_, _) =>
            {
                var hWnd = new WindowInteropHelper(this).Handle;
                int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
                Win32Helper.SetWindowLongPtr(hWnd, Win32Helper.GWL_EXSTYLE, (IntPtr)(exStyle | 0x00000080));
                PositionAgainstOverlay();
            };

            ContentRendered += async (_, _) =>
            {
                if (_ready) return;
                PositionAgainstOverlay();
                Opacity = 1;
                _ready = true;
                InstallOutsideClickHook();
                await RefreshSnapshotAsync(prime: true);
                _timer.Start();
            };

            Closed += (_, _) =>
            {
                _closing = true;
                _closeGeneration++;
                UninstallOutsideClickHook();
                _timer.Stop();
                _config.Config.PropertyChanged -= Config_PropertyChanged;
                LocalizationService.Instance.LanguageChanged -= ApplyLanguage;
                _service.Dispose();
            };

            PositionAgainstOverlay();
        }

        private void Config_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppConfig.UpdateInterval))
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, _config.Config.UpdateInterval));
            if (e.PropertyName == nameof(AppConfig.Language))
                ApplyLanguage();
            if (e.PropertyName == nameof(AppConfig.ProcessListShowIcons))
            {
                ApplyIconsVisibility();
                if (_config.Config.ProcessListShowIcons)
                    EnsureIconsForVisibleItems();
            }
        }

        private void ApplyLanguage()
        {
            var L = LocalizationService.Instance;
            Title = L["Processes.Title"];
            TitleText.Text = L["Processes.Title"];
            CloseBtn.ToolTip = L["Processes.Close"];
            OpenTaskMgrBtnText.Text = L["Processes.OpenTaskManager"];
            OpenTaskMgrBtn.ToolTip = L["Processes.OpenTaskManager"];
            StatusText.Text = L.Format("Processes.Count", _items.Count);
            MenuEndTask.Header = L["Processes.Menu.EndTask"];
            MenuOpenLocation.Header = L["Processes.Menu.OpenLocation"];
            MenuProperties.Header = L["Processes.Menu.Properties"];
            UpdateHeaderHints();
            _view?.Refresh();
        }

        private async Task RefreshSnapshotAsync(bool prime = false)
        {
            if (_closing || _refreshInFlight) return;
            _refreshInFlight = true;
            try
            {
                if (prime)
                    await Task.Run(() => _service.Snapshot()).ConfigureAwait(true);

                var snapshot = await Task.Run(() => _service.Snapshot()).ConfigureAwait(true);
                if (_closing) return;

                var byPid = snapshot.ToDictionary(p => p.Pid);

                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (!byPid.ContainsKey(_items[i].Pid))
                        _items.RemoveAt(i);
                }

                foreach (var src in snapshot)
                {
                    var existing = _items.FirstOrDefault(x => x.Pid == src.Pid);
                    if (existing == null)
                    {
                        if (_config.Config.ProcessListShowIcons)
                            src.EnsureIcon();
                        _items.Add(src);
                    }
                    else
                    {
                        existing.Name = src.Name;
                        existing.CpuPercent = src.CpuPercent;
                        existing.MemoryMb = src.MemoryMb;
                        existing.MemoryPercent = src.MemoryPercent;
                        existing.DiskPercent = src.DiskPercent;
                        existing.NetworkKbps = src.NetworkKbps;
                        if (!string.IsNullOrEmpty(src.ExePath))
                            existing.ExePath = src.ExePath;
                        if (_config.Config.ProcessListShowIcons)
                            existing.EnsureIcon();
                    }
                }

                StatusText.Text = LocalizationService.Instance.Format("Processes.Count", _items.Count);
                _view?.Refresh();
            }
            catch { }
            finally { _refreshInFlight = false; }
        }

        private void ApplyIconsVisibility()
        {
            ColIcon.Width = _config.Config.ProcessListShowIcons ? 28 : 0;
        }

        private void EnsureIconsForVisibleItems()
        {
            foreach (var item in _items)
                item.EnsureIcon();
        }

        /// <summary>
        /// Центр по оверлею + soft-clamp в work area (низ = верх таскбара). Без «прыжка» в сторону.
        /// </summary>
        public void PositionAgainstOverlay()
        {
            if (!Win32Helper.GetWindowRect(_ownerOverlay, out Win32Helper.RECT wr))
                return;

            IntPtr hMon = MonitorFromWindow(_ownerOverlay, 1);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(hMon, ref mi))
                return;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            double dpi = 1.0;
            try
            {
                uint d = GetDpiForWindow(hwnd != IntPtr.Zero ? hwnd : _ownerOverlay);
                if (d > 0) dpi = d / 96.0;
            }
            catch { }

            const double widthDip = 620;
            const int gap = 4;
            int workLeft = mi.rcWork.Left + gap;
            int workTop = mi.rcWork.Top + gap;
            int workRight = mi.rcWork.Right - gap;
            int workBottom = mi.rcWork.Bottom - gap; // верх таскбара при нижней панели

            int workH = Math.Max(120, workBottom - workTop);
            int preferredH = Math.Max(160, (int)Math.Round(workH * 0.5));
            int popupW = Math.Max(1, (int)Math.Round(widthDip * dpi));

            int panelCx = (wr.Left + wr.Right) / 2;
            int spaceAbove = Math.Max(0, wr.Top - workTop - gap);
            int spaceBelow = Math.Max(0, workBottom - wr.Bottom - gap);

            // Предпочитаем сверху (оверлей на таскбаре); высоту режем по доступному месту
            bool placeAbove = spaceAbove >= spaceBelow || spaceAbove >= 160;
            int avail = placeAbove ? spaceAbove : spaceBelow;
            if (avail < 120)
                avail = workH;

            int popupH = Math.Min(preferredH, avail);
            popupH = Math.Max(120, popupH);

            int left = panelCx - popupW / 2;
            int maxLeft = workRight - popupW;
            if (maxLeft < workLeft) maxLeft = workLeft;
            left = SoftClamp(left, workLeft, maxLeft);

            int top = placeAbove
                ? wr.Top - popupH - gap
                : wr.Bottom + gap;

            int maxTop = workBottom - popupH;
            if (maxTop < workTop) maxTop = workTop;
            top = SoftClamp(top, workTop, maxTop);

            // Гарантия: не ниже work area (не залазим на таскбар)
            if (top + popupH > workBottom)
            {
                popupH = Math.Max(120, workBottom - top);
                if (top + popupH > workBottom)
                    top = Math.Max(workTop, workBottom - popupH);
            }

            double heightDip = popupH / dpi;
            MaxHeight = heightDip;
            Height = heightDip;
            Width = widthDip;
            ProcessList.Height = Math.Max(80, heightDip - 48);
            ProcessList.MaxHeight = ProcessList.Height;

            Left = left / dpi;
            Top = top / dpi;

            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, IntPtr.Zero, left, top, popupW, popupH,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }

            if (DebugLogger.IsEnabled)
            {
                DebugLogger.Info("ProcessList.Pos",
                    $"overlay=({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom}) popup=({left},{top}) {popupW}x{popupH} workBottom={workBottom} dpi={dpi:0.##}");
            }
        }

        /// <summary>
        /// Следование за оверлеем: не чаще одного кадра диспетчера (без backlog).
        /// </summary>
        public void RepositionFromOverlay()
        {
            if (_closing || !_ready || _repositionQueued) return;
            _repositionQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _repositionQueued = false;
                if (!_closing && _ready)
                    PositionAgainstOverlay();
            }, DispatcherPriority.Render);
        }

        private static int SoftClamp(int v, int min, int max)
        {
            if (max < min) return (min + max) / 2;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void ApplySort()
        {
            if (_view == null) return;
            _view.SortDescriptions.Clear();

            string prop = _sortColumn switch
            {
                ProcessListSortColumns.Cpu => nameof(ProcessInfoItem.CpuPercent),
                ProcessListSortColumns.Memory => nameof(ProcessInfoItem.MemoryMb),
                ProcessListSortColumns.Disk => nameof(ProcessInfoItem.DiskPercent),
                ProcessListSortColumns.Network => nameof(ProcessInfoItem.NetworkKbps),
                _ => nameof(ProcessInfoItem.Name)
            };

            var dir = _sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
            _view.SortDescriptions.Add(new SortDescription(prop, dir));
            if (prop != nameof(ProcessInfoItem.Name))
                _view.SortDescriptions.Add(new SortDescription(nameof(ProcessInfoItem.Name), ListSortDirection.Ascending));
        }

        private void PersistSort()
        {
            _config.Config.ProcessListSortColumn = _sortColumn;
            _config.Config.ProcessListSortAscending = _sortAscending;
        }

        private void UpdateHeaderHints()
        {
            var L = LocalizationService.Instance;
            SetHeader(ColName, L["Processes.Col.Name"], ProcessListSortColumns.Name);
            SetHeader(ColCpu, L["Processes.Col.Cpu"], ProcessListSortColumns.Cpu);
            SetHeader(ColMemory, L["Processes.Col.Memory"], ProcessListSortColumns.Memory);
            SetHeader(ColDisk, L["Processes.Col.Disk"], ProcessListSortColumns.Disk);
            SetHeader(ColNetwork, L["Processes.Col.Network"], ProcessListSortColumns.Network);
        }

        private void SetHeader(GridViewColumn col, string title, string key)
        {
            string mark = "";
            if (_sortColumn == key)
                mark = _sortAscending ? " ▲" : " ▼";
            col.Header = new TextBlock { Text = title + mark, Tag = key };
        }

        private void ColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not GridViewColumnHeader header) return;
            string? key = null;
            if (header.Column?.Header is FrameworkElement fe && fe.Tag is string t1)
                key = t1;
            else if (header.Content is FrameworkElement fe2 && fe2.Tag is string t2)
                key = t2;

            if (string.IsNullOrEmpty(key)) return;

            if (_sortColumn == key)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = key;
                _sortAscending = key == ProcessListSortColumns.Name;
            }

            PersistSort();
            ApplySort();
            UpdateHeaderHints();
        }

        private ProcessInfoItem? SelectedItem => ProcessList.SelectedItem as ProcessInfoItem;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => RequestClose();

        private void OpenTaskMgrBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequestClose();
                Process.Start(new ProcessStartInfo("taskmgr") { UseShellExecute = true });
            }
            catch { }
        }

        private void RequestClose()
        {
            if (_closing) return;
            _closing = true;
            Close();
        }

        private void EndTask_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedItem;
            if (item == null) return;
            try
            {
                var p = Process.GetProcessById(item.Pid);
                p.Kill(entireProcessTree: false);
                p.Dispose();
            }
            catch (Exception ex)
            {
                _suppressDeactivate = true;
                WpfMessageBox.Show(this,
                    LocalizationService.Instance.Format("Processes.Msg.EndTaskFail", ex.Message),
                    LocalizationService.Instance["Processes.Title"],
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                _suppressDeactivate = false;
            }
        }

        private void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedItem;
            if (item == null) return;
            string path = item.ExePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _suppressDeactivate = true;
                WpfMessageBox.Show(this,
                    LocalizationService.Instance["Processes.Msg.LocationUnavailable"],
                    LocalizationService.Instance["Processes.Title"],
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _suppressDeactivate = false;
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private void Properties_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedItem;
            if (item == null) return;
            string path = item.ExePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _suppressDeactivate = true;
                WpfMessageBox.Show(this,
                    LocalizationService.Instance["Processes.Msg.PropertiesUnavailable"],
                    LocalizationService.Instance["Processes.Title"],
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _suppressDeactivate = false;
                return;
            }
            try
            {
                _suppressDeactivate = true;
                ShowFileProperties(path);
                Dispatcher.BeginInvoke(() => { _suppressDeactivate = false; }, DispatcherPriority.ApplicationIdle);
            }
            catch { _suppressDeactivate = false; }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e) => _suppressDeactivate = true;
        private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(() => { _suppressDeactivate = false; }, DispatcherPriority.ApplicationIdle);
        }

        private void ProcessList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => OpenLocation_Click(sender, e);

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                RequestClose();
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (_closing || _suppressDeactivate || !_ready) return;

            // Клик по оверлею закрывает через Toggle; во время drag оверлей забирает фокус —
            // не закрываем здесь. Outside-click ловит WH_MOUSE_LL.
            if (IsCursorOverOverlay() || IsCursorOverThisWindow())
                return;

            int gen = ++_closeGeneration;
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(80);
                if (_closing || gen != _closeGeneration || _suppressDeactivate) return;
                if (!IsCursorOverOverlay() && !IsCursorOverThisWindow())
                    RequestClose();
            }, DispatcherPriority.Background);
        }

        private void InstallOutsideClickHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseProc = OutsideClickHook;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        }

        private void UninstallOutsideClickHook()
        {
            if (_mouseHook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }

        private IntPtr OutsideClickHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _ready && !_closing && !_suppressDeactivate)
            {
                int msg = unchecked((int)wParam.ToInt64());
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (!IsPointOverThisWindow(info.pt.X, info.pt.Y) &&
                        !IsPointOverOverlay(info.pt.X, info.pt.Y))
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!_closing && !_suppressDeactivate)
                                RequestClose();
                        });
                    }
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private bool IsCursorOverOverlay()
        {
            if (!Win32Helper.GetCursorPos(out Win32Helper.POINT pt)) return false;
            return IsPointOverOverlay(pt.X, pt.Y);
        }

        private bool IsCursorOverThisWindow()
        {
            if (!Win32Helper.GetCursorPos(out Win32Helper.POINT pt)) return false;
            return IsPointOverThisWindow(pt.X, pt.Y);
        }

        private bool IsPointOverOverlay(int x, int y)
        {
            if (!Win32Helper.GetWindowRect(_ownerOverlay, out Win32Helper.RECT wr)) return false;
            return x >= wr.Left && x <= wr.Right && y >= wr.Top && y <= wr.Bottom;
        }

        private bool IsPointOverThisWindow(int x, int y)
        {
            var helper = new WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) return false;
            if (!Win32Helper.GetWindowRect(helper.Handle, out Win32Helper.RECT wr)) return false;
            return x >= wr.Left && x <= wr.Right && y >= wr.Top && y <= wr.Bottom;
        }

        private static string NormalizeSort(string? value)
        {
            return value switch
            {
                ProcessListSortColumns.Cpu => ProcessListSortColumns.Cpu,
                ProcessListSortColumns.Memory => ProcessListSortColumns.Memory,
                ProcessListSortColumns.Disk => ProcessListSortColumns.Disk,
                ProcessListSortColumns.Network => ProcessListSortColumns.Network,
                _ => ProcessListSortColumns.Name
            };
        }

        private static void ShowFileProperties(string path)
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                lpVerb = "properties",
                lpFile = path,
                nShow = 5,
                fMask = 0x0000000C
            };
            ShellExecuteEx(ref info);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public Win32Helper.RECT rcMonitor;
            public Win32Helper.RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb;
            public string lpFile;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTAPI
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINTAPI pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
    }
}
