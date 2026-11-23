namespace TerraScale.DotNet;

internal static class TerraScaleAuthPaths
{
    public static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed is "." or "..")
        {
            throw new ArgumentException("Path segments cannot be \".\" or \"..\".", nameof(value));
        }

        return Uri.EscapeDataString(trimmed);
    }
}
