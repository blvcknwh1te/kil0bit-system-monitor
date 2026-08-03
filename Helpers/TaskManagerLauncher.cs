using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// Запуск Task Manager.
    /// Win11: геометрия через settings.json → FullPosition (+50px к ширине popup, центр тот же).
    /// Win10 / любая ошибка: обычный старт без краша.
    /// </summary>
    public static class TaskManagerLauncher
    {
        public const string TaskManagerProcessName = "Taskmgr";
        public const string TaskManagerWindowClass = "TaskManagerWindow";

        /// <summary>Позиционирование через settings.json — только Win11.</summary>
        public static bool SupportsPositionedLaunch => OsCompat.IsWindows11;

        private static readonly TimeSpan KillWait = TimeSpan.FromMilliseconds(800);
        private const int PositionTolerancePx = 6;

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "TaskManager", "settings.json");

        public static void OpenNearOverlay(IntPtr overlayHwnd)
        {
            try
            {
                if (!SupportsPositionedLaunch)
                {
                    StartTaskMgr();
                    return;
                }

                if (overlayHwnd == IntPtr.Zero)
                    overlayHwnd = OverlayPopupLayout.FindOverlayHwnd();

                if (!OverlayPopupLayout.TryComputeForTaskManager(overlayHwnd, out var placement))
                {
                    StartTaskMgr();
                    return;
                }

                _ = Task.Run(() => OpenWithPlacement(placement));
            }
            catch (Exception ex)
            {
                DebugLogger.Error("TaskMgr", ex.Message);
                StartTaskMgr();
            }
        }

        public static void OpenNearOverlay() => OpenNearOverlay(IntPtr.Zero);

        public static void Open() => OpenNearOverlay(IntPtr.Zero);

        private static void OpenWithPlacement(OverlayPopupLayout.Placement placement)
        {
            try
            {
                IntPtr hwnd = FindTaskManagerWindow();
                if (hwnd != IntPtr.Zero)
                {
                    if (MatchesPlacement(hwnd, placement))
                    {
                        ShowWindow(hwnd, SwShowNormal);
                        SetForegroundWindow(hwnd);
                        return;
                    }

                    if (TryMoveLive(hwnd, placement))
                    {
                        SetForegroundWindow(hwnd);
                        if (DebugLogger.IsEnabled)
                            DebugLogger.Info("TaskMgr", "moved live via SetWindowPos");
                        return;
                    }
                }

                KillExistingTaskManager();
                if (!TryWriteFullPosition(placement))
                {
                    StartTaskMgr();
                    return;
                }

                StartTaskMgr();

                if (DebugLogger.IsEnabled)
                {
                    DebugLogger.Info("TaskMgr",
                        $"restart FullPosition ({placement.Left},{placement.Top}) {placement.WidthPx}x{placement.HeightPx}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("TaskMgr", ex.Message);
                StartTaskMgr();
            }
        }

        private static bool TryMoveLive(IntPtr hwnd, OverlayPopupLayout.Placement p)
        {
            try
            {
                ApplyPlacement(hwnd, p);
                return MatchesPlacement(hwnd, p);
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyPlacement(IntPtr hwnd, OverlayPopupLayout.Placement p)
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
            {
                wp.flags = 0;
                wp.showCmd = SwShowNormal;
                wp.rcNormalPosition = new Win32Helper.RECT
                {
                    Left = p.Left,
                    Top = p.Top,
                    Right = p.Left + p.WidthPx,
                    Bottom = p.Top + p.HeightPx
                };
                SetWindowPlacement(hwnd, ref wp);
            }

            SetWindowPos(hwnd, IntPtr.Zero, p.Left, p.Top, p.WidthPx, p.HeightPx, SwpNoZOrder);
        }

        private static bool MatchesPlacement(IntPtr hwnd, OverlayPopupLayout.Placement p)
        {
            if (!Win32Helper.GetWindowRect(hwnd, out var r)) return false;
            return Math.Abs(r.Left - p.Left) <= PositionTolerancePx
                && Math.Abs(r.Top - p.Top) <= PositionTolerancePx
                && Math.Abs(r.Width - p.WidthPx) <= PositionTolerancePx
                && Math.Abs(r.Height - p.HeightPx) <= PositionTolerancePx;
        }

        private static void KillExistingTaskManager()
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(TaskManagerProcessName); }
            catch { return; }

            foreach (var proc in procs)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        try
                        {
                            proc.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            proc.Kill();
                        }
                        proc.WaitForExit((int)KillWait.TotalMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("TaskMgr", $"kill: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }

            var deadline = DateTime.UtcNow + KillWait;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsTaskManagerRunning()) return;
                Thread.Sleep(15);
            }
        }

        private static bool IsTaskManagerRunning()
        {
            Process[] left;
            try { left = Process.GetProcessesByName(TaskManagerProcessName); }
            catch { return false; }

            bool alive = false;
            foreach (var p in left)
            {
                try { alive |= !p.HasExited; }
                catch { }
                finally { p.Dispose(); }
            }
            return alive;
        }

        private static bool TryWriteFullPosition(OverlayPopupLayout.Placement p)
        {
            try
            {
                string path = SettingsPath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                JsonNode root;
                if (File.Exists(path))
                {
                    root = JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject();
                }
                else
                {
                    root = new JsonObject();
                }

                root["FullPosition"] = new JsonObject
                {
                    ["Left"] = p.Left,
                    ["Top"] = p.Top,
                    ["Right"] = p.Left + p.WidthPx,
                    ["Bottom"] = p.Top + p.HeightPx
                };

                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("TaskMgr", $"settings: {ex.Message}");
                return false;
            }
        }

        private static void StartTaskMgr()
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskmgr") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DebugLogger.Error("TaskMgr", ex.Message);
            }
        }

        private static IntPtr FindTaskManagerWindow()
        {
            try
            {
                IntPtr byClass = Win32Helper.FindWindow(TaskManagerWindowClass, null);
                if (byClass != IntPtr.Zero)
                    return byClass;

                foreach (var proc in Process.GetProcessesByName(TaskManagerProcessName))
                {
                    try
                    {
                        if (proc.MainWindowHandle != IntPtr.Zero)
                            return proc.MainWindowHandle;
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch { }

            return IntPtr.Zero;
        }

        private const int SwShowNormal = 1;
        private const uint SwpNoZOrder = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public Win32Helper.POINT ptMinPosition;
            public Win32Helper.POINT ptMaxPosition;
            public Win32Helper.RECT rcNormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    }
}
