namespace TerraScale.Dashboard.Platform.Identity
{
    internal static class DeviceLabel
    {
        public static string Parse(string? userAgent)
        {
            return Browser(userAgent) + " on " + OperatingSystem(userAgent);
        }

        private static string Browser(string? userAgent)
        {
            if (userAgent is null)
            {
                return "Unknown browser";
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

            return "Unknown browser";
        }

        private static string OperatingSystem(string? userAgent)
        {
            if (userAgent is null)
            {
                return "unknown OS";
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

            return "unknown OS";
        }
    }
}
