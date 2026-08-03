namespace Kil0bitSystemMonitor.Models
{
    /// <summary>Единый источник дефолтов для warning/critical метрик (0–1 и цвета).</summary>
    public static class MetricAlertDefaults
    {
        public const double WarningThreshold = 0.75;
        public const double CriticalThreshold = 0.90;
        public const string WarningColorHex = "#FFFF9800";
        public const string CriticalColorHex = "#FFF44336";
    }
}
