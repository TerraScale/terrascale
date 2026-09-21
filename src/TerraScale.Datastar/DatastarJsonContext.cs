using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Datastar;

/// <summary>Source-generated metadata for the protocol's built-in JSON values.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, object?>))]
public partial class DatastarJsonContext : JsonSerializerContext;
