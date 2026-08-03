using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor
{
    public class OverlayWindow : IDisposable
    {
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private readonly WndProcDelegate _wndProc = null!;
        private IntPtr _hWnd;
        private IntPtr _hIcon;

        private readonly MainViewModel _viewModel = null!;
        private readonly ConfigService _config = null!;
        private readonly TelemetryService _telemetry = null!;
        private readonly System.Windows.Threading.Dispatcher _dispatcher = null!;
        private readonly System.Threading.Timer _zOrderTimer = null!;

        private bool _isHovered = false;
        private bool _trackingMouse = false;
        private bool _shellFullscreen = false;
        private bool _appbarRegistered = false;
        private bool _disposing = false;
        private bool _recreatingHwnd = false;
        private readonly Action<SystemMetrics>? _onMetricsUpdated;
        private readonly System.ComponentModel.PropertyChangedEventHandler? _onConfigPropertyChanged;
        private uint _currentDpi = 96;
        private float _dpiScale = 1.0f;

        // Click vs drag
        private bool _lButtonDown = false;
        private bool _lButtonDragged = false;
        private int _lButtonDownScreenX;
        private int _lButtonDownScreenY;
        private long _lastDragEndTick;
        private const int DragThresholdPx = 8;
        private const int PostDragClickGuardMs = 120;
        // Свой drag + ease ~0.Xs (вместо HTCAPTION)
        private int _dragOffsetX;
        private int _dragOffsetY;
        private double _dragPosX;
        private double _dragPosY;
        private double _dragTargetX;
        private double _dragTargetY;
        private long _dragAnimTick;
        private System.Windows.Threading.DispatcherTimer? _dragAnimTimer;
        private const double DragEaseSeconds = 0.13;
        private const double DragEaseTau = DragEaseSeconds / 3.0;
        private ProcessListWindow? _processListWindow;

        // Visibility / fade state
        private byte _currentAlpha = 255;
        private byte _targetAlpha = 255;
        private bool _overlayVisible = true;
        private System.Windows.Threading.DispatcherTimer? _fadeTimer;
        private System.Windows.Threading.DispatcherTimer? _hideDebounceTimer;

        private readonly System.Collections.Generic.Dictionary<string, Font> _fontCache = new();
        private readonly System.Collections.Generic.Dictionary<string, float> _measureCache = new();
        private Brush? _cachedBgBrush;
        private Brush? _cachedAccentBrush;
        private Brush? _cachedLabelBrush;
        private Pen? _cachedHoverPen;
        private Brush? _cachedHoverBrush;
        private Brush? _cachedPodBrush;
        // Per-section label brushes (null = use _cachedLabelBrush)
        private Brush? _cachedNetLabelBrush;
        private Brush? _cachedCpuRamLabelBrush;
        private Brush? _cachedGpuLabelBrush;
        private Brush? _cachedDiskLabelBrush;
        // Per-section accent/metric brushes (null = use _cachedAccentBrush)
        private Brush? _cachedNetAccentBrush;
        private Brush? _cachedCpuRamAccentBrush;
        private Brush? _cachedGpuAccentBrush;
        private Brush? _cachedDiskAccentBrush;
        private Bitmap? _offscreenBitmap;
        private Graphics? _offscreenGraphics;

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint WS_POPUP = 0x80000000;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_WINDOWPOSCHANGED = 0x0047;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const uint TME_LEAVE = 0x00000002;
        public const int WM_SETICON = 0x0080;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL = 0;
        public const int WM_SHOW_SETTINGS = 0x0501;
        private const uint WM_APPBAR_CALLBACK = 0x0502;
        private const uint ABM_NEW = 0x00000000;
        private const uint ABM_REMOVE = 0x00000001;
        private const uint ABN_FULLSCREENAPP = 0x00000002;
        private const uint ABM_WINDOWPOSCHANGED = 0x00000009;
        private const uint GW_HWNDPREV = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS { public IntPtr hwnd; public IntPtr hwndInsertAfter; public int x; public int y; public int cx; public int cy; public uint flags; }

        public OverlayWindow(MainViewModel viewModel, ConfigService config, TelemetryService telemetry)
        {
            try
            {
                _viewModel = viewModel;
                _config = config;
                _telemetry = telemetry;
                _dispatcher = System.Windows.Application.Current.Dispatcher;
                _wndProc = WndProc;

                WNDCLASSEX wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.style = 0x0008;
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
                wc.hInstance = GetModuleHandle(null);
                wc.lpszClassName = "Kil0bitOverlayWndClass_Main";
                wc.hCursor = LoadCursor(IntPtr.Zero, 32512);

                try
                {
                    string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.png");
                    if (System.IO.File.Exists(iconPath))
                    {
                        using (var bmp = new System.Drawing.Bitmap(iconPath)) _hIcon = bmp.GetHicon();
                    }
                }
                catch { }

                wc.hIcon = _hIcon;
                wc.hIconSm = _hIcon;
                RegisterClassEx(ref wc);

                int x = (int)_config.Config.X;
                int y = (int)_config.Config.Y;
                if (x < -10000 || x > 10000 || y < -10000 || y > 10000) { x = 100; y = 100; }

                _hWnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "Kil0bitOverlayWndClass_Main", "Kil0bit System Monitor Overlay", WS_POPUP, x, y, 300, 32, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hWnd == IntPtr.Zero) throw new Exception("Failed to create window");

                if (_hIcon != IntPtr.Zero) { SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_BIG, _hIcon); SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_SMALL, _hIcon); }

                _currentDpi = GetDpiForWindow(_hWnd);
                if (_currentDpi == 0) _currentDpi = 96;
                _dpiScale = _currentDpi / 96.0f;

                // Disable DWM animations to prevent flickering during Task View zoom transitions
                int disableTransitions = 1;
                Win32Helper.DwmSetWindowAttribute(_hWnd, 3, ref disableTransitions, sizeof(int));

                if (_config.Config.StickToTaskbar)
                    AttachToTaskbar();
                else
                    AlignToTaskbarCenter();
                ShowWindow(_hWnd, 5);
                UpdateCachedColors();
                UpdateLayer();
                RegisterShellHook();

                _onMetricsUpdated = (m) => {
                    _dispatcher.BeginInvoke(() => {
                        try
                        {
                            if (!EnsureOverlayHwndAlive()) return;
                            _viewModel.Metrics = m;
                            if (_targetAlpha > 0 || _currentAlpha > 0) UpdateLayer();
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.Error("Overlay.Metrics", ex.ToString());
                        }
                    });
                };
                _telemetry.MetricsUpdated += _onMetricsUpdated;
                _zOrderTimer = new System.Threading.Timer(EnforceZOrder, null, 0, 500);

                _onConfigPropertyChanged = (s, e) => {
                    _dispatcher.BeginInvoke(() => {
                        if (!EnsureOverlayHwndAlive()) return;
                        if (e.PropertyName == nameof(_config.Config.AccentColorHex) || e.PropertyName == nameof(_config.Config.LabelColorHex) || e.PropertyName == nameof(_config.Config.BackgroundColorHex) || e.PropertyName == nameof(_config.Config.PodColorHex) || e.PropertyName == nameof(_config.Config.FontFamily)
                            || e.PropertyName == nameof(_config.Config.NetLabelColorHex) || e.PropertyName == nameof(_config.Config.CpuRamLabelColorHex) || e.PropertyName == nameof(_config.Config.GpuLabelColorHex) || e.PropertyName == nameof(_config.Config.DiskLabelColorHex)
                            || e.PropertyName == nameof(_config.Config.NetAccentColorHex) || e.PropertyName == nameof(_config.Config.CpuRamAccentColorHex) || e.PropertyName == nameof(_config.Config.GpuAccentColorHex) || e.PropertyName == nameof(_config.Config.DiskAccentColorHex))
                        {
                            ClearCaches();
                            UpdateCachedColors();
                        }
                        if (e.PropertyName == nameof(_config.Config.StickToTaskbar))
                        {
                            if (_config.Config.StickToTaskbar)
                            {
                                AttachToTaskbar();
                            }
                            else
                            {
                                _attachedTaskbar = IntPtr.Zero;
                                UnregisterAppBar();
                                AlignToTaskbarCenter();
                            }
                        }
                        if (e.PropertyName == nameof(_config.Config.ShowOverlay) || e.PropertyName == nameof(_config.Config.HideOnFullscreen) || e.PropertyName == nameof(_config.Config.StickToTaskbar) || e.PropertyName == nameof(_config.Config.ShowPods) || e.PropertyName == nameof(_config.Config.ShowBackground) || e.PropertyName == nameof(_config.Config.AlwaysOnTop))
                        {
                            UpdateVisibility();
                            IntPtr zOrder = _config.Config.AlwaysOnTop ? Win32Helper.HWND_TOPMOST : Win32Helper.HWND_NOTOPMOST;
                            SetWindowPos(_hWnd, zOrder, 0, 0, 0, 0, Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | 0x0040);
                        }
                        UpdateLayer();
                    });
                };
                _config.Config.PropertyChanged += _onConfigPropertyChanged;
                if (!_config.Config.ShowOverlay) { ShowWindow(_hWnd, 0); _overlayVisible = false; _currentAlpha = 0; _targetAlpha = 0; }
            }
            catch { throw; }
        }

        private void EnforceZOrder(object? state)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposing) return;
                if (!EnsureOverlayHwndAlive()) return;

                bool show = ShouldShowOverlay();

                if (show)
                {
                    _hideDebounceTimer?.Stop();
                    if (_targetAlpha != 255) { _targetAlpha = 255; StartFade(); }
                }
                else
                {
                    // Debounce hide by 800ms to prevent flickering during shell animations
                    if (_hideDebounceTimer == null)
                    {
                        _hideDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                        _hideDebounceTimer.Tick += (s, e) => { _hideDebounceTimer.Stop(); if (!ShouldShowOverlay()) { _targetAlpha = 0; StartFade(); } };
                    }
                    if (!_hideDebounceTimer.IsEnabled && _targetAlpha != 0) _hideDebounceTimer.Start();
                }

                if (!_overlayVisible) return;
                ReassertZOrder();
            });
        }

        /// <summary>
        /// При клике по таскбару shell поднимает его над TOPMOST — HWND_TOPMOST в ответ даёт мигание.
        /// Ставим оверлей сразу над таскбаром в той же topmost-ленте.
        /// </summary>
        private void ReassertZOrder()
        {
            if (_disposing || !_overlayVisible || _hWnd == IntPtr.Zero || !IsWindow(_hWnd))
                return;

            IntPtr fg = GetForegroundWindow();
            var sb = new StringBuilder(256);
            Win32Helper.GetClassName(fg, sb, sb.Capacity);
            string fgClass = sb.ToString();
            bool taskbarFg = fgClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

            if (_config.Config.AlwaysOnTop)
            {
                long ex = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
                bool isTopMost = (ex & Win32Helper.WS_EX_TOPMOST) != 0;
                IntPtr prev = GetWindow(_hWnd, GW_HWNDPREV);
                bool coveredByTaskbar = false;
                IntPtr coveringTaskbar = IntPtr.Zero;
                if (prev != IntPtr.Zero)
                {
                    sb.Clear();
                    Win32Helper.GetClassName(prev, sb, sb.Capacity);
                    string prevClass = sb.ToString();
                    if (prevClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                    {
                        coveredByTaskbar = true;
                        coveringTaskbar = prev;
                    }
                }

                // Не HWND_TOPMOST при конфликте с таскбаром — только вставка над ним.
                if (taskbarFg || coveredByTaskbar)
                {
                    if (!isTopMost)
                    {
                        SetWindowPos(_hWnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0,
                            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
                    }
                    IntPtr taskbar = taskbarFg ? fg : coveringTaskbar;
                    if (taskbar == IntPtr.Zero || !IsWindow(taskbar))
                        taskbar = coveringTaskbar != IntPtr.Zero ? coveringTaskbar : fg;
                    KeepAboveTaskbar(taskbar);
                    return;
                }

                if (!isTopMost)
                {
                    SetWindowPos(_hWnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0,
                        Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
                }
                return;
            }

            if (!_config.Config.StickToTaskbar) return;

            IntPtr stickBar = _attachedTaskbar;
            if (taskbarFg)
                stickBar = fg;

            if (stickBar == IntPtr.Zero || !IsWindow(stickBar))
            {
                if (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT cur))
                    stickBar = ResolveTaskbarForPoint(cur.Left + cur.Width / 2, cur.Top + cur.Height / 2);
            }
            if (stickBar != IntPtr.Zero && IsWindow(stickBar))
                KeepAboveTaskbar(stickBar);
        }

        /// <summary>
        /// Ставит оверлей сразу над указанным таскбаром в Z-order (режим без AlwaysOnTop).
        /// </summary>
        private void KeepAboveTaskbar(IntPtr taskbar)
        {
            if (taskbar == IntPtr.Zero || !IsWindow(taskbar)) return;
            IntPtr above = GetWindow(taskbar, GW_HWNDPREV);
            if (above == _hWnd) return;
            if (above != IntPtr.Zero)
            {
                SetWindowPos(_hWnd, above, 0, 0, 0, 0,
                    Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
            }
            else
            {
                SetWindowPos(_hWnd, IntPtr.Zero, 0, 0, 0, 0,
                    Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
            }
        }

        /// <summary>
        /// HWND оверлея не владеет таскбаром: если shell всё же уничтожил окно — пересоздаём.
        /// </summary>
        private bool EnsureOverlayHwndAlive()
        {
            if (_disposing) return false;
            if (_hWnd != IntPtr.Zero && IsWindow(_hWnd)) return true;
            if (_recreatingHwnd) return false;

            _recreatingHwnd = true;
            try
            {
                DebugLogger.Warn("Overlay", $"HWND lost (was 0x{_hWnd.ToInt64():X}), recreating");
                _hWnd = IntPtr.Zero;
                _attachedTaskbar = IntPtr.Zero;
                _appbarRegistered = false;
                _shellHookRegistered = false;
                _overlayVisible = false;

                int x = (int)_config.Config.X;
                int y = (int)_config.Config.Y;
                if (x < -10000 || x > 10000 || y < -10000 || y > 10000) { x = 100; y = 100; }

                IntPtr hInstance = GetModuleHandle(null);
                _hWnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "Kil0bitOverlayWndClass_Main", "Kil0bit System Monitor Overlay", WS_POPUP, x, y, 300, 32, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (_hWnd == IntPtr.Zero)
                {
                    DebugLogger.Error("Overlay", "Failed to recreate overlay HWND");
                    return false;
                }

                if (_hIcon != IntPtr.Zero)
                {
                    SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_BIG, _hIcon);
                    SendMessage(_hWnd, WM_SETICON, (IntPtr)ICON_SMALL, _hIcon);
                }

                _currentDpi = GetDpiForWindow(_hWnd);
                if (_currentDpi == 0) _currentDpi = 96;
                _dpiScale = _currentDpi / 96.0f;

                int disableTransitions = 1;
                Win32Helper.DwmSetWindowAttribute(_hWnd, 3, ref disableTransitions, sizeof(int));

                if (_config.Config.StickToTaskbar)
                    AttachToTaskbar();
                else
                    AlignToTaskbarCenter();

                RegisterShellHook();
                _currentAlpha = 0;
                _targetAlpha = _config.Config.ShowOverlay ? (byte)255 : (byte)0;
                if (_targetAlpha == 255)
                {
                    ShowWindow(_hWnd, 5);
                    _overlayVisible = true;
                    _currentAlpha = 255;
                    UpdateLayer();
                }

                DebugLogger.Info("Overlay", $"HWND recreated 0x{_hWnd.ToInt64():X}");
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("Overlay", $"Recreate failed: {ex}");
                return false;
            }
            finally
            {
                _recreatingHwnd = false;
            }
        }

        private void RegisterAppBar() { if (_appbarRegistered || _hWnd == IntPtr.Zero) return; APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd, uCallbackMessage = WM_APPBAR_CALLBACK }; SHAppBarMessage(ABM_NEW, ref abd); _appbarRegistered = true; }
        private void UnregisterAppBar() { if (!_appbarRegistered || _hWnd == IntPtr.Zero) return; APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd }; SHAppBarMessage(ABM_REMOVE, ref abd); _appbarRegistered = false; }
        private void UpdateVisibility()
        {
            bool show = ShouldShowOverlay();
            if (show)
            {
                _hideDebounceTimer?.Stop();
                _targetAlpha = 255;
                StartFade();
            }
            else
            {
                if (_hideDebounceTimer == null)
                {
                    _hideDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _hideDebounceTimer.Tick += (s, e) => { _hideDebounceTimer.Stop(); if (!ShouldShowOverlay()) { _targetAlpha = 0; StartFade(); } };
                }
                if (!_hideDebounceTimer.IsEnabled && _targetAlpha != 0) _hideDebounceTimer.Start();
            }
        }

        // Starts the fade timer if not already running.
        private void StartFade()
        {
            if (_fadeTimer == null)
            {
                _fadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _fadeTimer.Tick += (s, e) => FadeTick();
            }
            if (!_fadeTimer.IsEnabled) _fadeTimer.Start();
        }

        // Steps _currentAlpha toward _targetAlpha at ~150ms for a full 0↔255 transition.
        private void FadeTick()
        {
            const int step = 30; // 255/30 ≈ 9 frames × 16ms ≈ 144ms
            if (_currentAlpha < _targetAlpha)
            {
                // Fading in — make sure window is shown before first pixel appears
                if (!_overlayVisible) { ShowWindow(_hWnd, 5); _overlayVisible = true; }
                _currentAlpha = (byte)Math.Min(255, _currentAlpha + step);
            }
            else if (_currentAlpha > _targetAlpha)
            {
                _currentAlpha = (byte)Math.Max(0, _currentAlpha - step);
            }

            // Reblit the existing bitmap with the new alpha — no re-render needed
            if (_offscreenBitmap != null) SetBitmap(_offscreenBitmap);

            if (_currentAlpha == _targetAlpha)
            {
                _fadeTimer!.Stop();
                // Only call ShowWindow(0) once we are fully transparent to avoid blink
                if (_currentAlpha == 0 && _overlayVisible) { ShowWindow(_hWnd, 0); _overlayVisible = false; }
            }
        }

        private bool ShouldShowOverlay()
        {
            if (!_config.Config.ShowOverlay) return false;

            if (_config.Config.HideOnFullscreen)
            {
                IntPtr fg = GetForegroundWindow();
                if (IsShellWindow(fg)) return true;

                // Наш popup процессов не должен прятать оверлей
                if (_processListWindow != null && _processListWindow.IsVisible)
                    return true;

                if (_shellFullscreen) return false;

                IntPtr taskbarHwnd = Win32Helper.FindWindow("Shell_TrayWnd", null!);
                if (taskbarHwnd != IntPtr.Zero && Win32Helper.GetWindowRect(taskbarHwnd, out Win32Helper.RECT tbRect))
                {
                    int tw = tbRect.Right - tbRect.Left;
                    int th = tbRect.Bottom - tbRect.Top;
                    // Игнор мигающих нулевых размеров при анимациях shell
                    if ((th <= 4 || tw <= 4) && tw + th > 0)
                        return false;
                }

                if (fg != IntPtr.Zero && fg != _hWnd)
                {
                    const long WS_CAPTION = 0x00C00000L;
                    const long WS_THICKFRAME = 0x00040000L;
                    long style = Win32Helper.GetWindowLong(fg, Win32Helper.GWL_STYLE);
                    bool hasCaption = (style & WS_CAPTION) != 0;
                    bool hasFrame = (style & WS_THICKFRAME) != 0;

                    // Только реально безрамочные fullscreen-кандидаты (не обычные окна IDE/браузера)
                    if (!hasCaption && !hasFrame && Win32Helper.GetWindowRect(fg, out Win32Helper.RECT fgRect))
                    {
                        IntPtr hMon = MonitorFromWindow(fg, 1);
                        MONITORINFO mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                        if (GetMonitorInfo(hMon, ref mi))
                        {
                            var s = mi.rcMonitor;
                            if (fgRect.Left <= s.Left && fgRect.Top <= s.Top &&
                                fgRect.Right >= s.Right && fgRect.Bottom >= s.Bottom)
                            {
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }

        private bool IsShellWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            StringBuilder sb = new StringBuilder(256);
            Win32Helper.GetClassName(hWnd, sb, sb.Capacity);
            string cls = sb.ToString();

            // Core Windows Shell and UWP overlay window classes
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                cls == "MultitaskingViewFrame" || cls == "TaskView" || cls == "Windows.UI.Core.CoreWindow" ||
                cls == "XamlExplorerViewHostWindow" || cls == "DesktopWindowXamlSource" ||
                cls == "Windows.UI.Input.InputSite.WindowClass" || cls == "PopupHost")
                return true;

            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != 0)
                {
                    IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hProcess != IntPtr.Zero)
                    {
                        try
                        {
                            uint size = 1024;
                            StringBuilder buffer = new StringBuilder((int)size);
                            if (QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                            {
                                string fullPath = buffer.ToString();
                                string pname = System.IO.Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant();
                                return (pname == "explorer" || pname == "shellexperiencehost" ||
                                        pname == "startmenuexperiencehost" || pname == "searchhost" ||
                                        pname == "dwm");
                            }
                        }
                        finally
                        {
                            CloseHandle(hProcess);
                        }
                    }
                    else
                    {
                        // OpenProcess failed (Access Denied / protected process).
                        // Highly protected UWP/system processes (like StartMenuExperienceHost or SYSTEM processes)
                        // are definitely shell/system windows, not standard user apps or games.
                        if (Marshal.GetLastWin32Error() == 5) // ERROR_ACCESS_DENIED
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (int IconRight, int TrayLeft)> _taskbarBounds = new();
        private IntPtr _attachedTaskbar = IntPtr.Zero;
        private int _taskbarBoundsRefreshGen;
        private bool _shellHookRegistered;
        private uint _wmShellHook;

        private const int HSHELL_WINDOWCREATED = 1;
        private const int HSHELL_WINDOWDESTROYED = 2;
        private const int HSHELL_WINDOWACTIVATED = 4;
        private const int HSHELL_WINDOWREPLACED = 13;
        private const int HSHELL_WINDOWREPLACING = 14;
        private const uint MONITOR_DEFAULTTONULL = 0;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private void AlignToTaskbarCenter()
        {
            if (!_config.Config.StickToTaskbar)
            {
                SetWindowPos(_hWnd, IntPtr.Zero, (int)_config.Config.X, (int)_config.Config.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
                return;
            }

            int refX = (int)_config.Config.X;
            int refY = (int)_config.Config.Y;
            int overlayW = 200;
            if (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT cur))
            {
                refX = cur.Left + cur.Width / 2;
                refY = cur.Top + cur.Height / 2;
                if (cur.Width > 0) overlayW = cur.Width;
            }

            IntPtr taskbar = ResolveTaskbarForPoint(refX, refY);
            if (taskbar == IntPtr.Zero || !Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb))
                return;

            EnsureAttachedToTaskbar(taskbar);
            int h = tb.Bottom - tb.Top;
            int oh = (int)((_config.Config.ShowPods ? 36 : 32) * _dpiScale * (float)_config.Config.ScaleFactor);
            int cy = tb.Top + (h - oh) / 2;
            int x = (int)_config.Config.X;
            if (TryGetTaskbarDragRange(taskbar, tb, overlayW, out int minX, out int maxX))
                x = Math.Max(minX, Math.Min(maxX, x));
            SetWindowPos(_hWnd, IntPtr.Zero, x, cy, 0, 0, 0x0001 | 0x0004 | 0x0010);
            _config.Config.X = x;
            _config.Config.Y = cy;
        }

        private void BeginCustomDrag()
        {
            if (!Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT r))
                return;

            _dragOffsetX = _lButtonDownScreenX - r.Left;
            _dragOffsetY = _lButtonDownScreenY - r.Top;
            _dragPosX = r.Left;
            _dragPosY = r.Top;
            UpdateDragTargetFromCursor();
            _dragAnimTick = Environment.TickCount64;

            if (_dragAnimTimer == null)
            {
                _dragAnimTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Render, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(10)
                };
                _dragAnimTimer.Tick += (_, _) => TickDragAnimation();
            }

            if (!_dragAnimTimer.IsEnabled)
                _dragAnimTimer.Start();
        }

        private void UpdateDragTargetFromCursor()
        {
            if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cur))
                return;

            double tx = cur.X - _dragOffsetX;
            double ty = cur.Y - _dragOffsetY;

            if (_config.Config.StickToTaskbar)
            {
                int overlayW = Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT wr) ? wr.Width : 200;
                IntPtr taskbar = ResolveTaskbarForPoint(cur.X, cur.Y);
                if (taskbar != IntPtr.Zero && Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb))
                {
                    int oh = (int)((_config.Config.ShowPods ? 36 : 32) * _dpiScale * (float)_config.Config.ScaleFactor);
                    ty = tb.Top + (tb.Bottom - tb.Top - oh) / 2.0;
                    if (TryGetTaskbarDragRange(taskbar, tb, overlayW, out int minX, out int maxX))
                        tx = Math.Max(minX, Math.Min(maxX, tx));
                }
            }

            _dragTargetX = tx;
            _dragTargetY = ty;
        }

        private void TickDragAnimation()
        {
            if (!_lButtonDragged || _hWnd == IntPtr.Zero)
            {
                _dragAnimTimer?.Stop();
                return;
            }

            UpdateDragTargetFromCursor();

            long now = Environment.TickCount64;
            double dt = Math.Clamp((now - _dragAnimTick) / 1000.0, 0.001, 0.05);
            _dragAnimTick = now;

            double t = 1.0 - Math.Exp(-dt / DragEaseTau);
            _dragPosX += (_dragTargetX - _dragPosX) * t;
            _dragPosY += (_dragTargetY - _dragPosY) * t;

            int x = (int)Math.Round(_dragPosX);
            int y = (int)Math.Round(_dragPosY);
            SetWindowPos(_hWnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }

        private void EndCustomDrag()
        {
            _dragAnimTimer?.Stop();
            _lastDragEndTick = Environment.TickCount64;

            UpdateDragTargetFromCursor();
            int x = (int)Math.Round(_dragTargetX);
            int y = (int)Math.Round(_dragTargetY);

            if (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT r))
            {
                if (_config.Config.StickToTaskbar)
                {
                    IntPtr taskbar = ResolveTaskbarForPoint(r.Left + r.Width / 2, r.Top + r.Height / 2);
                    if (taskbar == IntPtr.Zero && Win32Helper.GetCursorPos(out Win32Helper.POINT c))
                        taskbar = ResolveTaskbarForPoint(c.X, c.Y);

                    if (taskbar != IntPtr.Zero && Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb) &&
                        TryGetTaskbarDragRange(taskbar, tb, r.Width, out int minX, out int maxX))
                    {
                        EnsureAttachedToTaskbar(taskbar);
                        int oh = (int)((_config.Config.ShowPods ? 36 : 32) * _dpiScale * (float)_config.Config.ScaleFactor);
                        y = tb.Top + (tb.Bottom - tb.Top - oh) / 2;
                        x = Math.Max(minX, Math.Min(maxX, x));
                    }
                }
            }

            _dragPosX = x;
            _dragPosY = y;
            SetWindowPos(_hWnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            _config.Config.X = x;
            _config.Config.Y = y;
            _config.SaveConfig();
        }

        private void AttachToTaskbar()
        {
            int refX = (int)_config.Config.X;
            int refY = (int)_config.Config.Y;
            if (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT cur))
            {
                refX = cur.Left + cur.Width / 2;
                refY = cur.Top + cur.Height / 2;
            }

            IntPtr taskbarHwnd = ResolveTaskbarForPoint(refX, refY);

            if (taskbarHwnd != IntPtr.Zero)
            {
                EnsureAttachedToTaskbar(taskbarHwnd);
                RegisterAppBar();
                ScheduleTaskbarBoundsRefresh();
                AlignToTaskbarCenter();
            }
        }

        private void EnsureAttachedToTaskbar(IntPtr taskbar)
        {
            // Не ставим GWL_HWNDPARENT на Shell_TrayWnd: при пересоздании explorer/secondary tray
            // Windows уничтожает owned-окна — оверлей «сам закрывается».
            if (taskbar == IntPtr.Zero || taskbar == _attachedTaskbar) return;
            _attachedTaskbar = taskbar;
        }

        /// <summary>
        /// Таскбар монитора под точкой (primary Shell_TrayWnd или Shell_SecondaryTrayWnd).
        /// </summary>
        private IntPtr ResolveTaskbarForPoint(int x, int y)
        {
            var pt = new POINT { x = x, y = y };
            IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            return FindTaskbarForMonitor(hMon);
        }

        private IntPtr FindTaskbarForMonitor(IntPtr hMon)
        {
            IntPtr primary = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (hMon == IntPtr.Zero)
                return primary;

            if (primary != IntPtr.Zero && MonitorFromWindow(primary, MONITOR_DEFAULTTONULL) == hMon)
                return primary;

            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                var cls = new StringBuilder(64);
                Win32Helper.GetClassName(hwnd, cls, cls.Capacity);
                if (!string.Equals(cls.ToString(), "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
                    return true;
                if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) == hMon)
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            // Не подставлять primary: иначе оверлей на secondary уезжает на первый экран
            return found;
        }

        private System.Collections.Generic.List<IntPtr> EnumerateTaskbars()
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            IntPtr primary = Win32Helper.FindWindow("Shell_TrayWnd", null!);
            if (primary != IntPtr.Zero) list.Add(primary);

            EnumWindows((hwnd, _) =>
            {
                var cls = new StringBuilder(64);
                Win32Helper.GetClassName(hwnd, cls, cls.Capacity);
                if (string.Equals(cls.ToString(), "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
                    list.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            return list;
        }

        private void RegisterShellHook()
        {
            try
            {
                _wmShellHook = RegisterWindowMessage("SHELLHOOK");
                if (_wmShellHook != 0 && RegisterShellHookWindow(_hWnd))
                    _shellHookRegistered = true;
            }
            catch { }
            RefreshTaskbarBounds();
        }

        private void UnregisterShellHook()
        {
            if (!_shellHookRegistered || _hWnd == IntPtr.Zero) return;
            try { DeregisterShellHookWindow(_hWnd); } catch { }
            _shellHookRegistered = false;
        }

        private void OnShellHook(IntPtr wParam)
        {
            int code = unchecked((int)(wParam.ToInt64() & 0x7FFF));
            // Без HSHELL_REDRAW: иначе Refresh+UIA на каждый redraw любого окна.
            if (code is HSHELL_WINDOWCREATED or HSHELL_WINDOWDESTROYED
                or HSHELL_WINDOWREPLACED or HSHELL_WINDOWREPLACING)
                ScheduleTaskbarBoundsRefresh();

            // Сразу после активации таскбара — до тика 500ms EnforceZOrder.
            if (code == HSHELL_WINDOWACTIVATED && _overlayVisible)
                _dispatcher.BeginInvoke(ReassertZOrder);
        }

        private void ScheduleTaskbarBoundsRefresh()
        {
            int gen = System.Threading.Interlocked.Increment(ref _taskbarBoundsRefreshGen);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(250);
                if (gen != _taskbarBoundsRefreshGen) return;
                RefreshTaskbarBounds();
            });
        }

        private void RefreshTaskbarBounds()
        {
            try
            {
                foreach (var taskbar in EnumerateTaskbars())
                    RefreshOneTaskbarBounds(taskbar);
            }
            catch { }
        }

        private void RefreshOneTaskbarBounds(IntPtr taskbar)
        {
            if (taskbar == IntPtr.Zero || !Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb))
                return;

            int trayLeft = ResolveTrayLeft(taskbar, tb);

            int iconRight;
            int? lastIcon = TryGetLastTaskIconRight(taskbar, tb, trayLeft);
            if (lastIcon.HasValue)
                iconRight = lastIcon.Value;
            else
            {
                int startRight = TryGetStartButtonRight(taskbar, tb);
                iconRight = startRight > tb.Left
                    ? startRight
                    : tb.Left + (int)(48 * _dpiScale);
            }

            nint key = (nint)taskbar;
            if (_taskbarBounds.TryGetValue(key, out var prev) && prev.IconRight == iconRight && prev.TrayLeft == trayLeft)
                return;

            _taskbarBounds[key] = (iconRight, trayLeft);

            if (DebugLogger.IsEnabled)
                DebugLogger.Info("Taskbar.Bounds", $"hwnd=0x{taskbar.ToInt64():X} iconRight={iconRight} trayLeft={trayLeft}");
        }

        private int ResolveTrayLeft(IntPtr taskbar, Win32Helper.RECT tb)
        {
            string[] trayClasses =
            {
                "TrayNotifyWnd",
                "ClockFlyoutTrayBridgeWindow",
                "TrayClockWClass",
                "SystemTray.8HostWindow",
                "TrayShowDesktopButtonWClass"
            };

            foreach (var cls in trayClasses)
            {
                IntPtr h = FindDescendantByClass(taskbar, cls);
                if (h != IntPtr.Zero && Win32Helper.GetWindowRect(h, out Win32Helper.RECT rc) && rc.Width > 2)
                    return rc.Left;
            }

            int tbH = Math.Max(1, tb.Bottom - tb.Top);
            int threshold = tb.Left + (int)((tb.Right - tb.Left) * 0.5);
            int best = tb.Right;
            EnumChildWindows(taskbar, (hwnd, _) =>
            {
                var name = new StringBuilder(64);
                Win32Helper.GetClassName(hwnd, name, name.Capacity);
                string cls = name.ToString();
                if (cls is "MSTaskListWClass" or "MSTaskSwWClass" or "ReBarWindow32" or "WorkerW")
                    return true;
                if (!Win32Helper.GetWindowRect(hwnd, out Win32Helper.RECT rc) || rc.Width < 4)
                    return true;
                if (rc.Left < threshold) return true;
                if (rc.Height > tbH + 24) return true;
                if (rc.Width > (tb.Right - tb.Left) * 0.4) return true;
                if (rc.Left < best) best = rc.Left;
                return true;
            }, IntPtr.Zero);

            return best;
        }

        private static IntPtr FindDescendantByClass(IntPtr root, string className)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(root, (hwnd, _) =>
            {
                var sb = new StringBuilder(64);
                Win32Helper.GetClassName(hwnd, sb, sb.Capacity);
                if (string.Equals(sb.ToString(), className, StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static IntPtr FindBestTaskList(IntPtr taskbar)
        {
            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            EnumChildWindows(taskbar, (hwnd, _) =>
            {
                var sb = new StringBuilder(64);
                Win32Helper.GetClassName(hwnd, sb, sb.Capacity);
                if (!string.Equals(sb.ToString(), "MSTaskListWClass", StringComparison.Ordinal))
                    return true;
                if (!Win32Helper.GetWindowRect(hwnd, out Win32Helper.RECT rc) || rc.Width < 8)
                    return true;
                int area = rc.Width * Math.Max(1, rc.Height);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        /// <summary>
        /// X между последней иконкой и треем выбранного таскбара. Hot path — только кэш.
        /// </summary>
        private bool TryGetTaskbarDragRange(IntPtr taskbar, Win32Helper.RECT tb, int overlayWidth, out int minX, out int maxX)
        {
            int margin = Math.Max(4, (int)(4 * _dpiScale));

            if (!_taskbarBounds.TryGetValue((nint)taskbar, out var cache))
            {
                // Промах (смена монитора) — один синхронный пересчёт этой панели
                RefreshOneTaskbarBounds(taskbar);
                if (!_taskbarBounds.TryGetValue((nint)taskbar, out cache))
                {
                    minX = tb.Left + (int)(48 * _dpiScale);
                    maxX = tb.Right - overlayWidth - margin;
                    if (maxX < minX) minX = maxX;
                    return true;
                }
            }

            minX = cache.IconRight + margin;
            maxX = cache.TrayLeft - overlayWidth - margin;
            if (maxX < minX)
                minX = maxX;
            return true;
        }

        private int TryGetStartButtonRight(IntPtr taskbar, Win32Helper.RECT tb)
        {
            int best = 0;
            IntPtr start = FindWindowEx(taskbar, IntPtr.Zero, "Start", null);
            if (start == IntPtr.Zero)
                start = FindDescendantByClass(taskbar, "Start");
            if (start != IntPtr.Zero && Win32Helper.GetWindowRect(start, out Win32Helper.RECT sr) && sr.Width > 0)
                best = sr.Right;

            EnumChildWindows(taskbar, (hwnd, _) =>
            {
                var cls = new StringBuilder(64);
                Win32Helper.GetClassName(hwnd, cls, cls.Capacity);
                string name = cls.ToString();
                if (name is not ("Start" or "LaunchBand"))
                    return true;
                if (!Win32Helper.GetWindowRect(hwnd, out Win32Helper.RECT rc) || rc.Width < 8) return true;
                if (rc.Left > tb.Left + (tb.Right - tb.Left) / 3) return true;
                if (rc.Right > best && rc.Right < tb.Left + (int)(400 * _dpiScale))
                    best = rc.Right;
                return true;
            }, IntPtr.Zero);

            return best;
        }

        private int? TryGetLastTaskIconRight(IntPtr taskbar, Win32Helper.RECT tb, int trayLeft)
        {
            IntPtr taskList = FindBestTaskList(taskbar);
            if (taskList == IntPtr.Zero)
            {
                IntPtr rebar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
                IntPtr taskSw = FindWindowEx(rebar != IntPtr.Zero ? rebar : taskbar, IntPtr.Zero, "MSTaskSwWClass", null);
                taskList = FindWindowEx(
                    taskSw != IntPtr.Zero ? taskSw : (rebar != IntPtr.Zero ? rebar : taskbar),
                    IntPtr.Zero, "MSTaskListWClass", null);
            }
            if (taskList == IntPtr.Zero) return null;

            int tbH = Math.Max(1, tb.Bottom - tb.Top);
            int best = 0;

            // Win10: кнопки ToolbarWindow32
            EnumChildWindows(taskList, (hwnd, _) =>
            {
                var cls = new StringBuilder(64);
                Win32Helper.GetClassName(hwnd, cls, cls.Capacity);
                if (!string.Equals(cls.ToString(), "ToolbarWindow32", StringComparison.Ordinal))
                    return true;

                int count = SendMessage(hwnd, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                for (int i = 0; i < count; i++)
                {
                    var rc = new Win32Helper.RECT();
                    if (SendMessageRect(hwnd, TB_GETITEMRECT, (IntPtr)i, ref rc) == IntPtr.Zero)
                        continue;
                    var tl = new POINT { x = rc.Left, y = rc.Top };
                    var br = new POINT { x = rc.Right, y = rc.Bottom };
                    ClientToScreen(hwnd, ref tl);
                    ClientToScreen(hwnd, ref br);
                    int w = br.x - tl.x;
                    if (w < 6 || br.x >= trayLeft) continue;
                    if (br.x > best) best = br.x;
                }
                return true;
            }, IntPtr.Zero);

            if (best > tb.Left + 8)
                return best;

            // Win11 / fallback: компактные HWND иконок внутри списка (не сам контейнер)
            EnumChildWindows(taskList, (hwnd, _) =>
            {
                if (hwnd == taskList) return true;
                if (!Win32Helper.GetWindowRect(hwnd, out Win32Helper.RECT rc) || rc.Width < 8)
                    return true;
                if (rc.Right >= trayLeft - 2) return true;
                if (rc.Height > tbH + 20) return true;
                if (rc.Width > Math.Max(tbH * 3, (int)(72 * _dpiScale))) return true;
                if (rc.Right > best) best = rc.Right;
                return true;
            }, IntPtr.Zero);

            if (best > tb.Left + 8)
                return best;

            best = Math.Max(best, TryGetLastIconRightViaUia(taskList, tb, trayLeft));
            if (best <= tb.Left + 8)
                best = Math.Max(best, TryGetLastIconRightViaUia(taskbar, tb, trayLeft));

            return best > tb.Left + 8 ? best : null;
        }

        private static int TryGetLastIconRightViaUia(IntPtr hwnd, Win32Helper.RECT tb, int trayLeft)
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                if (root == null) return 0;

                var condition = new System.Windows.Automation.OrCondition(
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.ListItem),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.Button),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.MenuItem));

                var children = root.FindAll(System.Windows.Automation.TreeScope.Descendants, condition);
                double uiaBest = 0;
                int tbH = Math.Max(1, tb.Bottom - tb.Top);
                for (int i = 0; i < children.Count; i++)
                {
                    var r = children[i].Current.BoundingRectangle;
                    if (r.IsEmpty || r.Width < 4 || r.Width > Math.Max(tbH * 4, 96)) continue;
                    if (r.Right >= trayLeft) continue;
                    if (r.Top > tb.Bottom + 4 || r.Bottom < tb.Top - 4) continue;
                    if (r.Right > uiaBest) uiaBest = r.Right;
                }
                return uiaBest > tb.Left + 8 ? (int)Math.Round(uiaBest) : 0;
            }
            catch
            {
                return 0;
            }
        }

        // Reserve: worst-case string to measure for stable column width. Null = use live Value width.
        private class MetricItem { public string Label { get; set; } = ""; public string Value { get; set; } = ""; public string? Reserve { get; set; } = null; }

        private void UpdateLayer()
        {
            if (_targetAlpha == 0 && _currentAlpha == 0) return;
            var columns = PrepareMetricsData();
            float scale = _dpiScale * (float)_config.Config.ScaleFactor;
            float textScale = (float)_config.Config.ScaleFactor;
            bool pods = _config.Config.ShowPods;
            string fontName = _config.Config.FontFamily;
            if (string.IsNullOrEmpty(fontName) || fontName == "Default") fontName = "Segoe UI";
            System.Drawing.FontStyle style = _config.Config.IsTextBold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular;
            Font font = GetCachedFont(fontName, 8.5f * textScale, style);

            int h = (int)((pods ? 36 : 32) * scale); // pods get 4px extra height for top/bottom breathing room
            float gap = 2 * scale;                          // label→value gap
            float podGap = Math.Max(0, _config.Config.ColumnSpacing) * scale;  // user-controlled column spacing
            float pad = (pods ? 4 : 0) * scale;             // pod inner horizontal padding

            float[] widths = new float[columns.Count];
            float total = 2 * scale;                         // left outer margin
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                float GetItemWidth(MetricItem? item) {
                    if (item == null) return 0;
                    // Use the reserve string width when available so the column never resizes on value change
                    float valW = item.Reserve != null ? GetCachedMeasure(item.Reserve, font) : GetCachedMeasure(item.Value, font);
                    return GetCachedMeasure(item.Label, font) + gap + valW;
                }

                widths[i] = Math.Max(GetItemWidth(col.Top), GetItemWidth(col.Bottom)) + (pad * 2);

                total += widths[i] + podGap;
            }
            total = total - podGap + (2 * scale);           // right outer margin (was 4)

            int w = (int)Math.Max(20, total);
            EnsureOffscreenBuffer(w, h);
            if (_offscreenGraphics == null || _offscreenBitmap == null) return;

            _offscreenGraphics.Clear(Color.Transparent);
            RenderBackground(_offscreenGraphics, w, h, scale);
            RenderHoverEffect(_offscreenGraphics, w, h, scale);

            Brush vBrush = _cachedAccentBrush ?? Brushes.White;
            Brush lBrush = _cachedLabelBrush ?? Brushes.Cyan;
            bool ownPBrush = _cachedPodBrush == null;
            Brush pBrush = _cachedPodBrush ?? new SolidBrush(Color.FromArgb(15, 255, 255, 255));
            using var pPen = new Pen(Color.FromArgb(20, 255, 255, 255), 1);

            // Section brushes: fall back to global brush when per-section color is not set
            Brush netLBrush  = _cachedNetLabelBrush    ?? lBrush;
            Brush cpuLBrush  = _cachedCpuRamLabelBrush ?? lBrush;
            Brush gpuLBrush  = _cachedGpuLabelBrush    ?? lBrush;
            Brush dskLBrush  = _cachedDiskLabelBrush   ?? lBrush;
            Brush netVBrush  = _cachedNetAccentBrush    ?? vBrush;
            Brush cpuVBrush  = _cachedCpuRamAccentBrush ?? vBrush;
            Brush gpuVBrush  = _cachedGpuAccentBrush    ?? vBrush;
            Brush dskVBrush  = _cachedDiskAccentBrush   ?? vBrush;

            float cx = 2 * scale;                          // start drawing from left margin (was 4)
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                if (pods)
                {
                    using (var path = CreateRoundedRectPath((int)cx, (int)(2 * scale), (int)widths[i], (int)(h - 4 * scale), (int)(6 * scale)))
                    { _offscreenGraphics.FillPath(pBrush, path); _offscreenGraphics.DrawPath(pPen, path); }
                }

                // Pick the correct label and accent brushes for this column index
                Brush sectionLBrush = i == 0 ? netLBrush
                                    : i == 1 ? cpuLBrush
                                    : i == 2 ? gpuLBrush
                                    : dskLBrush;
                Brush sectionVBrush = i == 0 ? netVBrush
                                    : i == 1 ? cpuVBrush
                                    : i == 2 ? gpuVBrush
                                    : dskVBrush;

                float contentX = cx + pad;
                float contentW = Math.Max(0, widths[i] - pad * 2);
                // Fix: calculate y positions so both text rows are fully contained within h
                float lineH = font.Height;
                float totalTextH = lineH * 2 + (2 * scale);
                float y1 = (h - totalTextH) / 2f;
                float y2 = y1 + lineH + (2 * scale);

                Action<MetricItem, float> drawItem = (item, y) => {
                    float lw = GetCachedMeasure(item.Label, font);
                    float vw = GetCachedMeasure(item.Value, font);
                    // Лейбл — к левому краю капсулы; значение — к правому.
                    float valueX = contentX + contentW - vw;
                    valueX = Math.Max(valueX, contentX + lw + gap);
                    _offscreenGraphics.DrawString(item.Label, font, sectionLBrush, contentX, y, StringFormat.GenericTypographic);
                    _offscreenGraphics.DrawString(item.Value, font, sectionVBrush, valueX, y, StringFormat.GenericTypographic);
                };

                if (col.Top != null && col.Bottom != null)
                {
                    drawItem(col.Top, y1);
                    drawItem(col.Bottom, y2);
                }
                else
                {
                    var item = col.Top ?? col.Bottom;
                    if (item != null) drawItem(item, (h - font.Height) / 2f);
                }
                cx += widths[i] + podGap;
            }
            SetBitmap(_offscreenBitmap);
            if (ownPBrush) pBrush.Dispose();
        }

        private string FormatDiskSpeed(float kbps)
        {
            var L = LocalizationService.Instance;
            if (kbps >= 1024 * 1024) return L.Format("Unit.NetGBps", kbps / 1024f / 1024f);
            if (kbps >= 1024f) return L.Format("Unit.NetMBps", kbps / 1024f);
            return L.Format("Unit.NetKBpsInt", kbps);
        }

        private System.Collections.Generic.List<(MetricItem? Top, MetricItem? Bottom)> PrepareMetricsData()
        {
            bool compact = (_config.Config.DisplayStyle ?? "Text") == "Compact";
            var m = _viewModel.Metrics; var c = _config.Config;
            var L = LocalizationService.Instance;

            MetricItem Pct(string f, string cp, string v)  => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "100%" };
            MetricItem Temp(string f, string cp, string v) => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "100°" };
            // Reserve "1023 MB/s": widest net format before switching to GB/s (M glyph is wider than K)
            MetricItem Net(string f, string cp, string v)  => new MetricItem { Label = compact ? cp : f, Value = v, Reserve = "1023 MB/s" };

            var list = new System.Collections.Generic.List<(MetricItem?, MetricItem?)>();

            if (c.ShowNetUp || c.ShowNetDown)
                list.Add((
                    c.ShowNetUp ? Net(L["Overlay.Label.Up"], L["Overlay.Label.Compact.Up"], m.NetUpText) : null,
                    c.ShowNetDown ? Net(L["Overlay.Label.Down"], L["Overlay.Label.Compact.Down"], m.NetDownText) : null));

            if (c.ShowCpu || c.ShowRam)
                list.Add((
                    c.ShowCpu ? Pct(L["Overlay.Label.Cpu"], L["Overlay.Label.Compact.Cpu"], $"{(int)m.CpuUsage}%") : null,
                    c.ShowRam ? Pct(L["Overlay.Label.Ram"], L["Overlay.Label.Compact.Ram"], $"{(int)m.RamPercent}%") : null));

            string tempStr = m.GpuTemperature > 0 ? $"{(int)m.GpuTemperature}°" : L["Overlay.Label.Na"];
            if (c.ShowGpu || c.ShowTemp)
                list.Add((
                    c.ShowGpu ? Pct(L["Overlay.Label.Gpu"], L["Overlay.Label.Compact.Gpu"], $"{(int)m.GpuUsage}%") : null,
                    c.ShowTemp ? Temp(L["Overlay.Label.Temp"], L["Overlay.Label.Compact.Temp"], tempStr) : null));

            if (c.ShowDisk || c.ShowDiskSpeed)
            {
                if (m.Disks != null && m.Disks.Count > 0)
                {
                    foreach (var d in m.Disks)
                    {
                        // Clean name: "0 C: D:" -> "C"
                        string letter = d.Name;
                        int colonIdx = letter.IndexOf(':');
                        if (colonIdx > 0) letter = letter.Substring(colonIdx - 1, 1);
                        else if (letter.Length > 0) letter = letter.Substring(0, 1);

                        string cdkLabel = L.Format("Overlay.Label.Disk", letter.ToUpper());

                        list.Add((
                            c.ShowDisk ? Pct(cdkLabel, letter, $"{(int)d.SpacePercent}%") : null,
                            c.ShowDiskSpeed ? Pct(L["Overlay.Label.Speed"], L["Overlay.Label.Compact.Speed"], $"{(int)d.ActivityPercent}%") : null
                        ));
                    }
                }
            }

            return list;
        }

        private void EnsureOffscreenBuffer(int w, int h)
        {
            if (_offscreenBitmap == null || _offscreenBitmap.Width != w || _offscreenBitmap.Height != h)
            {
                _offscreenGraphics?.Dispose(); _offscreenBitmap?.Dispose();
                _offscreenBitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _offscreenGraphics = Graphics.FromImage(_offscreenBitmap);
                _offscreenGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                _offscreenGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }
        }

        private void RenderBackground(Graphics g, int w, int h, float s) { if (!_config.Config.ShowBackground || _cachedBgBrush == null) return; using (var p = CreateRoundedRectPath(0, 0, w, h, (int)(12 * s))) g.FillPath(_cachedBgBrush, p); }
        private void RenderHoverEffect(Graphics g, int w, int h, float s) { if (!_isHovered || _cachedHoverBrush == null || _cachedHoverPen == null) return; using (var p = CreateRoundedRectPath(0, 0, w - 1, h - 1, (int)(12 * s))) { g.FillPath(_cachedHoverBrush, p); g.DrawPath(_cachedHoverPen, p); } }
        private GraphicsPath CreateRoundedRectPath(int x, int y, int w, int h, int r) { GraphicsPath p = new GraphicsPath(); if (r <= 0) { p.AddRectangle(new Rectangle(x, y, w, h)); return p; } p.AddArc(x, y, r, r, 180, 90); p.AddArc(x + w - r, y, r, r, 270, 90); p.AddArc(x + w - r, y + h - r, r, r, 0, 90); p.AddArc(x, y + h - r, r, r, 90, 90); p.CloseFigure(); return p; }
        private Font GetCachedFont(string f, float s, System.Drawing.FontStyle st) { string k = $"{f}_{s}_{st}"; if (!_fontCache.TryGetValue(k, out var font)) { font = new Font(f, s, st); _fontCache[k] = font; } return font; }
        private void UpdateCachedColors()
        {
            _cachedBgBrush?.Dispose(); _cachedAccentBrush?.Dispose(); _cachedLabelBrush?.Dispose(); _cachedPodBrush?.Dispose(); _cachedHoverPen?.Dispose(); _cachedHoverBrush?.Dispose();
            _cachedNetLabelBrush?.Dispose(); _cachedCpuRamLabelBrush?.Dispose(); _cachedGpuLabelBrush?.Dispose(); _cachedDiskLabelBrush?.Dispose();
            _cachedNetAccentBrush?.Dispose(); _cachedCpuRamAccentBrush?.Dispose(); _cachedGpuAccentBrush?.Dispose(); _cachedDiskAccentBrush?.Dispose();
            _cachedBgBrush = new SolidBrush(HexToColor(_config.Config.BackgroundColorHex ?? "#B4141414"));
            _cachedAccentBrush = new SolidBrush(HexToColor(_config.Config.AccentColorHex ?? "#FFFFFF"));
            _cachedLabelBrush = new SolidBrush(HexToColor(_config.Config.LabelColorHex ?? "#00CCFF"));
            _cachedPodBrush = new SolidBrush(HexToColor(_config.Config.PodColorHex ?? "#0FFFFFFF"));
            _cachedHoverPen = new Pen(Color.FromArgb(20, 255, 255, 255));
            _cachedHoverBrush = new SolidBrush(Color.FromArgb(25, 255, 255, 255));
            // Per-section label brushes: only create if a custom color is set
            _cachedNetLabelBrush    = string.IsNullOrEmpty(_config.Config.NetLabelColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.NetLabelColorHex));
            _cachedCpuRamLabelBrush = string.IsNullOrEmpty(_config.Config.CpuRamLabelColorHex) ? null : new SolidBrush(HexToColor(_config.Config.CpuRamLabelColorHex));
            _cachedGpuLabelBrush    = string.IsNullOrEmpty(_config.Config.GpuLabelColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.GpuLabelColorHex));
            _cachedDiskLabelBrush   = string.IsNullOrEmpty(_config.Config.DiskLabelColorHex)   ? null : new SolidBrush(HexToColor(_config.Config.DiskLabelColorHex));
            // Per-section accent brushes: only create if a custom color is set
            _cachedNetAccentBrush    = string.IsNullOrEmpty(_config.Config.NetAccentColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.NetAccentColorHex));
            _cachedCpuRamAccentBrush = string.IsNullOrEmpty(_config.Config.CpuRamAccentColorHex) ? null : new SolidBrush(HexToColor(_config.Config.CpuRamAccentColorHex));
            _cachedGpuAccentBrush    = string.IsNullOrEmpty(_config.Config.GpuAccentColorHex)    ? null : new SolidBrush(HexToColor(_config.Config.GpuAccentColorHex));
            _cachedDiskAccentBrush   = string.IsNullOrEmpty(_config.Config.DiskAccentColorHex)   ? null : new SolidBrush(HexToColor(_config.Config.DiskAccentColorHex));
        }

        private float GetCachedMeasure(string t, Font f) { if (_offscreenGraphics == null) return 0; string k = $"{t}_{f.Name}_{f.Size}_{f.Style}"; if (!_measureCache.TryGetValue(k, out var w)) { w = _offscreenGraphics.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width; _measureCache[k] = w; } return w; }
        private void ClearCaches() { foreach (var f in _fontCache.Values) f.Dispose(); _fontCache.Clear(); _measureCache.Clear(); }
        private void SetBitmap(Bitmap bitmap)
        {
            IntPtr windowDC = GetWindowDC(_hWnd); IntPtr memDC = CreateCompatibleDC(windowDC); IntPtr hBitmap = IntPtr.Zero; IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0)); oldBitmap = SelectObject(memDC, hBitmap);
                SIZE size = new SIZE { cx = bitmap.Width, cy = bitmap.Height }; POINT ps = new POINT { x = 0, y = 0 }; POINT tp;
                if (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT wr)) tp = new POINT { x = wr.Left, y = wr.Top }; else tp = new POINT { x = (int)_config.Config.X, y = (int)_config.Config.Y };
                BLENDFUNCTION b = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = _currentAlpha, AlphaFormat = 1 };
                UpdateLayeredWindow(_hWnd, windowDC, ref tp, ref size, memDC, ref ps, 0, ref b, 2);
            }
            finally { if (hBitmap != IntPtr.Zero) { SelectObject(memDC, oldBitmap); DeleteObject(hBitmap); } DeleteDC(memDC); ReleaseDC(_hWnd, windowDC); }
        }

        private Color HexToColor(string hex)
        {
            try { hex = hex.Replace("#", ""); if (hex.Length == 8) return Color.FromArgb(int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));
                if (hex.Length == 6) return Color.FromArgb(255, int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber)); } catch { } return Color.White;
        }

        public void Dispose()
        {
            try
            {
                _disposing = true;
                _dispatcher.BeginInvoke(() =>
                {
                    try { _processListWindow?.Close(); } catch { }
                    _processListWindow = null;
                });
                _telemetry.MetricsUpdated -= _onMetricsUpdated; _config.Config.PropertyChanged -= _onConfigPropertyChanged; _zOrderTimer?.Dispose(); _fadeTimer?.Stop(); _dragAnimTimer?.Stop(); UnregisterShellHook(); UnregisterAppBar(); ClearCaches(); _offscreenGraphics?.Dispose(); _offscreenBitmap?.Dispose(); _cachedBgBrush?.Dispose(); _cachedAccentBrush?.Dispose(); _cachedLabelBrush?.Dispose(); _cachedPodBrush?.Dispose(); _cachedHoverPen?.Dispose(); _cachedHoverBrush?.Dispose(); _cachedNetLabelBrush?.Dispose(); _cachedCpuRamLabelBrush?.Dispose(); _cachedGpuLabelBrush?.Dispose(); _cachedDiskLabelBrush?.Dispose(); _cachedNetAccentBrush?.Dispose(); _cachedCpuRamAccentBrush?.Dispose(); _cachedGpuAccentBrush?.Dispose(); _cachedDiskAccentBrush?.Dispose(); if (_hWnd != IntPtr.Zero) DestroyWindow(_hWnd); _hWnd = IntPtr.Zero; if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_wmShellHook != 0 && msg == _wmShellHook)
            {
                OnShellHook(wParam);
                return IntPtr.Zero;
            }
            if (msg == 0x0002) // WM_DESTROY
            {
                if (!_disposing && hWnd == _hWnd)
                {
                    DebugLogger.Warn("Overlay", "WM_DESTROY received");
                    _hWnd = IntPtr.Zero;
                    _attachedTaskbar = IntPtr.Zero;
                    _appbarRegistered = false;
                    _shellHookRegistered = false;
                    _overlayVisible = false;
                    _dispatcher.BeginInvoke(() => EnsureOverlayHwndAlive());
                }
                return IntPtr.Zero;
            }
            if (msg == 0x0084) return (IntPtr)1;
            if (msg == 0x0010) return IntPtr.Zero; // WM_CLOSE — ignore, overlay is not closeable
            if (msg == WM_WINDOWPOSCHANGING && _config.Config.StickToTaskbar)
            {
                WINDOWPOS pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                // SWP_NOMOVE = 0x0002. Курсор — только во время drag; иначе оверлей
                // уезжает на монитор под мышью на каждом UpdateLayeredWindow (тики метрик).
                if ((pos.flags & 0x0002) == 0)
                {
                    int refX;
                    int refY;
                    if (_lButtonDragged && Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
                    {
                        refX = cursor.X;
                        refY = cursor.Y;
                    }
                    else if (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT cur) && cur.Width > 0)
                    {
                        refX = cur.Left + cur.Width / 2;
                        refY = cur.Top + cur.Height / 2;
                    }
                    else
                    {
                        int w = pos.cx > 0 ? pos.cx : 1;
                        refX = pos.x + w / 2;
                        refY = pos.y;
                    }

                    IntPtr taskbar = ResolveTaskbarForPoint(refX, refY);
                    if (taskbar != IntPtr.Zero && Win32Helper.GetWindowRect(taskbar, out Win32Helper.RECT tb))
                    {
                        EnsureAttachedToTaskbar(taskbar);
                        int oh = (int)((_config.Config.ShowPods ? 36 : 32) * _dpiScale * (float)_config.Config.ScaleFactor);
                        pos.y = tb.Top + (tb.Bottom - tb.Top - oh) / 2;

                        int overlayW = pos.cx > 0 ? pos.cx : (Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT wr) ? wr.Width : 200);
                        if (TryGetTaskbarDragRange(taskbar, tb, overlayW, out int minX, out int maxX))
                            pos.x = Math.Max(minX, Math.Min(maxX, pos.x));

                        Marshal.StructureToPtr(pos, lParam, false);
                    }
                }
            }
            if (msg == WM_WINDOWPOSCHANGED)
            {
                if (_appbarRegistered)
                {
                    APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hWnd };
                    SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd);
                }
                if (_processListWindow != null && _processListWindow.IsVisible)
                    _processListWindow.RepositionFromOverlay();
                return IntPtr.Zero;
            }
            if (msg == WM_APPBAR_CALLBACK) { if ((uint)wParam.ToInt32() == ABN_FULLSCREENAPP) { _shellFullscreen = (lParam != IntPtr.Zero); _dispatcher.BeginInvoke(UpdateVisibility); } return IntPtr.Zero; }
            if (msg == WM_SHOW_SETTINGS) { _dispatcher.BeginInvoke(() => App.OpenSettings(_viewModel, _config)); return IntPtr.Zero; }
            if (msg == WM_DPICHANGED) { _currentDpi = (uint)(wParam.ToInt32() & 0xFFFF); _dpiScale = _currentDpi / 96.0f; ClearCaches(); ScheduleTaskbarBoundsRefresh(); AlignToTaskbarCenter(); UpdateLayer(); return IntPtr.Zero; }
            if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE) { ScheduleTaskbarBoundsRefresh(); AlignToTaskbarCenter(); UpdateLayer(); return IntPtr.Zero; }
            if (msg == WM_CAPTURECHANGED)
            {
                if (_lButtonDragged && wParam != hWnd)
                {
                    EndCustomDrag();
                    _lButtonDown = false;
                    _lButtonDragged = false;
                }
                return IntPtr.Zero;
            }
            if (msg == WM_MOUSEMOVE)
            {
                if (!_trackingMouse)
                {
                    TRACKMOUSEEVENT tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = TME_LEAVE, hwndTrack = hWnd };
                    TrackMouseEvent(ref tme);
                    _trackingMouse = true;
                    _isHovered = true;
                    UpdateLayer();
                }

                if (_lButtonDragged)
                {
                    UpdateDragTargetFromCursor();
                    return IntPtr.Zero;
                }

                if (_lButtonDown && !_config.Config.LockPosition &&
                    Win32Helper.GetCursorPos(out Win32Helper.POINT cur))
                {
                    int dx = Math.Abs(cur.X - _lButtonDownScreenX);
                    int dy = Math.Abs(cur.Y - _lButtonDownScreenY);
                    if (dx > DragThresholdPx || dy > DragThresholdPx)
                    {
                        _lButtonDragged = true;
                        BeginCustomDrag();
                        return IntPtr.Zero;
                    }
                }
            }
            if (msg == WM_MOUSELEAVE) { _trackingMouse = false; _isHovered = false; UpdateLayer(); }
            if (msg == WM_LBUTTONDBLCLK)
            {
                _lButtonDown = false;
                // Дабл-клик → taskmgr только в режиме TaskManager (иначе конфликтует с popup)
                if (!_config.Config.IsProcessListClickMode)
                {
                    DebugLogger.Info("Overlay.Click", "DBLCLK → Task Manager");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true });
                }
                else
                {
                    DebugLogger.Info("Overlay.Click", "DBLCLK ignored (ProcessList mode)");
                }
                return IntPtr.Zero;
            }
            if (msg == WM_LBUTTONDOWN)
            {
                _lButtonDown = true;
                _lButtonDragged = false;
                if (Win32Helper.GetCursorPos(out Win32Helper.POINT downPt))
                {
                    _lButtonDownScreenX = downPt.X;
                    _lButtonDownScreenY = downPt.Y;
                }
                DebugLogger.Info("Overlay.Click", $"DOWN screen=({_lButtonDownScreenX},{_lButtonDownScreenY}) mode={_config.Config.OverlayClickMode}");
                SetCapture(hWnd);
                return IntPtr.Zero;
            }
            if (msg == WM_LBUTTONUP)
            {
                bool wasDown = _lButtonDown;
                bool wasDragged = _lButtonDragged;
                long sinceDrag = Environment.TickCount64 - _lastDragEndTick;

                if (wasDragged)
                    EndCustomDrag();

                _lButtonDown = false;
                _lButtonDragged = false;
                if (GetCapture() == hWnd)
                    ReleaseCapture();

                if (wasDown && !wasDragged && sinceDrag >= PostDragClickGuardMs)
                {
                    if (_config.Config.IsProcessListClickMode)
                    {
                        DebugLogger.Info("Overlay.Click", "UP → ToggleProcessList");
                        _dispatcher.BeginInvoke(ToggleProcessList);
                    }
                    else
                    {
                        DebugLogger.Info("Overlay.Click", "UP ignored (TaskManager mode — use double-click)");
                    }
                }
                else
                {
                    DebugLogger.Info("Overlay.Click", $"UP skipped down={wasDown} dragged={wasDragged} sinceDrag={sinceDrag}");
                }
                return IntPtr.Zero;
            }
            if (msg == WM_RBUTTONUP)
            {
                if (Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
                {
                    SetPreferredAppMode(2); AllowDarkModeForWindow(hWnd, true); FlushMenuThemes();
                    IntPtr hMenu = CreatePopupMenu();
                    var L = LocalizationService.Instance;
                    AppendMenu(hMenu, 0, 1001, L["Overlay.Menu.Settings"]);
                    AppendMenu(hMenu, 0, 1002, L["Overlay.Menu.TaskManager"]);
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, (_config.Config.AlwaysOnTop ? 0x0008U : 0), 1008, L["Overlay.Menu.KeepOnTop"]);
                    AppendMenu(hMenu, (_config.Config.HideOnFullscreen ? 0x0008U : 0), 1009, L["Overlay.Menu.HideFullscreen"]);
                    AppendMenu(hMenu, (_config.Config.LockPosition ? 0x0008U : 0), 1006, L["Overlay.Menu.LockPosition"]);
                    AppendMenu(hMenu, (_config.Config.StickToTaskbar ? 0x0008U : 0), 1007, L["Overlay.Menu.SnapTaskbar"]);
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, 0, 1003, L["Overlay.Menu.About"]);
                    AppendMenu(hMenu, 0x0800, 0, null);
                    AppendMenu(hMenu, 0, 1004, L["Overlay.Menu.Exit"]);
                    SetForegroundWindow(hWnd);

                    Win32Helper.GetWindowRect(hWnd, out Win32Helper.RECT wr);
                    IntPtr hMon = MonitorFromWindow(hWnd, 1);
                    MONITORINFO mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                    GetMonitorInfo(hMon, ref mi);

                    int my;
                    uint alignFlag;
                    // If the overlay is in the bottom half of the screen, pop the menu UP
                    if (wr.Top > (mi.rcWork.Top + mi.rcWork.Bottom) / 2)
                    {
                        my = wr.Top - 4;
                        alignFlag = 0x0020; // TPM_BOTTOMALIGN
                    }
                    else
                    {
                        my = wr.Bottom + 4;
                        alignFlag = 0x0000; // TPM_TOPALIGN
                    }

                    int ch = TrackPopupMenuEx(hMenu, 0x0100 | 0x0002 | alignFlag, pt.X, my, hWnd, IntPtr.Zero);
                    DestroyMenu(hMenu);
                    if (ch == 1001) _dispatcher.BeginInvoke(() => App.OpenSettings(_viewModel, _config));
                    else if (ch == 1006) { _config.Config.LockPosition = !_config.Config.LockPosition; _config.SaveConfig(); }
                    else if (ch == 1007) { _config.Config.StickToTaskbar = !_config.Config.StickToTaskbar; _config.SaveConfig(); }
                    else if (ch == 1008) { _config.Config.AlwaysOnTop = !_config.Config.AlwaysOnTop; _config.SaveConfig(); }
                    else if (ch == 1009) { _config.Config.HideOnFullscreen = !_config.Config.HideOnFullscreen; _config.SaveConfig(); }
                    else if (ch == 1002) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true });
                    else if (ch == 1003) _dispatcher.BeginInvoke(() => { App.OpenSettings(_viewModel, _config); App.SettingsWindow?.SelectSection("About"); });
                    else if (ch == 1004) _dispatcher.BeginInvoke(() => App.Quit());
                }
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ToggleProcessList()
        {
            try
            {
                if (_processListWindow != null && _processListWindow.IsVisible)
                {
                    DebugLogger.Info("ProcessList", "close");
                    _processListWindow.Close();
                    _processListWindow = null;
                    return;
                }

                DebugLogger.Info("ProcessList", "open");
                _processListWindow = new ProcessListWindow(_config, _hWnd);
                var win = _processListWindow;
                win.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_processListWindow, win))
                        _processListWindow = null;
                };
                win.Show();
                win.Activate();
            }
            catch (Exception ex)
            {
                DebugLogger.Error("ProcessList", ex.Message);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WNDCLASSEX { public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cl, string nm, uint st, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr ha, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? n);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr i, int n);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hd, ref POINT pd, ref SIZE ps, IntPtr hs, ref POINT pr, int c, ref BLENDFUNCTION b, int f);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr hd);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr h, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendMessageRect(IntPtr h, uint m, IntPtr w, ref Win32Helper.RECT l);
        private const uint TB_BUTTONCOUNT = 0x0418;
        private const uint TB_GETITEMRECT = 0x041D;
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public uint cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr m, uint f, uint id, string? n);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr m, uint f, int x, int y, IntPtr h, IntPtr t);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr h, bool a);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int m);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public uint cbSize; public Win32Helper.RECT rcMonitor; public Win32Helper.RECT rcWork; public uint dwFlags; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO m);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public Win32Helper.RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr SHAppBarMessage(uint m, ref APPBARDATA d);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    }
}
