using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TsAuthOrganization))]
[JsonSerializable(typeof(TsAuthOrganizationRequest))]
[JsonSerializable(typeof(TsAuthOrganizationMembership))]
[JsonSerializable(typeof(TsAuthOrganizationMembershipRequest))]
[JsonSerializable(typeof(TsAuthOrganizationInvitation))]
[JsonSerializable(typeof(TsAuthOrganizationInvitationRequest))]
[JsonSerializable(typeof(TsAuthOrganizationDomain))]
[JsonSerializable(typeof(TsAuthOrganizationDomainRequest))]
[JsonSerializable(typeof(TsAuthOrganizationPage))]
[JsonSerializable(typeof(TsAuthOrganizationMembershipPage))]
[JsonSerializable(typeof(TsAuthOrganizationInvitationPage))]
[JsonSerializable(typeof(TsAuthOrganizationDomainPage))]
internal sealed partial class TsAuthOrganizationsJson : JsonSerializerContext;
