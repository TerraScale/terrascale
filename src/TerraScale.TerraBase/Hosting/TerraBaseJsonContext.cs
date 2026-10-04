using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Features.Health;

namespace TerraScale.TerraBase.Hosting;

/// <summary>Source-generated JSON metadata for every request and response type (fast startup, no reflection-based serialization).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StatusBody))]
[JsonSerializable(typeof(ApiKey))]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(CreatedApiKeyBody))]
[JsonSerializable(typeof(DatabaseBody))]
[JsonSerializable(typeof(DocumentBody))]
[JsonSerializable(typeof(DocumentPageBody))]
[JsonSerializable(typeof(DocumentVersionBody))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
internal sealed partial class TerraBaseJsonContext : JsonSerializerContext;
