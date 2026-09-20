using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Instance record and hosted-UI branding settings.</summary>
public sealed class TerraScaleAuthInstanceClient(TerraScaleAuthClient client)
{
    /// <summary>Gets the instance record including its creation time.</summary>
    public Task<TerraScaleAuthInstanceRecord> GetAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/instance"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthInstanceRecord,
            cancellationToken);

    /// <summary>
    /// Updates the hosted-UI branding fields. Absent or null arguments leave a
    /// field unchanged; <paramref name="logoUrl"/> must be an absolute http(s)
    /// URL and <paramref name="themeColor"/> a <c>#</c> plus six hex digits.
    /// </summary>
    public Task<TerraScaleAuthInstanceSettings> UpdateAsync(
        string? name = null,
        string? logoUrl = null,
        string? themeColor = null,
        CancellationToken cancellationToken = default) =>
        client.SendJsonAsync<TerraScaleAuthUpdateInstance, TerraScaleAuthInstanceSettings>(
            HttpMethod.Patch,
            client.ManagementRoute("/instance"),
            new TerraScaleAuthUpdateInstance(name, logoUrl, themeColor),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUpdateInstance,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthInstanceSettings,
            cancellationToken);
}

/// <summary>The instance record as returned by GET, including its creation time.</summary>
public sealed record TerraScaleAuthInstanceRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("logo_url")] string? LogoUrl,
    [property: JsonPropertyName("theme_color")] string? ThemeColor,
    [property: JsonPropertyName("created_at")] long CreatedAt);

/// <summary>The instance settings as returned by PATCH, which omits <c>created_at</c>.</summary>
public sealed record TerraScaleAuthInstanceSettings(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("logo_url")] string? LogoUrl,
    [property: JsonPropertyName("theme_color")] string? ThemeColor);

internal sealed record TerraScaleAuthUpdateInstance(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("logo_url")] string? LogoUrl,
    [property: JsonPropertyName("theme_color")] string? ThemeColor);
