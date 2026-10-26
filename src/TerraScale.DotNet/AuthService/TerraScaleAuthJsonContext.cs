using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Source-generated JSON metadata for the ts-auth client. All wire names are
/// pinned with <see cref="JsonPropertyNameAttribute"/> because the service
/// mixes snake_case management fields with camelCase oauth-client fields.
/// </summary>
[JsonSerializable(typeof(TerraScaleAuthUser))]
[JsonSerializable(typeof(TerraScaleAuthUserPage))]
[JsonSerializable(typeof(TerraScaleAuthCreateUser))]
[JsonSerializable(typeof(TerraScaleAuthSession))]
[JsonSerializable(typeof(TerraScaleAuthSessionPage))]
[JsonSerializable(typeof(TerraScaleAuthImpersonation))]
[JsonSerializable(typeof(TerraScaleAuthEmptyBody))]
[JsonSerializable(typeof(TerraScaleAuthMfaFactor))]
[JsonSerializable(typeof(TerraScaleAuthMfaFactorPage))]
[JsonSerializable(typeof(TerraScaleAuthRecoveryCodes))]
[JsonSerializable(typeof(TerraScaleAuthPasskey))]
[JsonSerializable(typeof(TerraScaleAuthPasskeyPage))]
[JsonSerializable(typeof(TerraScaleAuthUserOrganization))]
[JsonSerializable(typeof(IReadOnlyList<TerraScaleAuthUserOrganization>))]
[JsonSerializable(typeof(TerraScaleAuthOrganization))]
[JsonSerializable(typeof(TerraScaleAuthWebhook))]
[JsonSerializable(typeof(IReadOnlyList<TerraScaleAuthWebhook>))]
[JsonSerializable(typeof(TerraScaleAuthCreatedWebhook))]
[JsonSerializable(typeof(TerraScaleAuthCreateWebhook))]
[JsonSerializable(typeof(TerraScaleAuthWebhookTest))]
[JsonSerializable(typeof(TerraScaleAuthDeadDelivery))]
[JsonSerializable(typeof(IReadOnlyList<TerraScaleAuthDeadDelivery>))]
[JsonSerializable(typeof(TerraScaleAuthReplayResult))]
[JsonSerializable(typeof(TerraScaleAuthAuditEvent))]
[JsonSerializable(typeof(IReadOnlyList<TerraScaleAuthAuditEvent>))]
[JsonSerializable(typeof(TerraScaleAuthInstanceRecord))]
[JsonSerializable(typeof(TerraScaleAuthInstanceSettings))]
[JsonSerializable(typeof(TerraScaleAuthUpdateInstance))]
[JsonSerializable(typeof(TerraScaleAuthOidcDiscovery))]
[JsonSerializable(typeof(TerraScaleAuthJwkSet))]
[JsonSerializable(typeof(TerraScaleAuthJwk))]
[JsonSerializable(typeof(TerraScaleAuthTokenResponse))]
[JsonSerializable(typeof(TerraScaleAuthUserInfo))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
internal partial class TerraScaleAuthJsonContext : JsonSerializerContext;
