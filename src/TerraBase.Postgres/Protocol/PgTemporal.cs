using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TerraBase.Postgres.Protocol;

internal static partial class PgTemporal
{
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly int EpochDay = DateOnly.FromDateTime(Epoch).DayNumber;
    private static readonly string[] ClockFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];

    public static bool TryParse(int oid, string text, out DateTime value)
    {
        value = default;
        var trimmed = text.Trim();
        if (oid == PgOid.Time)
        {
            if (!TimeOnly.TryParseExact(trimmed, ["HH:mm:ss", "HH:mm:ss.FFFFFFF", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return false;
            }

            value = Epoch.Add(time.ToTimeSpan());
            return true;
        }

        var match = TimestampPattern().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        var clock = match.Groups["clock"].Value.Replace('T', ' ');
        if (!DateTime.TryParseExact(clock, ClockFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        var offset = match.Groups["offset"].Value;
        if (oid != PgOid.Timestamptz || offset is "" or "Z" or "z")
        {
            value = oid == PgOid.Date ? parsed.Date : parsed;
            return true;
        }

        var sign = offset[0] == '-' ? -1 : 1;
        var hours = int.Parse(offset.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var minutes = offset.Length > 3 ? int.Parse(offset.AsSpan(offset.Length - 2, 2), NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        value = parsed.AddMinutes(-sign * ((hours * 60) + minutes));
        return true;
    }

    public static string Format(int oid, DateTime value) => oid switch
    {
        PgOid.Date => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        PgOid.Time => value.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        PgOid.Timestamptz => value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('.') + "+00",
        _ => value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
    };

    public static void WriteBinary(int oid, DateTime value, PgMessageBuffer output)
    {
        if (oid == PgOid.Date)
        {
            output.WriteInt32(DateOnly.FromDateTime(value).DayNumber - EpochDay);
            return;
        }

        if (oid == PgOid.Time)
        {
            output.WriteInt64(value.TimeOfDay.Ticks / TimeSpan.TicksPerMicrosecond);
            return;
        }

        output.WriteInt64((value.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMicrosecond);
    }

    public static bool TryReadBinary(int oid, ReadOnlySpan<byte> value, out string text)
    {
        text = string.Empty;
        if (oid == PgOid.Date && value.Length == 4)
        {
            var days = (long)BinaryPrimitives.ReadInt32BigEndian(value) + EpochDay;
            if (days < DateOnly.MinValue.DayNumber || days > DateOnly.MaxValue.DayNumber)
            {
                return false;
            }

            text = DateOnly.FromDayNumber((int)days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        if (value.Length != 8)
        {
            return false;
        }

        var micros = BinaryPrimitives.ReadInt64BigEndian(value);
        if (oid == PgOid.Time)
        {
            if (micros < 0 || micros >= TimeSpan.TicksPerDay / TimeSpan.TicksPerMicrosecond)
            {
                return false;
            }

            text = Format(oid, Epoch.AddTicks(micros * TimeSpan.TicksPerMicrosecond));
            return true;
        }

        if (micros < (DateTime.MinValue.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMicrosecond
            || micros > (DateTime.MaxValue.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMicrosecond)
        {
            return false;
        }

        text = Format(oid, Epoch.AddTicks(micros * TimeSpan.TicksPerMicrosecond));
        return true;
    }

    [GeneratedRegex(@"\A(?<clock>[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[ T][0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]{1,7})?)?)?)\s*(?<offset>Z|z|[+-][0-9]{2}(?::?[0-9]{2})?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
}
