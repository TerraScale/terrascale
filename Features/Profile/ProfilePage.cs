using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

public sealed record ProfilePageModel(
    string UserId,
    string Email,
    string Name,
    string PhoneNumber,
    string AvatarInitials,
    ImmutableArray<string> Roles,
    ImmutableArray<string> Permissions,
    string ActiveOrganizationName,
    string ActiveOrganizationId,
    string PersonalOrganizationId,
    string ProjectId,
    ImmutableArray<Session> Sessions,
    ImmutableArray<string> MfaFactors,
    ImmutableArray<ProfilePasskeyView> Passkeys,
    string Tab) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static ProfilePageModel From(DashboardStore store, string href, string tab)
    {
        var user = store.CurrentUser;
        return new(
            UserId: user.Id,
            Email: user.Email,
            Name: store.EffectiveDisplayName,
            PhoneNumber: store.ProfilePhoneNumber,
            AvatarInitials: store.Initials(store.EffectiveDisplayName, user.Email),
            Roles: store.ProfileRoles,
            Permissions: store.ProfilePermissions,
            ActiveOrganizationName: store.ActiveOrganizationName,
            ActiveOrganizationId: store.ActiveOrganizationId,
            PersonalOrganizationId: store.PersonalOrganizationId,
            ProjectId: store.ActiveProjectId,
            Sessions: store.Sessions,
            MfaFactors: store.MfaFactors,
            Passkeys: store.Passkeys,
            Tab: tab)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}

public sealed class ProfilePageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/profile");
        Summary("Renders the Profile page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var tab = HttpContext.Request.Query["tab"].FirstOrDefault() is { Length: > 0 } t ? t : "profile";
        return Task.FromResult<IResult>(ProfileSlice.Create(ProfilePageModel.From(store, "/profile", tab)));
    }
}
