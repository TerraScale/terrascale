namespace TerraScale.DotNet;

public sealed record TerraScaleAuthClientOptions
{
    private const int MaxSlugLength = 255;

    public Uri? BaseAddress { get; set; }

    public string? InstanceSlug { get; set; }

    public override string ToString() => "TerraScale auth client options";

    internal (Uri Address, string Slug) Validate()
    {
        if (BaseAddress is not { } address)
        {
            throw new ArgumentException("The ts-auth base address must be configured.", nameof(BaseAddress));
        }

        return (TerraScaleOrigin.Parse(address, "The ts-auth base address", nameof(BaseAddress), TerraScaleService.Auth), ValidateSlug(InstanceSlug));
    }

    private static string ValidateSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException("The ts-auth instance slug must be configured.", nameof(InstanceSlug));
        }

        var trimmed = slug.Trim();
        if (trimmed.Any(char.IsControl) || trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The ts-auth instance slug must not contain control or whitespace characters.", nameof(InstanceSlug));
        }

        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed is "." or "..")
        {
            throw new ArgumentException("The ts-auth instance slug must not contain path separators or dot segments.", nameof(InstanceSlug));
        }

        if (trimmed.Length > MaxSlugLength)
        {
            throw new ArgumentException($"The ts-auth instance slug must be at most {MaxSlugLength} characters.", nameof(InstanceSlug));
        }

        return trimmed;
    }
}
