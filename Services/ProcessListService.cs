using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Лёгкий снимок процессов (CPU/RAM/Disk). Сеть — доля Other IO (best-effort, без ESTATS).
    /// Вызывать с фонового потока: на UI не блокирует.
    /// </summary>
    public class ProcessListService : IDisposable
    {
        private readonly Dictionary<int, ProcessSample> _prev = new();
        private readonly Dictionary<int, string> _pathCache = new();
        private readonly object _gate = new();
        private bool _primed;

        private sealed class ProcessSample
        {
            public long CpuTicks;
            public ulong IoBytes;
            public ulong NetBytes;
            public DateTime Time;
        }

        public IReadOnlyList<ProcessInfoItem> Snapshot()
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var result = new List<ProcessInfoItem>();
                var current = new Dictionary<int, ProcessSample>();
                var ioRates = new Dictionary<int, double>();
                var netRates = new Dictionary<int, double>();
                var cpuRates = new Dictionary<int, double>();
                var memBytes = new Dictionary<int, long>();
                var names = new Dictionary<int, string>();

                ulong totalPhys = GetTotalPhys();
                int processors = Math.Max(1, Environment.ProcessorCount);

                Process[] procs;
                try { procs = Process.GetProcesses(); }
                catch { return result; }

                foreach (var p in procs)
                {
                    try
                    {
                        int pid = p.Id;
                        if (pid == 0) continue;

                        string name;
                        try { name = p.ProcessName; }
                        catch { continue; }

                        long ws = 0;
                        try { ws = p.WorkingSet64; } catch { }

                        long cpuTicks = 0;
                        try { cpuTicks = p.TotalProcessorTime.Ticks; } catch { }

                        var (ioBytes, netBytes) = ReadIoBytes(pid);

                        var sample = new ProcessSample
                        {
                            CpuTicks = cpuTicks,
                            IoBytes = ioBytes,
                            NetBytes = netBytes,
                            Time = now
                        };
                        current[pid] = sample;
                        names[pid] = name;
                        memBytes[pid] = ws;

                        if (_prev.TryGetValue(pid, out var prev))
                        {
                            double dt = (now - prev.Time).TotalSeconds;
                            if (dt > 0.05)
                            {
                                double cpu = (cpuTicks - prev.CpuTicks) / (double)TimeSpan.TicksPerSecond / processors / dt * 100.0;
                                cpuRates[pid] = Math.Clamp(cpu, 0, processors * 100.0);

                                double ioDelta = ioBytes >= prev.IoBytes ? ioBytes - prev.IoBytes : 0;
                                ioRates[pid] = ioDelta / dt;

                                double netDelta = netBytes >= prev.NetBytes ? netBytes - prev.NetBytes : 0;
                                netRates[pid] = netDelta / dt;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                _prev.Clear();
                foreach (var kv in current)
                    _prev[kv.Key] = kv.Value;

                if (!_primed)
                {
                    _primed = true;
                    return Array.Empty<ProcessInfoItem>();
                }

                double totalIo = ioRates.Values.Sum();
                if (totalIo < 1) totalIo = 1;

                foreach (var pid in names.Keys)
                {
                    memBytes.TryGetValue(pid, out long ws);
                    cpuRates.TryGetValue(pid, out double cpu);
                    ioRates.TryGetValue(pid, out double ioRate);
                    netRates.TryGetValue(pid, out double netRate);

                    float memMb = ws / (1024f * 1024f);
                    float memPct = totalPhys > 0 ? (float)(ws * 100.0 / totalPhys) : 0;

                    result.Add(new ProcessInfoItem
                    {
                        Pid = pid,
                        Name = names[pid],
                        CpuPercent = (float)Math.Round(cpu, 1),
                        MemoryMb = (float)Math.Round(memMb, 0),
                        MemoryPercent = (float)Math.Round(memPct, 1),
                        DiskPercent = (float)Math.Round(Math.Min(100.0, ioRate * 100.0 / totalIo), 1),
                        // KB/s по OtherTransfer (часто включает сетевой трафик на уровне процесса)
                        NetworkKbps = (float)(netRate / 1024.0),
                        ExePath = ResolvePath(pid)
                    });
                }

                return result;
            }
        }

        private static (ulong ioBytes, ulong netBytes) ReadIoBytes(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) return (0, 0);
            try
            {
                if (GetProcessIoCounters(h, out IO_COUNTERS io))
                    return (io.ReadTransferCount + io.WriteTransferCount, io.OtherTransferCount);
            }
            catch { }
            finally { CloseHandle(h); }
            return (0, 0);
        }

        private string ResolvePath(int pid)
        {
            if (_pathCache.TryGetValue(pid, out var cached))
                return cached;

            string path = "";
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size))
                        path = sb.ToString();
                }
                catch { }
                finally { CloseHandle(h); }
            }

            if (_pathCache.Count > 4000)
                _pathCache.Clear();
            _pathCache[pid] = path;
            return path;
        }

        private static ulong GetTotalPhys()
        {
            var ms = new MEMORYSTATUSEX();
            return GlobalMemoryStatusEx(ms) ? ms.ullTotalPhys : 0;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _prev.Clear();
                _pathCache.Clear();
            }
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
    }
}
