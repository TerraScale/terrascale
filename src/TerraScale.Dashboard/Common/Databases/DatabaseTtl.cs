using System.Globalization;

namespace TerraScale.Dashboard.Common.Databases
{
    public static class DatabaseTtl
    {
        public const string MinimumHours = "0.016666666666666667";

        public static decimal? Hours(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return null; }
            if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours))
            {
                throw new AccountAccessException("Enter a TTL in hours between one minute and 365 days.", 422);
            }
            _ = Seconds(hours);
            return hours;
        }

        public static long? Seconds(decimal? hours)
        {
            if (hours is null) { return null; }
            if (hours < 1m / 60m || hours > 8760m)
            {
                throw new AccountAccessException("Enter a TTL in hours between one minute and 365 days.", 422);
            }
            return (long)decimal.Round(hours.Value * 3600m, 0, MidpointRounding.AwayFromZero);
        }

        public static DateTimeOffset? Instant(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return null; }
            if (!DateTimeOffset.TryParseExact(value,
                ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
            {
                throw new AccountAccessException("Enter an ISO 8601 point in time, such as 2026-11-04T12:00:00Z.", 422);
            }
            return instant;
        }
    }
}
