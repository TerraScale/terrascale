using System.Globalization;
using System.Numerics;
using System.Text;

namespace TerraScale.TerraBase.Primitives;

/// <summary>
/// An exact decimal, <c>Mantissa × 10^Exponent</c>: the one number rule for client JSON and the arithmetic of
/// <c>inc</c>, which never rounds.
/// </summary>
/// <param name="Mantissa">Digits without trailing zeros.</param>
/// <param name="Exponent">Power of ten.</param>
internal readonly record struct ExactNumber(BigInteger Mantissa, int Exponent)
{
    private const int MaxSignificantDigits = 128;

    // Decimal exponent of the leading digit: inside the normal double range, so SQLite never reads an infinity.
    private const int MinMagnitude = -307;
    private const int MaxMagnitude = 307;

    /// <summary>
    /// Parses JSON number text with at most 128 significant digits (counted before parsing, so the work is bounded)
    /// and a value that is 0 or between 1e-307 and below 1e308 in magnitude.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> text, out ExactNumber number)
    {
        number = default;
        var e = text.IndexOfAny((byte)'e', (byte)'E');
        var mantissa = e < 0 ? text : text[..e];
        long exponent = 0;
        // A bounded exponent keeps the arithmetic below free of overflow; the magnitude check decides the rest.
        if (e >= 0 && (!long.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
            || exponent is < -1_000_000 or > 1_000_000))
        {
            return false;
        }

        var digits = new StringBuilder(mantissa.Length);
        var negative = false;
        foreach (var c in mantissa)
        {
            if (c == '-')
            {
                negative = true;
            }
            else if (c == '.')
            {
                exponent -= mantissa.Length - mantissa.IndexOf((byte)'.') - 1;
            }
            else if (digits.Length > 0 || c != '0')
            {
                if (digits.Length == MaxSignificantDigits)
                {
                    return false;
                }

                digits.Append((char)c);
            }
        }

        if (digits.Length == 0)
        {
            return true;
        }

        var value = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        var magnitude = exponent + digits.Length - 1;
        if (magnitude is < MinMagnitude or > MaxMagnitude)
        {
            return false;
        }

        number = Normalize(negative ? -value : value, (int)exponent);
        return true;
    }

    /// <summary>Parses JSON number text; see <see cref="TryParse(ReadOnlySpan{byte}, out ExactNumber)"/>.</summary>
    public static bool TryParse(string text, out ExactNumber number) => TryParse(Encoding.UTF8.GetBytes(text), out number);

    /// <summary>The exact sum; the document limits then decide whether it can be stored.</summary>
    public static ExactNumber Add(ExactNumber a, ExactNumber b)
    {
        if (a.Mantissa.IsZero || b.Mantissa.IsZero)
        {
            return a.Mantissa.IsZero ? b : a;
        }

        // Valid operands keep exponents within about 750 of each other, so the alignment stays small.
        var low = Math.Min(a.Exponent, b.Exponent);
        return Normalize((a.Mantissa * BigInteger.Pow(10, a.Exponent - low)) + (b.Mantissa * BigInteger.Pow(10, b.Exponent - low)), low);
    }

    /// <summary>Plain JSON number text: digits and a decimal point, or an exponent for large integers.</summary>
    public override string ToString()
    {
        var digits = BigInteger.Abs(Mantissa).ToString(CultureInfo.InvariantCulture);
        var sign = Mantissa.Sign < 0 ? "-" : "";
        if (Exponent >= 0)
        {
            return sign + (digits.Length + Exponent <= MaxSignificantDigits ? digits + new string('0', Exponent) : $"{digits}e{Exponent}");
        }

        var point = digits.Length + Exponent;
        return sign + (point > 0 ? $"{digits[..point]}.{digits[point..]}" : $"0.{new string('0', -point)}{digits}");
    }

    private static ExactNumber Normalize(BigInteger mantissa, int exponent)
    {
        if (mantissa.IsZero)
        {
            return default;
        }

        while (mantissa % 10 == 0)
        {
            mantissa /= 10;
            exponent++;
        }

        return new ExactNumber(mantissa, exponent);
    }
}
