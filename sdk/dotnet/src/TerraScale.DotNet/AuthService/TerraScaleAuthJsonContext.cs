using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

[JsonSerializable(typeof(TerraScaleAuthOidcDiscovery))]
[JsonSerializable(typeof(TerraScaleAuthJwkSet))]
[JsonSerializable(typeof(TerraScaleAuthJwk))]
[JsonSerializable(typeof(TerraScaleAuthTokenResponse))]
[JsonSerializable(typeof(TerraScaleAuthUserInfo))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
internal partial class TerraScaleAuthJsonContext : JsonSerializerContext;
