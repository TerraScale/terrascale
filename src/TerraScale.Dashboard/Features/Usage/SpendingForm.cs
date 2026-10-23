using System.Globalization;

namespace TerraScale.Dashboard.Features.Usage;

public static class SpendingForm
{
    public static bool TryParse(IFormCollection form, out long capCents)
    {
        capCents = 0;
        if (form.Keys.Any(key => key is not ("_csrf" or "spendCap" or "initial_spendCap")) ||
            !form.TryGetValue("spendCap", out var values) || values.Count != 1) return false;
        var text = values[0]?.Trim() ?? "";
        if (text.Length == 0) return true;
        var parts = text.Split('.');
        if (parts.Length > 2 || parts[0].Length == 0 || parts.Any(part => part.Any(character => !char.IsAsciiDigit(character))) || parts.Length == 2 && parts[1].Length is not (1 or 2) ||
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dollars) || dollars > long.MaxValue / 100m) return false;
        capCents = (long)(dollars * 100);
        return true;
    }
}
