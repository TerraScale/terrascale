using System.Globalization;

namespace TerraScale.Dashboard.Shared.Formatting;

public static class MoneyFormat
{
    public static string Money(long minorUnits, string currency = "usd")
    {
        // Stripe represents ISK and UGX charges with two decimal places.
        var zeroDecimal = currency.ToLowerInvariant() is "bif" or "clp" or "djf" or "gnf" or "jpy" or "kmf" or "krw" or
            "mga" or "pyg" or "rwf" or "vnd" or "vuv" or "xaf" or "xof" or "xpf";
        var amount = Math.Abs((decimal)minorUnits / (zeroDecimal ? 1m : 100m))
            .ToString(zeroDecimal ? "F0" : "F2", CultureInfo.InvariantCulture);
        var prefix = currency.Equals("usd", StringComparison.OrdinalIgnoreCase) ? "$" : currency.Length == 0 ? "" : currency.ToUpperInvariant() + " ";
        return (minorUnits < 0 ? "-" : "") + prefix + amount + (currency.Length == 0 ? " (currency unreported)" : "");
    }
}
