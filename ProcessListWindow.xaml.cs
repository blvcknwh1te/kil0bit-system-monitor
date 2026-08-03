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
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace Kil0bitSystemMonitor
{
    public partial class ProcessListWindow : Window
    {
        /// <summary>Как DragEaseSeconds у оверлея — коротко и без пафоса.</summary>
        private const double OpenAnimSeconds = 0.14;
        private const double OpenSlideDip = 10;

        private readonly ConfigService _config;
        private readonly ProcessListService _service = new();
        private readonly Dictionary<int, ProcessInfoItem> _processes = new();
        private readonly ObservableCollection<ProcessListRow> _rows = new();
        private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _timer;
        private readonly IntPtr _ownerOverlay;
        private readonly Action? _onOverlayPointerDown;
        private bool _closing;
        private bool _suppressDeactivate;
        private bool _ready;
        private bool _openAnimStarted;
        private bool _closeAnimStarted;
        private bool _refreshInFlight;
        private bool _repositionQueued;
        private bool _contextMenuOpen;
        private ProcessListRow? _contextRow;
        private int[] _contextPids = Array.Empty<int>();
        private string _contextExePath = "";
        private string _sortColumn;
        private bool _sortAscending;
        private int _closeGeneration;
        private IntPtr _mouseHook;
        private LowLevelMouseProc? _mouseProc;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>Идёт закрытие (в т.ч. fade-out) — toggle может форсировать reopen.</summary>
        public bool IsClosing => _closing;

        public ProcessListWindow(ConfigService config, IntPtr ownerOverlay, Action? onOverlayPointerDown = null)
        {
            InitializeComponent();
            _config = config;
            _ownerOverlay = ownerOverlay;
            _onOverlayPointerDown = onOverlayPointerDown;

            _sortColumn = NormalizeSort(_config.Config.ProcessListSortColumn);
            _sortAscending = _config.Config.ProcessListSortAscending;

            ProcessList.ItemsSource = _rows;
            ApplyLanguage();
            ApplyIconsVisibility();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(250, _config.Config.UpdateInterval)) };
            _timer.Tick += (_, _) => _ = RefreshSnapshotAsync();

            _config.Config.PropertyChanged += Config_PropertyChanged;
            LocalizationService.Instance.LanguageChanged += ApplyLanguage;

            // HWND сразу — hook и позиция до первого кадра Show()
            new WindowInteropHelper(this).EnsureHandle();
            var hWnd = new WindowInteropHelper(this).Handle;
            int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
            Win32Helper.SetWindowLongPtr(hWnd, Win32Helper.GWL_EXSTYLE, (IntPtr)(exStyle | 0x00000080));
            PositionAgainstOverlay();
            _ready = true;
            InstallOutsideClickHook();
            _timer.Start();

            SourceInitialized += (_, _) =>
            {
                PositionAgainstOverlay();
                StartOpenAnimation();
            };

            ContentRendered += async (_, _) =>
            {
                PositionAgainstOverlay();
                if (!_openAnimStarted)
                    StartOpenAnimation();
                await RefreshSnapshotAsync(prime: true);
            };

            Closed += (_, _) =>
            {
                _closing = true;
                _closeGeneration++;
                UninstallOutsideClickHook();
                _timer.Stop();
                BeginAnimation(OpacityProperty, null);
                RootSlide.BeginAnimation(TranslateTransform.YProperty, null);
                _config.Config.PropertyChanged -= Config_PropertyChanged;
                LocalizationService.Instance.LanguageChanged -= ApplyLanguage;
                _service.Dispose();
            };

            StartOpenAnimation();
        }

        private void StartOpenAnimation()
        {
            if (_openAnimStarted || _closing) return;
            _openAnimStarted = true;

            RootSlide.Y = OpenSlideDip;
            Opacity = 0;

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromSeconds(OpenAnimSeconds);

            var fade = new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            var slide = new DoubleAnimation(OpenSlideDip, 0, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };

            BeginAnimation(OpacityProperty, fade);
            RootSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void StartCloseAnimation(Action onCompleted)
        {
            if (_closeAnimStarted)
            {
                onCompleted();
                return;
            }
            _closeAnimStarted = true;

            // Снять HoldEnd от open-anim, иначе Close не сдвинет значения
            double opacityFrom = Opacity;
            double slideFrom = RootSlide.Y;
            BeginAnimation(OpacityProperty, null);
            RootSlide.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = opacityFrom;
            RootSlide.Y = slideFrom;

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var duration = TimeSpan.FromSeconds(OpenAnimSeconds);

            var fade = new DoubleAnimation(opacityFrom, 0, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            var slide = new DoubleAnimation(slideFrom, OpenSlideDip, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };

            fade.Completed += (_, _) => onCompleted();
            BeginAnimation(OpacityProperty, fade);
            RootSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        /// <summary>Закрытие с fade-out (обратный open).</summary>
        public void RequestClose()
        {
            if (_closing) return;
            _closing = true;
            _closeGeneration++;
            _timer.Stop();
            // Hook оставляем до конца анимации — повторный клик по оверлею = reopen

            StartCloseAnimation(() =>
            {
                UninstallOutsideClickHook();
                try
                {
                    if (IsLoaded)
                        Close();
                }
                catch { }
            });
        }

        /// <summary>Мгновенно закрыть без ожидания анимации (быстрый reopen).</summary>
        public void ForceCloseImmediate()
        {
            _closing = true;
            _closeGeneration++;
            UninstallOutsideClickHook();
            _timer.Stop();
            BeginAnimation(OpacityProperty, null);
            RootSlide.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 0;
            try
            {
                if (IsLoaded)
                    Close();
            }
            catch { }
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
            StatusText.Text = L.Format("Processes.Count", _processes.Count);
            MenuEndTask.Header = L["Processes.Menu.EndTask"];
            MenuOpenLocation.Header = L["Processes.Menu.OpenLocation"];
            MenuProperties.Header = L["Processes.Menu.Properties"];
            UpdateHeaderHints();
            RebuildRows();
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

                foreach (var pid in _processes.Keys.ToList())
                {
                    if (!byPid.ContainsKey(pid))
                        _processes.Remove(pid);
                }

                foreach (var src in snapshot)
                {
                    if (_processes.TryGetValue(src.Pid, out var existing))
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
                    else
                    {
                        if (_config.Config.ProcessListShowIcons)
                            src.EnsureIcon();
                        _processes[src.Pid] = src;
                    }
                }

                // Убрать expand для исчезнувших групп
                var liveNames = new HashSet<string>(
                    _processes.Values.Select(p => p.Name),
                    StringComparer.OrdinalIgnoreCase);
                _expandedGroups.RemoveWhere(k => !liveNames.Contains(k));

                StatusText.Text = LocalizationService.Instance.Format("Processes.Count", _processes.Count);
                if (!_contextMenuOpen)
                    RebuildRows();
            }
            catch { }
            finally { _refreshInFlight = false; }
        }

        private void RebuildRows()
        {
            if (_contextMenuOpen) return;

            var groups = _processes.Values
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.First().Name, Items: g.ToList()))
                .ToList();

            groups = SortGroups(groups);

            var next = new List<ProcessListRow>(groups.Count + 16);
            foreach (var g in groups)
            {
                if (g.Items.Count == 1)
                {
                    next.Add(ProcessListRow.FromSingle(g.Items[0]));
                    continue;
                }

                bool expanded = _expandedGroups.Contains(g.Name);
                next.Add(ProcessListRow.FromGroup(g.Name, g.Items, expanded));
                if (!expanded) continue;

                foreach (var child in SortProcesses(g.Items))
                    next.Add(ProcessListRow.FromChild(child));
            }

            _rows.Clear();
            foreach (var row in next)
                _rows.Add(row);
        }

        private List<(string Name, List<ProcessInfoItem> Items)> SortGroups(
            List<(string Name, List<ProcessInfoItem> Items)> groups)
        {
            float Metric(List<ProcessInfoItem> items) => _sortColumn switch
            {
                ProcessListSortColumns.Cpu => items.Sum(i => i.CpuPercent),
                ProcessListSortColumns.Memory => items.Sum(i => i.MemoryMb),
                ProcessListSortColumns.Disk => items.Sum(i => i.DiskPercent),
                ProcessListSortColumns.Network => items.Sum(i => i.NetworkKbps),
                _ => 0
            };

            if (_sortColumn == ProcessListSortColumns.Name)
            {
                return _sortAscending
                    ? groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList()
                    : groups.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }

            return _sortAscending
                ? groups.OrderBy(g => Metric(g.Items)).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : groups.OrderByDescending(g => Metric(g.Items)).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private List<ProcessInfoItem> SortProcesses(List<ProcessInfoItem> items)
        {
            IOrderedEnumerable<ProcessInfoItem> ordered = _sortColumn switch
            {
                ProcessListSortColumns.Cpu => _sortAscending
                    ? items.OrderBy(i => i.CpuPercent) : items.OrderByDescending(i => i.CpuPercent),
                ProcessListSortColumns.Memory => _sortAscending
                    ? items.OrderBy(i => i.MemoryMb) : items.OrderByDescending(i => i.MemoryMb),
                ProcessListSortColumns.Disk => _sortAscending
                    ? items.OrderBy(i => i.DiskPercent) : items.OrderByDescending(i => i.DiskPercent),
                ProcessListSortColumns.Network => _sortAscending
                    ? items.OrderBy(i => i.NetworkKbps) : items.OrderByDescending(i => i.NetworkKbps),
                _ => _sortAscending
                    ? items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    : items.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
            };
            return ordered.ThenBy(i => i.Pid).ToList();
        }

        private void ApplyIconsVisibility()
        {
            ColIcon.Width = _config.Config.ProcessListShowIcons ? 28 : 0;
        }

        private void EnsureIconsForVisibleItems()
        {
            foreach (var item in _processes.Values)
                item.EnsureIcon();
            RebuildRows();
        }

        private void Expand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ProcessListRow row || !row.IsExpandable)
                return;

            if (!_expandedGroups.Add(row.GroupKey))
                _expandedGroups.Remove(row.GroupKey);

            RebuildRows();
            e.Handled = true;
        }

        /// <summary>
        /// Центр по оверлею + soft-clamp в work area (низ = верх таскбара). Без «прыжка» в сторону.
        /// </summary>
        public void PositionAgainstOverlay()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (!OverlayPopupLayout.TryCompute(_ownerOverlay, hwnd, out var p))
                return;

            MaxHeight = p.HeightDip;
            Height = p.HeightDip;
            Width = p.WidthDip;
            ProcessList.Height = Math.Max(80, p.HeightDip - 48);
            ProcessList.MaxHeight = ProcessList.Height;

            Left = p.Left / p.DpiScale;
            Top = p.Top / p.DpiScale;

            if (hwnd != IntPtr.Zero)
            {
                Win32Helper.SetWindowPos(hwnd, IntPtr.Zero, p.Left, p.Top, p.WidthPx, p.HeightPx,
                    Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);
            }

            if (DebugLogger.IsEnabled && Win32Helper.GetWindowRect(_ownerOverlay, out Win32Helper.RECT wr))
            {
                DebugLogger.Info("ProcessList.Pos",
                    $"overlay=({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom}) popup=({p.Left},{p.Top}) {p.WidthPx}x{p.HeightPx} dpi={p.DpiScale:0.##}");
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

        private void ApplySort()
        {
            RebuildRows();
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

        private ProcessListRow? SelectedRow => ProcessList.SelectedItem as ProcessListRow;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => RequestClose();

        private void OpenTaskMgrBtn_Click(object sender, RoutedEventArgs e)
        {
            ForceCloseImmediate();
            TaskManagerLauncher.OpenNearOverlay(_ownerOverlay);
        }

        private void CaptureContext(ProcessListRow? row)
        {
            _contextRow = row;
            if (row == null)
            {
                _contextPids = Array.Empty<int>();
                _contextExePath = "";
                return;
            }

            _contextPids = row.Members.Select(m => m.Pid).Distinct().ToArray();
            _contextExePath = !string.IsNullOrWhiteSpace(row.ExePath)
                ? row.ExePath
                : row.Members.Select(m => m.ExePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
        }

        private ProcessListRow? RowFromSource(DependencyObject? src)
        {
            if (src == null) return ProcessList.SelectedItem as ProcessListRow;

            var item = ItemsControl.ContainerFromElement(ProcessList, src) as System.Windows.Controls.ListViewItem
                       ?? FindAncestor<System.Windows.Controls.ListViewItem>(src);
            if (item != null)
            {
                item.IsSelected = true;
                return item.DataContext as ProcessListRow;
            }

            return ProcessList.SelectedItem as ProcessListRow;
        }

        private void EndTask_Click(object sender, RoutedEventArgs e)
        {
            var pids = _contextPids;
            if (pids.Length == 0) return;

            foreach (var pid in pids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
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
                    break;
                }
            }
        }

        private void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            string path = _contextExePath;
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
            string path = _contextExePath;
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

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            _suppressDeactivate = true;
            _contextMenuOpen = true;
        }

        private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            _contextMenuOpen = false;
            Dispatcher.BeginInvoke(() =>
            {
                _suppressDeactivate = false;
                if (!_closing)
                    RebuildRows();
            }, DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// Меню открываем сами: у ListViewItem нет ContextMenu, шаринг одного меню по Style ломает Click.
        /// </summary>
        private void ProcessList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            _suppressDeactivate = true;
            CaptureContext(RowFromSource(e.OriginalSource as DependencyObject));
            if (_contextPids.Length == 0) return;

            var menu = RowContextMenu;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.PlacementTarget = ProcessList;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
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
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing || gen != _closeGeneration || _suppressDeactivate) return;
                if (!IsCursorOverOverlay() && !IsCursorOverThisWindow())
                    RequestClose();
            }, DispatcherPriority.Input);
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
            // Hook живёт и во время close-anim: клик по оверлею = мгновенный reopen-сигнал через Toggle на UP.
            // Закрытие по оверлею — на DOWN, пока ещё не _closing.
            if (nCode < 0 || !_ready || _suppressDeactivate || _contextMenuOpen)
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            int msg = unchecked((int)wParam.ToInt64());
            if (msg != WM_LBUTTONDOWN)
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int x = info.pt.X;
            int y = info.pt.Y;

            if (IsPointOverPopupMenu(x, y) || IsPointOverThisWindow(x, y))
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            if (IsPointOverOverlay(x, y))
            {
                // Весь toggle по оверлею — на DOWN (close или reopen), UP подавляется флагом
                try { _onOverlayPointerDown?.Invoke(); } catch { }
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            if (!_closing)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_closing && !_suppressDeactivate && !_contextMenuOpen)
                        RequestClose();
                }, DispatcherPriority.Send);
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static bool IsPointOverPopupMenu(int x, int y)
        {
            IntPtr hwnd = WindowFromPoint(new POINTAPI { X = x, Y = y });
            if (hwnd == IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(64);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 && sb.ToString() == "#32768";
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
        private static extern IntPtr WindowFromPoint(POINTAPI pt);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

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
