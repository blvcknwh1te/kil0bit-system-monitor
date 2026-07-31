using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.Models
{
    /// <summary>
    /// Строка списка: одиночный процесс, группа или дочерний процесс в раскрытой группе.
    /// </summary>
    public sealed class ProcessListRow : INotifyPropertyChanged
    {
        private bool _isExpanded;

        public bool IsGroup { get; private init; }
        public bool IsChild { get; private init; }
        public bool IsExpandable => IsGroup && ChildCount > 1;
        public string GroupKey { get; private init; } = "";
        public int ChildCount { get; private init; }
        public IReadOnlyList<ProcessInfoItem> Members { get; private init; } = System.Array.Empty<ProcessInfoItem>();
        public ProcessInfoItem? Process { get; private init; }

        public string Name { get; private set; } = "";
        public int Pid { get; private set; }
        public float CpuPercent { get; private set; }
        public float MemoryMb { get; private set; }
        public float MemoryPercent { get; private set; }
        public float DiskPercent { get; private set; }
        public float NetworkKbps { get; private set; }
        public string ExePath { get; private set; } = "";
        public ImageSource? Icon { get; private set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExpanderGlyph));
            }
        }

        public string DisplayName =>
            IsExpandable ? $"{Name} ({ChildCount})" : Name;

        public string ExpanderGlyph => IsExpanded ? "▼" : "▶";

        public Visibility ExpanderVisibility =>
            IsExpandable ? Visibility.Visible : Visibility.Hidden;

        public Thickness NamePadding =>
            IsChild ? new Thickness(14, 0, 0, 0) : new Thickness(0);

        public string CpuText => $"{CpuPercent:0.0}%";
        public string MemoryText => LocalizationService.Instance.Format("Unit.MemoryMbPct", MemoryMb, MemoryPercent);
        public string DiskText => $"{DiskPercent:0.0}%";
        public string NetworkText => FormatNetwork(NetworkKbps);

        public static ProcessListRow FromSingle(ProcessInfoItem p)
        {
            return new ProcessListRow
            {
                IsGroup = false,
                IsChild = false,
                GroupKey = p.Name,
                ChildCount = 1,
                Members = new[] { p },
                Process = p,
                Name = p.Name,
                Pid = p.Pid,
                CpuPercent = p.CpuPercent,
                MemoryMb = p.MemoryMb,
                MemoryPercent = p.MemoryPercent,
                DiskPercent = p.DiskPercent,
                NetworkKbps = p.NetworkKbps,
                ExePath = p.ExePath,
                Icon = p.Icon
            };
        }

        public static ProcessListRow FromChild(ProcessInfoItem p)
        {
            var row = FromSingle(p);
            // IsChild через init — пересоздаём
            return new ProcessListRow
            {
                IsGroup = false,
                IsChild = true,
                GroupKey = p.Name,
                ChildCount = 1,
                Members = new[] { p },
                Process = p,
                Name = p.Name,
                Pid = p.Pid,
                CpuPercent = p.CpuPercent,
                MemoryMb = p.MemoryMb,
                MemoryPercent = p.MemoryPercent,
                DiskPercent = p.DiskPercent,
                NetworkKbps = p.NetworkKbps,
                ExePath = p.ExePath,
                Icon = p.Icon
            };
        }

        public static ProcessListRow FromGroup(string name, IReadOnlyList<ProcessInfoItem> members, bool expanded)
        {
            float cpu = 0, memMb = 0, memPct = 0, disk = 0, net = 0;
            foreach (var m in members)
            {
                cpu += m.CpuPercent;
                memMb += m.MemoryMb;
                memPct += m.MemoryPercent;
                disk += m.DiskPercent;
                net += m.NetworkKbps;
            }

            var primary = members
                .OrderByDescending(m => m.MemoryMb)
                .First();

            return new ProcessListRow
            {
                IsGroup = true,
                IsChild = false,
                IsExpanded = expanded,
                GroupKey = name,
                ChildCount = members.Count,
                Members = members,
                Process = primary,
                Name = name,
                Pid = primary.Pid,
                CpuPercent = cpu,
                MemoryMb = memMb,
                MemoryPercent = memPct,
                DiskPercent = disk,
                NetworkKbps = net,
                ExePath = primary.ExePath,
                Icon = primary.Icon ?? members.Select(m => m.Icon).FirstOrDefault(i => i != null)
            };
        }

        private static string FormatNetwork(float kbps)
        {
            var L = LocalizationService.Instance;
            if (kbps < 0.05f) return L.Format("Unit.NetKBps", 0);
            if (kbps >= 1024f) return L.Format("Unit.NetMBps", kbps / 1024f);
            return L.Format("Unit.NetKBps", kbps);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
