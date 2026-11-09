using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Hosting;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(Ulid))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(IReadOnlyList<JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(TenantContext))]
[JsonSerializable(typeof(string))]
internal sealed partial class TerraBaseJsonContext : JsonSerializerContext;
