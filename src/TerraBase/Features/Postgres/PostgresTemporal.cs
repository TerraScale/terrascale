using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Postgres;

/// <summary>The finite Gregorian subset shared by PostgreSQL and .NET, at microsecond precision.</summary>
internal static partial class PostgresTemporal
{
    private static readonly DateTime Epoch = new(2000, 1, 1);
    private static readonly int EpochDay = DateOnly.FromDateTime(Epoch).DayNumber;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF"];
    private static readonly string[] OffsetFormats = ["yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-dd HH:mm:ss.FFFFFFzzz"];

    public static SqlValue? Decode(int oid, byte[] bytes)
    {
        if (oid == 1082 && bytes.Length == 4)
        {
            var days = BinaryPrimitives.ReadInt32BigEndian(bytes);
            return days >= -EpochDay && days <= DateOnly.MaxValue.DayNumber - EpochDay
                ? SqlValue.Of(DateOnly.FromDayNumber(days + EpochDay).ToString("yyyy-MM-dd", Invariant)) : null;
        }

        if (oid is 1114 or 1184 && bytes.Length == 8)
        {
            var micros = BinaryPrimitives.ReadInt64BigEndian(bytes);
            // Check before multiplying: PostgreSQL's range (including infinities) exceeds DateTime's.
            return micros >= -Epoch.Ticks / 10 && micros <= (DateTime.MaxValue.Ticks - Epoch.Ticks) / 10
                ? SqlValue.Of(Format(oid, new DateTime(Epoch.Ticks + micros * 10))) : null;
        }

        return null;
    }
    public static SqlValue? Parse(int oid, string text) => TryParse(oid, text, out var value) ? SqlValue.Of(Format(oid, value)) : null;

    public static bool TryEncode(string text, int oid, bool binary, out byte[]? bytes)
    {
        bytes = null;
        if (!TryParse(oid, text, out var value))
        {
            return false;
        }

        if (!binary)
        {
            bytes = Encoding.UTF8.GetBytes(Format(oid, value));
        }
        else if (oid == 1082)
        {
            bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, DateOnly.FromDateTime(value).DayNumber - EpochDay);
        }
        else
        {
            bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, (value.Ticks - Epoch.Ticks) / 10);
        }

        return true;
    }

    private static bool TryParse(int oid, string text, out DateTime value)
    {
        value = default;
        if (oid == 1082)
        {
            return DateTime.TryParseExact(text, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out value);
        }

        var match = TimestampPattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var clock = match.Groups[1].Value.Replace('T', ' ');
        var offset = match.Groups[2].Value;
        offset = offset is "" or "Z" ? "+00:00" : offset.Length == 3 ? offset + ":00" : offset;
        if (oid == 1114)
        {
            return DateTimeOffset.TryParseExact("2000-01-01 00:00:00" + offset, OffsetFormats, Invariant, DateTimeStyles.None, out _)
                && DateTime.TryParseExact(clock, TimestampFormats, Invariant, DateTimeStyles.None, out value);
        }

        if (!DateTimeOffset.TryParseExact(clock + offset, OffsetFormats, Invariant, DateTimeStyles.None, out var instant))
        {
            return false;
        }

        value = instant.UtcDateTime;
        return true;
    }

    private static string Format(int oid, DateTime value) => value.ToString(oid == 1082 ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.ffffff", Invariant)
        + (oid == 1184 ? "+00" : "");

    [GeneratedRegex(@"\A([0-9]{4}-[0-9]{2}-[0-9]{2}[ T][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,6})?)(Z|[+-][0-9]{2}(?::[0-9]{2})?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
}
