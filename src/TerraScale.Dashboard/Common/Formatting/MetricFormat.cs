using System.Globalization;

namespace TerraScale.Dashboard.Common.Formatting
{
    public static class MetricFormat
    {
        public static string Latency(double? value)
        {
            return value is null ? "Not reported" :
            (Math.Abs(value.Value) >= 999.5 ? Math.Round(value.Value, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture)
                : value.Value.ToString(Math.Abs(value.Value) >= 100 ? "F0" : Math.Abs(value.Value) >= 10 ? "F1" : "F2", CultureInfo.InvariantCulture)) + " ms";
        }

        public static string Bytes(long value)
        {
            if (value < 1000)
            {
                return value.ToString(CultureInfo.InvariantCulture) + " B";
            }

            var amount = value / 1000d;
            var index = 0;
            while (amount >= 999.95 && index < 4) { amount /= 1000; index++; }
            return amount.ToString("F1", CultureInfo.InvariantCulture) + " " + new[] { "kB", "MB", "GB", "TB", "PB" }[index];
        }
    }
}
