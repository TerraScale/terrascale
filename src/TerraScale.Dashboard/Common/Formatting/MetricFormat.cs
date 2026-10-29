using System.Globalization;

namespace TerraScale.Dashboard.Common.Formatting
{
    public static class MetricFormat
    {
        // Three significant figures below 1,000 ms; larger values use grouped whole milliseconds.
        public static string Latency(double? value)
        {
            return value is null ? "Not reported" :
            (Math.Abs(value.Value) >= 999.5 ? Math.Round(value.Value, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture)
                : value.Value.ToString(Math.Abs(value.Value) >= 100 ? "F0" : Math.Abs(value.Value) >= 10 ? "F1" : "F2", CultureInfo.InvariantCulture)) + " ms";
        }

        // Decimal units, as the published catalog prices storage and egress in GB.
        // Promotion uses the rounded value, so 999,999,999 bytes reads "1.0 GB".
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
