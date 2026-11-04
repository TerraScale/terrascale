using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeHintAuth(string Type, string Token);
internal sealed record ShapeHintFrame(string Type, string? Handle = null, string? Offset = null, string? NextOffset = null,
    ShapeMirrorManifest? Manifest = null, string? Code = null, int? RetryAfter = null);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ShapeHintAuth))]
[JsonSerializable(typeof(ShapeHintFrame))]
internal sealed partial class ShapeHintJsonContext : JsonSerializerContext;
