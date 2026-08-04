namespace Kil0bitSystemMonitor.Models
{
    /// <summary>Единый источник дефолтов для warning/critical метрик (0–1 и цвета).</summary>
    public static class MetricAlertDefaults
    {
        public const double WarningThreshold = 0.75;
        public const double CriticalThreshold = 0.90;

        /// <summary>Минимальный зазор critical − warning.</summary>
        public const double ThresholdMinGap = 0.05;
        public const double ThresholdMinGapPercent = 5;

        /// <summary>Critical: фиксированный диапазон слайдера (шкала 0–100).</summary>
        public const double CriticalPercentMin = 10;
        public const double CriticalPercentMax = 95;

        /// <summary>Warning min = Critical.min − gap.</summary>
        public const double WarningPercentMin = CriticalPercentMin - ThresholdMinGapPercent;

        public const string WarningColorHex = "#FFFF9800";
        public const string CriticalColorHex = "#FFF44336";
    }
}
