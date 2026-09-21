namespace TerraScale.Auth.Management;

using TerraScale.Auth.Api;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// <summary>Patch body for instance settings; null fields are left unchanged.</summary>
public sealed record InstanceSettingsPatch(
    string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("logo_url")] string? LogoUrl,
    [property: System.Text.Json.Serialization.JsonPropertyName("theme_color")] string? ThemeColor);
