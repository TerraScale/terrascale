using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(StoredIssuerKeys))]
[JsonSerializable(typeof(PublicIssuerKeys))]
[JsonSerializable(typeof(TsAuthProjectAuthority))]
[JsonSerializable(typeof(TsAuthOrganizationAuthority))]
[JsonSerializable(typeof(TsAuthAuthoritySubject))]
[JsonSerializable(typeof(TsAuthTokenResponse))]
[JsonSerializable(typeof(TsAuthErrorBody))]
[JsonSerializable(typeof(TsAuthKeyStatus))]
internal sealed partial class UserAuthJsonContext : JsonSerializerContext;
