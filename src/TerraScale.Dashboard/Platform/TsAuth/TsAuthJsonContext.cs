using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    [JsonSerializable(typeof(TsAuthOidcDiscovery))]
    [JsonSerializable(typeof(TsAuthJwkSet))]
    [JsonSerializable(typeof(TsAuthJwk))]
    [JsonSerializable(typeof(TsAuthUserInfo))]
    internal partial class TsAuthJsonContext : JsonSerializerContext;
}
