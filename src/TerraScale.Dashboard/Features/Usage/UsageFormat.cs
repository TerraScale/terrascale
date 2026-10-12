using System.Globalization;

namespace TerraScale.Dashboard.Features.Usage;

public static class UsageFormat
{
    private const long KiB = 1L << 10;
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;
    // Preserve the original usage page's exact binary thresholds. Rounded
    // output does not promote a value into the next unit.
    public static string Bytes(long value) => value switch
    {
        < KiB => value.ToString(CultureInfo.InvariantCulture) + " B",
        < MiB => (value / (double)KiB).ToString("F1", CultureInfo.InvariantCulture) + " KiB",
        < GiB => (value / (double)MiB).ToString("F1", CultureInfo.InvariantCulture) + " MiB",
        _ => (value / (double)GiB).ToString("F1", CultureInfo.InvariantCulture) + " GiB"
    };
}
