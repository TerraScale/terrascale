namespace TerraScale.Dashboard.Platform.Identity
{
    internal static class DeviceLabel
    {
        public static string Parse(string? userAgent)
        {
            var browser = Browser(userAgent);
            var operatingSystem = OperatingSystem(userAgent);
            if (browser is null)
            {
                return operatingSystem ?? "Unknown device";
            }
            return operatingSystem is null ? browser : browser + " on " + operatingSystem;
        }

        private static string? Browser(string? userAgent)
        {
            if (userAgent is null)
            {
                return null;
            }

            if (userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase))
            {
                return "Firefox";
            }

            if (userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase))
            {
                return "Edge";
            }

            if (userAgent.Contains("OPR/", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Opera", StringComparison.OrdinalIgnoreCase))
            {
                return "Opera";
            }

            if (userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase))
            {
                return "Chrome";
            }

            if (userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase))
            {
                return "Safari";
            }

            return null;
        }

        private static string? OperatingSystem(string? userAgent)
        {
            if (userAgent is null)
            {
                return null;
            }

            if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase))
            {
                return "iOS";
            }

            if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
            {
                return "Android";
            }

            if (userAgent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
            {
                return "macOS";
            }

            if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows";
            }

            if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("X11", StringComparison.OrdinalIgnoreCase))
            {
                return "Linux";
            }

            return null;
        }
    }
}
