namespace TerraScale.Dashboard.Shared.UI;

// Display facts for region codes. Codes match case-insensitively; unknown codes get no name or flag.
public static class Regions
{
    private static readonly string[] Ordered = ["gru", "fra", "sjc"];

    private static readonly Dictionary<string, (string Name, string Country)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gru"] = ("São Paulo", "br"),
        ["fra"] = ("Frankfurt", "de"),
        ["sjc"] = ("San Francisco Bay Area", "us"),
        ["sfo"] = ("San Francisco", "us"),
    };

    // gru, fra, sjc first in that order; every other code ties and sorts by the caller.
    public static int Rank(string code)
    {
        var index = Array.FindIndex(Ordered, match => string.Equals(match, code, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? Ordered.Length : index;
    }

    public static string Name(string code) => Known.TryGetValue(code, out var known) ? known.Name : "";

    public static string Country(string code) => Known.TryGetValue(code, out var known) ? known.Country : "";
}
