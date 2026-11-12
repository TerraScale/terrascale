using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Source-generated JSON metadata for the ts-auth client. All wire names are
/// pinned with <see cref="JsonPropertyNameAttribute"/> because the service
/// uses explicit wire names for each operation.
/// </summary>
[JsonSerializable(typeof(TerraScaleAuthOidcDiscovery))]
[JsonSerializable(typeof(TerraScaleAuthJwkSet))]
[JsonSerializable(typeof(TerraScaleAuthJwk))]
[JsonSerializable(typeof(TerraScaleAuthTokenResponse))]
[JsonSerializable(typeof(TerraScaleAuthUserInfo))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
internal partial class TerraScaleAuthJsonContext : JsonSerializerContext;
