using System.Text.Json.Serialization;
using Cysharp.Serialization.Json;

namespace TerraScale.Platform.Hosting;

public sealed record HealthStatus(string Status, string Region);

public sealed record InternalLogPayload(string Project, string Database, string Id, string Method, string Path, int Status, long DurationMs);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(UlidJsonConverter)])]
[JsonSerializable(typeof(HealthStatus))]
[JsonSerializable(typeof(InternalLogPayload))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class PlatformJsonContext : JsonSerializerContext;
