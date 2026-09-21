using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Profile;

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
    ImmutableArray<string> RecoveryCodes,
    string? TotpEnrollmentToken,
    string? TotpSecret,
    ImmutableArray<ProfilePasskeyView> Passkeys,
    string Locale,
    string LanguageStatus,
    string LoadedAtText,
    string Tab) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static ProfilePageModel From(DashboardStore store, string href, string tab, string? enrollToken = null, bool languageSaved = false)
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
            RecoveryCodes: store.ConsumeRecoveryCodes(),
            TotpEnrollmentToken: enrollToken is { Length: > 0 } ? store.FindTotpEnrollment(enrollToken)?.Token : null,
            TotpSecret: enrollToken is { Length: > 0 } token ? store.FindTotpEnrollment(token)?.Secret : null,
            Passkeys: store.Passkeys,
            Locale: store.AccountLocale,
            LanguageStatus: languageSaved ? "Language preference saved." : "",
            LoadedAtText: DashboardStore.FormatUtcDateTime(DateTimeOffset.UtcNow.ToString("O")),
            Tab: tab)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }
}
