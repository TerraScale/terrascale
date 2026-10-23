using System.Globalization;
using System.Text;

namespace TerraBase.Engine.Sqlite;

/// <summary>SQLite's text conversion and <c>||</c>, bounded (see <see cref="SqlProgram.Concat"/>).</summary>
internal static class SqliteText
{
    /// <summary>The marker a bounded function's failure carries through Turso's error text.</summary>
    public const string TooLarge = "terrabase: string value too large";

    public static string? Concat(object? left, object? right)
    {
        if (Text(left) is not { } a || Text(right) is not { } b)
        {
            return null;
        }

        // UTF-16 length bounds the UTF-8 length from below by a third; check the exact size only when it can pass.
        if ((long)a.Length + b.Length > SqlProgram.MaxStringBytes
            || ((long)a.Length + b.Length) * 3 > SqlProgram.MaxStringBytes && Encoding.UTF8.GetByteCount(a) + Encoding.UTF8.GetByteCount(b) > SqlProgram.MaxStringBytes)
        {
            throw new InvalidOperationException(TooLarge);
        }

        return string.Concat(a, b);
    }

    // SQLite renders an integer in decimal and a real like printf("%!.15g"): 15 significant digits, always with a
    // decimal point or an exponent.
    private static string? Text(object? value) => value switch
    {
        null or DBNull => null,
        string text => text,
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        double real => Real(real),
        byte[] blob => Encoding.UTF8.GetString(blob),
        var other => Convert.ToString(other, CultureInfo.InvariantCulture),
    };

    private static string Real(double real)
    {
        if (double.IsNaN(real))
        {
            return "";
        }

        if (double.IsInfinity(real))
        {
            return real > 0 ? "Inf" : "-Inf";
        }

        var text = real.ToString("G15", CultureInfo.InvariantCulture);
        var exponent = text.IndexOf('E', StringComparison.Ordinal);
        var mantissa = exponent < 0 ? text : text[..exponent];
        if (!mantissa.Contains('.', StringComparison.Ordinal))
        {
            mantissa += ".0";
        }

        return exponent < 0 ? mantissa : mantissa + "e" + text[(exponent + 1)..];
    }
}
