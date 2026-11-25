using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    [JsonSerializable(typeof(TsAuthOidcDiscovery))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(TsAuthUserInfo))]
    internal sealed partial class TsAuthJsonContext : JsonSerializerContext;
}
