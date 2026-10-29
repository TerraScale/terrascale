using System.Globalization;

namespace TerraScale.Dashboard.Common.Formatting
{
    public static class ValueFormat
    {
        public static string Plural(long count, string singular, string? plural = null, string? number = null)
        {
            return (number ?? count.ToString("N0", CultureInfo.InvariantCulture)) + " " + (count == 1 ? singular : plural ?? singular + "s");
        }

        public static string UtcDate(DateTimeOffset value)
        {
            return value.UtcDateTime.ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }

        // Keep the measured ratio for chart geometry and structured tool responses.
        public static double? Percent(long? part, long? total)
        {
            return part is null || total is null ? null
            : part <= 0 || total <= 0 ? 0 : part.Value / (double)total.Value * 100;
        }

        public static string PercentLabel(double value)
        {
            return value is > 0 and < 1 ? "<1%"
            : Math.Round(value, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture) + "%";
        }
    }
}
