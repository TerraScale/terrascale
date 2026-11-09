using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.ApiKeys;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(ApiKey))]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(CreatedApiKeyBody))]
[JsonSerializable(typeof(ProjectKeyEntry))]
internal sealed partial class ApiKeysJsonContext : JsonSerializerContext;
