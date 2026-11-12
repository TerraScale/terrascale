using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TerraScaleAuthOrganization))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationRequest))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationMembership))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationMembershipRequest))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationInvitation))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationInvitationRequest))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationDomain))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationDomainRequest))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationPage))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationMembershipPage))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationInvitationPage))]
[JsonSerializable(typeof(TerraScaleAuthOrganizationDomainPage))]
internal sealed partial class TerraScaleAuthOrganizationsJson : JsonSerializerContext;
