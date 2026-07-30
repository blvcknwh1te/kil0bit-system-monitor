using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Kil0bitSystemMonitor.Services;

namespace Kil0bitSystemMonitor.Models
{
    public class ProcessInfoItem : INotifyPropertyChanged
    {
        private string _name = "";
        private int _pid;
        private float _cpuPercent;
        private float _memoryMb;
        private float _memoryPercent;
        private float _diskPercent;
        private float _networkKbps;
        private string _exePath = "";
        private ImageSource? _icon;

        public string Name { get => _name; set { if (_name != value) { _name = value; OnPropertyChanged(); } } }
        public int Pid { get => _pid; set { if (_pid != value) { _pid = value; OnPropertyChanged(); } } }
        public float CpuPercent { get => _cpuPercent; set { if (_cpuPercent != value) { _cpuPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(CpuText)); } } }
        public float MemoryMb { get => _memoryMb; set { if (_memoryMb != value) { _memoryMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(MemoryText)); } } }
        public float MemoryPercent { get => _memoryPercent; set { if (_memoryPercent != value) { _memoryPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(MemoryText)); } } }
        public float DiskPercent { get => _diskPercent; set { if (_diskPercent != value) { _diskPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(DiskText)); } } }
        public float NetworkKbps { get => _networkKbps; set { if (_networkKbps != value) { _networkKbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetworkText)); } } }
        public string ExePath
        {
            get => _exePath;
            set
            {
                if (_exePath == value) return;
                _exePath = value;
                OnPropertyChanged();
            }
        }

        public ImageSource? Icon
        {
            get => _icon;
            set { if (!ReferenceEquals(_icon, value)) { _icon = value; OnPropertyChanged(); } }
        }

        public string CpuText => $"{CpuPercent:0.0}%";
        public string MemoryText => LocalizationService.Instance.Format("Unit.MemoryMbPct", MemoryMb, MemoryPercent);
        public string DiskText => $"{DiskPercent:0.0}%";
        public string NetworkText => FormatNetwork(NetworkKbps);

        private static string FormatNetwork(float kbps)
        {
            var L = LocalizationService.Instance;
            if (kbps < 0.05f) return L.Format("Unit.NetKBps", 0);
            if (kbps >= 1024f) return L.Format("Unit.NetMBps", kbps / 1024f);
            return L.Format("Unit.NetKBps", kbps);
        }

        public void EnsureIcon()
        {
            if (_icon != null || string.IsNullOrEmpty(_exePath)) return;
            Icon = ProcessIconCache.Get(_exePath);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class ProcessListSortColumns
    {
        public const string Name = "Name";
        public const string Cpu = "Cpu";
        public const string Memory = "Memory";
        public const string Disk = "Disk";
        public const string Network = "Network";
    }
}
