namespace TerraScale.DotNet;

internal static class TerraScalePaths
{
    public static string Segment(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value is "." or ".." || value.AsSpan().ContainsAny('/', '\\') || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} must be one path segment: not \".\" or \"..\", without slashes or control characters.", name);
        }

        return Uri.EscapeDataString(value);
    }
}
