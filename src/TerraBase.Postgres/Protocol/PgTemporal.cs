using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace TerraBase.Postgres.Protocol;

internal static class PgTemporal
{
    private const int EpochDay = 730119;
    private const long EpochTicks = 630822816000000000L;
    private const long MinMicros = -EpochTicks / TimeSpan.TicksPerMicrosecond;
    private const long MaxMicros = (3155378975999999999L - EpochTicks) / TimeSpan.TicksPerMicrosecond;
    private const long MicrosPerDay = TimeSpan.TicksPerDay / TimeSpan.TicksPerMicrosecond;

    private static ReadOnlySpan<byte> DaysPerMonth => [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    public static bool TryParse(int oid, ReadOnlySpan<char> text, out DateTime value)
    {
        value = default;
        var trimmed = text.Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        if (oid == PgOid.Time)
        {
            return TryParseTime(trimmed, out value);
        }

        return TryParseDateOrTimestamp(oid, trimmed, out value);
    }

    public static bool TryParseUtf8(int oid, ReadOnlySpan<byte> utf8, out DateTime value)
    {
        value = default;
        var start = 0;
        var end = utf8.Length;
        while (start < end && IsAsciiSpace(utf8[start]))
        {
            start++;
        }

        while (end > start && IsAsciiSpace(utf8[end - 1]))
        {
            end--;
        }

        var trimmed = utf8[start..end];
        if (trimmed.IsEmpty)
        {
            return false;
        }

        return oid == PgOid.Time
            ? TryParseTimeUtf8(trimmed, out value)
            : TryParseDateOrTimestampUtf8(oid, trimmed, out value);
    }

    public static bool IsCanonicalTimestamptz(ReadOnlySpan<char> text)
    {
        var len = text.Length;
        if (len is < 22 or > 29)
        {
            return false;
        }

        if (text[4] != '-' || text[7] != '-' || text[10] != ' ' || text[13] != ':' || text[16] != ':'
            || text[len - 3] != '+' || text[len - 2] != '0' || text[len - 1] != '0')
        {
            return false;
        }

        if (!TryDigits4(text, 0, out var year) || !TryDigits2(text, 5, out var month) || !TryDigits2(text, 8, out var day)
            || !TryDigits2(text, 11, out var hour) || !TryDigits2(text, 14, out var minute) || !TryDigits2(text, 17, out var second)
            || !IsValidDate(year, month, day) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        if (len == 22)
        {
            return true;
        }

        if (text[19] != '.' || len == 23 || text[len - 4] == '0')
        {
            return false;
        }

        for (var index = 20; index < len - 3; index++)
        {
            if ((uint)(text[index] - '0') > 9u)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsCanonicalTimestamptzUtf8(ReadOnlySpan<byte> utf8)
    {
        var len = utf8.Length;
        if (len is < 22 or > 29)
        {
            return false;
        }

        if (utf8[4] != (byte)'-' || utf8[7] != (byte)'-' || utf8[10] != (byte)' ' || utf8[13] != (byte)':' || utf8[16] != (byte)':'
            || utf8[len - 3] != (byte)'+' || utf8[len - 2] != (byte)'0' || utf8[len - 1] != (byte)'0')
        {
            return false;
        }

        if (!TryDigits4Utf8(utf8, 0, out var year) || !TryDigits2Utf8(utf8, 5, out var month) || !TryDigits2Utf8(utf8, 8, out var day)
            || !TryDigits2Utf8(utf8, 11, out var hour) || !TryDigits2Utf8(utf8, 14, out var minute) || !TryDigits2Utf8(utf8, 17, out var second)
            || !IsValidDate(year, month, day) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        if (len == 22)
        {
            return true;
        }

        if (utf8[19] != (byte)'.' || len == 23 || utf8[len - 4] == (byte)'0')
        {
            return false;
        }

        for (var index = 20; index < len - 3; index++)
        {
            if ((uint)(utf8[index] - '0') > 9u)
            {
                return false;
            }
        }

        return true;
    }

    public static string Format(int oid, DateTime value)
    {
        Span<char> buffer = stackalloc char[32];
        var length = FormatChars(oid, value.Ticks, buffer);
        return new string(buffer[..length]);
    }

    public static void WriteText(int oid, DateTime value, PgMessageBuffer output)
    {
        var destination = output.GetSpan(32);
        var length = FormatUtf8(oid, value.Ticks, destination);
        output.Advance(length);
    }

    public static void WriteBinary(int oid, DateTime value, PgMessageBuffer output)
    {
        if (oid == PgOid.Date)
        {
            var days = (int)(value.Ticks / TimeSpan.TicksPerDay);
            output.WriteInt32(days - EpochDay);
            return;
        }

        if (oid == PgOid.Time)
        {
            output.WriteInt64((value.Ticks % TimeSpan.TicksPerDay) / TimeSpan.TicksPerMicrosecond);
            return;
        }

        output.WriteInt64((value.Ticks - EpochTicks) / TimeSpan.TicksPerMicrosecond);
    }

    public static bool TryReadBinary(int oid, ReadOnlySpan<byte> value, out string text)
    {
        text = string.Empty;
        if (oid == PgOid.Date && value.Length == 4)
        {
            var days = (long)BinaryPrimitives.ReadInt32BigEndian(value) + EpochDay;
            if (days < 0 || days > 3652058)
            {
                return false;
            }

            CivilFromDays((int)days, out var year, out var month, out var day);
            Span<char> buffer = stackalloc char[10];
            Write4(buffer, 0, year);
            buffer[4] = '-';
            Write2(buffer, 5, month);
            buffer[7] = '-';
            Write2(buffer, 8, day);
            text = new string(buffer);
            return true;
        }

        if (value.Length != 8)
        {
            return false;
        }

        var micros = BinaryPrimitives.ReadInt64BigEndian(value);
        if (oid == PgOid.Time)
        {
            if (micros < 0 || micros >= MicrosPerDay)
            {
                return false;
            }

            Span<char> buffer = stackalloc char[16];
            var length = FormatChars(oid, EpochTicks + (micros * TimeSpan.TicksPerMicrosecond), buffer);
            text = new string(buffer[..length]);
            return true;
        }

        if (micros < MinMicros || micros > MaxMicros)
        {
            return false;
        }

        Span<char> tsBuffer = stackalloc char[32];
        var tsLength = FormatChars(oid, EpochTicks + (micros * TimeSpan.TicksPerMicrosecond), tsBuffer);
        text = new string(tsBuffer[..tsLength]);
        return true;
    }

    private static bool TryParseTime(ReadOnlySpan<char> text, out DateTime value)
    {
        value = default;
        if (text.Length < 5 || text[2] != ':' || !TryDigits2(text, 0, out var hour) || !TryDigits2(text, 3, out var minute)
            || hour > 23 || minute > 59)
        {
            return false;
        }

        var second = 0;
        long fracTicks = 0;
        var cursor = 5;
        if (cursor < text.Length)
        {
            if (text[cursor] != ':' || cursor + 3 > text.Length || !TryDigits2(text, cursor + 1, out second) || second > 59)
            {
                return false;
            }

            cursor += 3;
            if (cursor < text.Length)
            {
                if (text[cursor] != '.' || !TryFractionTicks(text, ref cursor, out fracTicks))
                {
                    return false;
                }
            }
        }

        if (cursor != text.Length)
        {
            return false;
        }

        var ticks = EpochTicks + ((((hour * 60L) + minute) * 60L + second) * TimeSpan.TicksPerSecond) + fracTicks;
        value = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static bool TryParseTimeUtf8(ReadOnlySpan<byte> text, out DateTime value)
    {
        value = default;
        if (text.Length < 5 || text[2] != (byte)':' || !TryDigits2Utf8(text, 0, out var hour) || !TryDigits2Utf8(text, 3, out var minute)
            || hour > 23 || minute > 59)
        {
            return false;
        }

        var second = 0;
        long fracTicks = 0;
        var cursor = 5;
        if (cursor < text.Length)
        {
            if (text[cursor] != (byte)':' || cursor + 3 > text.Length || !TryDigits2Utf8(text, cursor + 1, out second) || second > 59)
            {
                return false;
            }

            cursor += 3;
            if (cursor < text.Length)
            {
                if (text[cursor] != (byte)'.' || !TryFractionTicksUtf8(text, ref cursor, out fracTicks))
                {
                    return false;
                }
            }
        }

        if (cursor != text.Length)
        {
            return false;
        }

        var ticks = EpochTicks + ((((hour * 60L) + minute) * 60L + second) * TimeSpan.TicksPerSecond) + fracTicks;
        value = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static bool TryParseDateOrTimestamp(int oid, ReadOnlySpan<char> text, out DateTime value)
    {
        value = default;
        if (text.Length < 10 || text[4] != '-' || text[7] != '-'
            || !TryDigits4(text, 0, out var year)
            || !TryDigits2(text, 5, out var month)
            || !TryDigits2(text, 8, out var day)
            || !IsValidDate(year, month, day))
        {
            return false;
        }

        var hour = 0;
        var minute = 0;
        var second = 0;
        long fracTicks = 0;
        var cursor = 10;
        if (cursor < text.Length && text[cursor] is ' ' or 'T' && cursor + 1 < text.Length && char.IsAsciiDigit(text[cursor + 1]))
        {
            cursor++;
            if (cursor + 5 > text.Length || text[cursor + 2] != ':'
                || !TryDigits2(text, cursor, out hour) || !TryDigits2(text, cursor + 3, out minute)
                || hour > 23 || minute > 59)
            {
                return false;
            }

            cursor += 5;
            if (cursor < text.Length && text[cursor] == ':')
            {
                if (cursor + 3 > text.Length || !TryDigits2(text, cursor + 1, out second) || second > 59)
                {
                    return false;
                }

                cursor += 3;
                if (cursor < text.Length && text[cursor] == '.')
                {
                    if (!TryFractionTicks(text, ref cursor, out fracTicks))
                    {
                        return false;
                    }
                }
            }
        }

        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        var offsetMinutes = 0;
        if (cursor < text.Length)
        {
            var marker = text[cursor];
            if (marker is 'Z' or 'z')
            {
                if (cursor + 1 != text.Length)
                {
                    return false;
                }
            }
            else if (marker is '+' or '-')
            {
                var sign = marker == '-' ? -1 : 1;
                var remaining = text.Length - cursor;
                if (remaining == 3)
                {
                    if (!TryDigits2(text, cursor + 1, out var offsetHours))
                    {
                        return false;
                    }

                    offsetMinutes = sign * offsetHours * 60;
                }
                else if (remaining == 5)
                {
                    if (!TryDigits2(text, cursor + 1, out var offsetHours) || !TryDigits2(text, cursor + 3, out var offsetMins))
                    {
                        return false;
                    }

                    offsetMinutes = sign * ((offsetHours * 60) + offsetMins);
                }
                else if (remaining == 6 && text[cursor + 3] == ':')
                {
                    if (!TryDigits2(text, cursor + 1, out var offsetHours) || !TryDigits2(text, cursor + 4, out var offsetMins))
                    {
                        return false;
                    }

                    offsetMinutes = sign * ((offsetHours * 60) + offsetMins);
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return BuildDateTime(oid, year, month, day, hour, minute, second, fracTicks, offsetMinutes, out value);
    }

    private static bool TryParseDateOrTimestampUtf8(int oid, ReadOnlySpan<byte> text, out DateTime value)
    {
        value = default;
        if (text.Length < 10 || text[4] != (byte)'-' || text[7] != (byte)'-'
            || !TryDigits4Utf8(text, 0, out var year)
            || !TryDigits2Utf8(text, 5, out var month)
            || !TryDigits2Utf8(text, 8, out var day)
            || !IsValidDate(year, month, day))
        {
            return false;
        }

        var hour = 0;
        var minute = 0;
        var second = 0;
        long fracTicks = 0;
        var cursor = 10;
        if (cursor < text.Length && text[cursor] is (byte)' ' or (byte)'T' && cursor + 1 < text.Length && (uint)(text[cursor + 1] - '0') <= 9u)
        {
            cursor++;
            if (cursor + 5 > text.Length || text[cursor + 2] != (byte)':'
                || !TryDigits2Utf8(text, cursor, out hour) || !TryDigits2Utf8(text, cursor + 3, out minute)
                || hour > 23 || minute > 59)
            {
                return false;
            }

            cursor += 5;
            if (cursor < text.Length && text[cursor] == (byte)':')
            {
                if (cursor + 3 > text.Length || !TryDigits2Utf8(text, cursor + 1, out second) || second > 59)
                {
                    return false;
                }

                cursor += 3;
                if (cursor < text.Length && text[cursor] == (byte)'.')
                {
                    if (!TryFractionTicksUtf8(text, ref cursor, out fracTicks))
                    {
                        return false;
                    }
                }
            }
        }

        while (cursor < text.Length && IsAsciiSpace(text[cursor]))
        {
            cursor++;
        }

        var offsetMinutes = 0;
        if (cursor < text.Length)
        {
            var marker = text[cursor];
            if (marker is (byte)'Z' or (byte)'z')
            {
                if (cursor + 1 != text.Length)
                {
                    return false;
                }
            }
            else if (marker is (byte)'+' or (byte)'-')
            {
                var sign = marker == (byte)'-' ? -1 : 1;
                var remaining = text.Length - cursor;
                if (remaining == 3)
                {
                    if (!TryDigits2Utf8(text, cursor + 1, out var offsetHours))
                    {
                        return false;
                    }

                    offsetMinutes = sign * offsetHours * 60;
                }
                else if (remaining == 5)
                {
                    if (!TryDigits2Utf8(text, cursor + 1, out var offsetHours) || !TryDigits2Utf8(text, cursor + 3, out var offsetMins))
                    {
                        return false;
                    }

                    offsetMinutes = sign * ((offsetHours * 60) + offsetMins);
                }
                else if (remaining == 6 && text[cursor + 3] == (byte)':')
                {
                    if (!TryDigits2Utf8(text, cursor + 1, out var offsetHours) || !TryDigits2Utf8(text, cursor + 4, out var offsetMins))
                    {
                        return false;
                    }

                    offsetMinutes = sign * ((offsetHours * 60) + offsetMins);
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return BuildDateTime(oid, year, month, day, hour, minute, second, fracTicks, offsetMinutes, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BuildDateTime(int oid, int year, int month, int day, int hour, int minute, int second, long fracTicks, int offsetMinutes, out DateTime value)
    {
        var days = DaysFromCivil(year, month, day);
        if (oid == PgOid.Date)
        {
            value = new DateTime(days * TimeSpan.TicksPerDay, DateTimeKind.Utc);
            return true;
        }

        var ticks = (days * TimeSpan.TicksPerDay) + ((((hour * 60L) + minute) * 60L + second) * TimeSpan.TicksPerSecond) + fracTicks;
        if (oid == PgOid.Timestamptz && offsetMinutes != 0)
        {
            ticks -= offsetMinutes * TimeSpan.TicksPerMinute;
            if ((ulong)ticks > (ulong)DateTime.MaxValue.Ticks)
            {
                value = default;
                return false;
            }
        }

        value = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidDate(int year, int month, int day)
    {
        if (year < 1 || month is < 1 or > 12 || day < 1)
        {
            return false;
        }

        if (month != 2)
        {
            return day <= DaysPerMonth[month];
        }

        var leap = (year & 3) == 0 && (year % 100 != 0 || year % 400 == 0);
        return day <= (leap ? 29 : 28);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DaysFromCivil(int year, int month, int day)
    {
        var y = month <= 2 ? year - 1 : year;
        var era = y / 400;
        var yoe = y - (era * 400);
        var doy = ((153 * (month + (month > 2 ? -3 : 9)) + 2) / 5) + day - 1;
        var doe = (yoe * 365) + (yoe / 4) - (yoe / 100) + doy;
        return (era * 146097L) + doe - 306L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CivilFromDays(int days, out int year, out int month, out int day)
    {
        var z = days + 306;
        var era = z / 146097;
        var doe = z - (era * 146097);
        var yoe = (doe - (doe / 1460) + (doe / 36524) - (doe / 146096)) / 365;
        var y = yoe + (era * 400);
        var doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
        var mp = ((5 * doy) + 2) / 153;
        day = doy - (((153 * mp) + 2) / 5) + 1;
        month = mp + (mp < 10 ? 3 : -9);
        year = y + (month <= 2 ? 1 : 0);
    }

    private static bool TryFractionTicks(ReadOnlySpan<char> text, ref int cursor, out long ticks)
    {
        ticks = 0;
        cursor++;
        var digits = 0;
        var scale = 1_000_000L;
        while (cursor < text.Length && (uint)(text[cursor] - '0') <= 9u)
        {
            if (digits >= 7)
            {
                return false;
            }

            ticks += (text[cursor] - '0') * scale;
            scale /= 10;
            digits++;
            cursor++;
        }

        return digits > 0;
    }

    private static bool TryFractionTicksUtf8(ReadOnlySpan<byte> text, ref int cursor, out long ticks)
    {
        ticks = 0;
        cursor++;
        var digits = 0;
        var scale = 1_000_000L;
        while (cursor < text.Length && (uint)(text[cursor] - '0') <= 9u)
        {
            if (digits >= 7)
            {
                return false;
            }

            ticks += (text[cursor] - '0') * scale;
            scale /= 10;
            digits++;
            cursor++;
        }

        return digits > 0;
    }

    private static int FormatChars(int oid, long ticks, Span<char> buffer)
    {
        var pos = 0;
        var days = (int)Math.DivRem(ticks, TimeSpan.TicksPerDay, out var timeOfDayTicks);
        if (oid != PgOid.Time)
        {
            CivilFromDays(days, out var year, out var month, out var day);
            Write4(buffer, pos, year);
            buffer[pos + 4] = '-';
            Write2(buffer, pos + 5, month);
            buffer[pos + 7] = '-';
            Write2(buffer, pos + 8, day);
            pos += 10;
            if (oid == PgOid.Date)
            {
                return pos;
            }

            buffer[pos++] = ' ';
        }

        var totalSeconds = (int)Math.DivRem(timeOfDayTicks, TimeSpan.TicksPerSecond, out var subSecondTicks);
        var totalMinutes = Math.DivRem(totalSeconds, 60, out var second);
        var hour = Math.DivRem(totalMinutes, 60, out var minute);
        Write2(buffer, pos, hour);
        buffer[pos + 2] = ':';
        Write2(buffer, pos + 3, minute);
        buffer[pos + 5] = ':';
        Write2(buffer, pos + 6, second);
        pos += 8;

        var micros = (int)(subSecondTicks / TimeSpan.TicksPerMicrosecond);
        if (micros > 0)
        {
            buffer[pos++] = '.';
            var fracStart = pos;
            Write2(buffer, pos, micros / 10000);
            Write2(buffer, pos + 2, (micros / 100) % 100);
            Write2(buffer, pos + 4, micros % 100);
            pos += 6;
            while (pos > fracStart && buffer[pos - 1] == '0')
            {
                pos--;
            }
        }

        if (oid == PgOid.Timestamptz)
        {
            buffer[pos++] = '+';
            buffer[pos++] = '0';
            buffer[pos++] = '0';
        }

        return pos;
    }

    private static int FormatUtf8(int oid, long ticks, Span<byte> buffer)
    {
        var pos = 0;
        var days = (int)Math.DivRem(ticks, TimeSpan.TicksPerDay, out var timeOfDayTicks);
        if (oid != PgOid.Time)
        {
            CivilFromDays(days, out var year, out var month, out var day);
            Write4Utf8(buffer, pos, year);
            buffer[pos + 4] = (byte)'-';
            Write2Utf8(buffer, pos + 5, month);
            buffer[pos + 7] = (byte)'-';
            Write2Utf8(buffer, pos + 8, day);
            pos += 10;
            if (oid == PgOid.Date)
            {
                return pos;
            }

            buffer[pos++] = (byte)' ';
        }

        var totalSeconds = (int)Math.DivRem(timeOfDayTicks, TimeSpan.TicksPerSecond, out var subSecondTicks);
        var totalMinutes = Math.DivRem(totalSeconds, 60, out var second);
        var hour = Math.DivRem(totalMinutes, 60, out var minute);
        Write2Utf8(buffer, pos, hour);
        buffer[pos + 2] = (byte)':';
        Write2Utf8(buffer, pos + 3, minute);
        buffer[pos + 5] = (byte)':';
        Write2Utf8(buffer, pos + 6, second);
        pos += 8;

        var micros = (int)(subSecondTicks / TimeSpan.TicksPerMicrosecond);
        if (micros > 0)
        {
            buffer[pos++] = (byte)'.';
            var fracStart = pos;
            Write2Utf8(buffer, pos, micros / 10000);
            Write2Utf8(buffer, pos + 2, (micros / 100) % 100);
            Write2Utf8(buffer, pos + 4, micros % 100);
            pos += 6;
            while (pos > fracStart && buffer[pos - 1] == (byte)'0')
            {
                pos--;
            }
        }

        if (oid == PgOid.Timestamptz)
        {
            buffer[pos++] = (byte)'+';
            buffer[pos++] = (byte)'0';
            buffer[pos++] = (byte)'0';
        }

        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryDigits2(ReadOnlySpan<char> text, int index, out int result)
    {
        var a = text[index] - '0';
        var b = text[index + 1] - '0';
        if ((uint)a > 9u || (uint)b > 9u)
        {
            result = 0;
            return false;
        }

        result = (a * 10) + b;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryDigits4(ReadOnlySpan<char> text, int index, out int result)
    {
        var a = text[index] - '0';
        var b = text[index + 1] - '0';
        var c = text[index + 2] - '0';
        var d = text[index + 3] - '0';
        if ((uint)a > 9u || (uint)b > 9u || (uint)c > 9u || (uint)d > 9u)
        {
            result = 0;
            return false;
        }

        result = (a * 1000) + (b * 100) + (c * 10) + d;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryDigits2Utf8(ReadOnlySpan<byte> text, int index, out int result)
    {
        var a = text[index] - '0';
        var b = text[index + 1] - '0';
        if ((uint)a > 9u || (uint)b > 9u)
        {
            result = 0;
            return false;
        }

        result = (a * 10) + b;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryDigits4Utf8(ReadOnlySpan<byte> text, int index, out int result)
    {
        var a = text[index] - '0';
        var b = text[index + 1] - '0';
        var c = text[index + 2] - '0';
        var d = text[index + 3] - '0';
        if ((uint)a > 9u || (uint)b > 9u || (uint)c > 9u || (uint)d > 9u)
        {
            result = 0;
            return false;
        }

        result = (a * 1000) + (b * 100) + (c * 10) + d;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write2(Span<char> buffer, int index, int value)
    {
        buffer[index] = (char)('0' + (value / 10));
        buffer[index + 1] = (char)('0' + (value % 10));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write4(Span<char> buffer, int index, int value)
    {
        buffer[index] = (char)('0' + (value / 1000));
        buffer[index + 1] = (char)('0' + ((value / 100) % 10));
        buffer[index + 2] = (char)('0' + ((value / 10) % 10));
        buffer[index + 3] = (char)('0' + (value % 10));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write2Utf8(Span<byte> buffer, int index, int value)
    {
        buffer[index] = (byte)('0' + (value / 10));
        buffer[index + 1] = (byte)('0' + (value % 10));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write4Utf8(Span<byte> buffer, int index, int value)
    {
        buffer[index] = (byte)('0' + (value / 1000));
        buffer[index + 1] = (byte)('0' + ((value / 100) % 10));
        buffer[index + 2] = (byte)('0' + ((value / 10) % 10));
        buffer[index + 3] = (byte)('0' + (value % 10));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiSpace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v';
}
