using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

internal enum TerraScaleErrorContract
{
    TerraBase,
    DashboardProduct,
}

internal sealed record DashboardErrorResponse(
    [property: JsonPropertyName("error")] DashboardError Error);

internal sealed record DashboardError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("category")] string Category);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardErrorResponse))]
internal partial class DashboardErrorJsonContext : JsonSerializerContext;
