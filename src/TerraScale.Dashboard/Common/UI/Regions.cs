namespace TerraScale.Dashboard.Common.UI
{
    public static class Regions
    {
        private static readonly string[] Ordered = ["gru", "fra", "sjc"];

        private static readonly string[] Flags = ["br", "de", "us"];

        private static readonly Dictionary<string, (string Name, string Country)> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gru"] = ("São Paulo", "br"),
            ["fra"] = ("Frankfurt", "de"),
            ["sjc"] = ("San Francisco Bay Area", "us"),
            ["sfo"] = ("San Francisco", "us"),
        };

        public static int Rank(string code)
        {
            var index = Array.FindIndex(Ordered, match => string.Equals(match, code, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? Ordered.Length : index;
        }

        public static string Name(string code)
        {
            return Known.TryGetValue(code, out var known) ? known.Name : "";
        }

        public static string Country(string code)
        {
            return Known.TryGetValue(code, out var known) ? known.Country : "";
        }

        public static string Flag(string country)
        {
            return Array.Find(Flags, flag => string.Equals(flag, country, StringComparison.OrdinalIgnoreCase)) ?? "";
        }

        public static string PingUrl(string code)
        {
            return DatabaseRegion.FromId(code?.ToLowerInvariant()) is { } region ? region.Api + "healthz" : "";
        }
    }
}
