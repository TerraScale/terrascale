using System.Globalization;

namespace TerraScale.Dashboard.Shared.Formatting;

public static class MetricFormat
{
    public static string Milliseconds(double? value) => value is null ? "Not reported" : value.Value.ToString("0.#", CultureInfo.InvariantCulture) + " ms";

    public static string Latency(double? value) => value is null ? "Not reported" : value.Value.ToString("F3", CultureInfo.InvariantCulture) + " ms";

    public static string Bytes(long value)
    {
        if (value < 1024) return value + " B";
        var amount = value / 1024d;
        var index = 0;
        while (amount >= 1023.95 && index < 4) { amount /= 1024; index++; }
        return amount.ToString("F1", CultureInfo.InvariantCulture) + " " + new[] { "KiB", "MiB", "GiB", "TiB", "PiB" }[index];
    }
}
