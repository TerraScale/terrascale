namespace TerraScale.Auth.Organizations;

internal static class OrganizationsModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(CreateOrganizationEndpoint),
        typeof(ListOrganizationsEndpoint),
        typeof(GetOrganizationEndpoint),
        typeof(UpdateOrganizationEndpoint),
        typeof(DeleteOrganizationEndpoint),
        typeof(CreateMembershipEndpoint),
        typeof(ListMembershipsEndpoint),
        typeof(UpdateMembershipEndpoint),
        typeof(DeleteMembershipEndpoint),
        typeof(ListUserOrganizationsEndpoint),
        typeof(CreateInvitationEndpoint),
        typeof(ListInvitationsEndpoint),
        typeof(RevokeInvitationEndpoint),
        typeof(AcceptInvitationEndpoint),
        typeof(CreateDomainEndpoint),
        typeof(ListDomainsEndpoint),
        typeof(DeleteDomainEndpoint),
        typeof(EnrollByDomainEndpoint),
    ];
}
