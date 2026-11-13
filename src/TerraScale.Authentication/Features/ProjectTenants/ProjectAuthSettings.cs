using System.Text.Json.Serialization;
using TerraScale.Authentication.Features.AttackProtection;
using TerraScale.Authentication.Contracts;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>A project tenant's sign-in methods and branding (table <c>project_auth_settings</c>, one per project).</summary>
/// <param name="InstanceId">The project.</param>
/// <param name="Passcode">Email passcode sign-in.</param>
/// <param name="Password">Password sign-in.</param>
/// <param name="SignUp">Self sign-up.</param>
/// <param name="Name">Display name, or null for the project's name.</param>
/// <param name="LogoUrl">An https logo URL, or null.</param>
/// <param name="ThemeColor"><c>#rrggbb</c>, or null for the default theme.</param>
/// <param name="Providers">The configured social sign-in providers, or null for none.</param>
/// <param name="TitleSize">Hosted title size in pixels, or the default.</param>
/// <param name="DefaultTheme">Light, dark or system appearance.</param>
/// <param name="FooterLinks">Project footer links, or the default links.</param>
/// <param name="BackgroundColor">Optional page background colour.</param>
/// <param name="TextColor">Optional text colour.</param>
/// <param name="Configuration">Project redirect and absolute session policy.</param>
/// <param name="EmailMessage">Optional plain-text message in authentication emails, never HTML or a template expression.</param>
public sealed record ProjectAuthSettingsRecord(string InstanceId, bool Passcode = true, bool Password = true, bool SignUp = true, string? Name = null, string? LogoUrl = null, string? ThemeColor = null,
    IReadOnlyList<SocialProviderRecord>? Providers = null, int? TitleSize = null, string? DefaultTheme = null, IReadOnlyList<FooterLinkBody>? FooterLinks = null,
    string? BackgroundColor = null, string? TextColor = null, ConfigurationBody? Configuration = null, string? EmailMessage = null,
    IReadOnlyList<string>? SubscribedWebhookEvents = null, bool OrganizationPicker = false)
{
    public AttackProtectionSettings AttackProtection { get; init; } = new();

    /// <summary>The configured <paramref name="provider"/>, or null.</summary>
    public SocialProviderRecord? Provider(string provider) => this.Providers?.FirstOrDefault(record => record.Provider == provider);
}

/// <summary>Per-project sign-in settings, read by the management API and the tenant's hosted pages.</summary>
public sealed class ProjectAuthSettings(AuthData data)
{
    public async Task<ProjectAuthSettingsRecord> GetAsync(string projectId, CancellationToken cancellationToken = default) =>
        (await this.ReadVersionedAsync(projectId, cancellationToken).ConfigureAwait(false)).Settings;

    public async Task<ProjectAuthSettingsRecord> UpdateAsync(string projectId, Func<ProjectAuthSettingsRecord, ProjectAuthSettingsRecord> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        for (var round = 0; round < 3; round++)
        {
            var (current, version) = await this.ReadVersionedAsync(projectId, cancellationToken).ConfigureAwait(false);
            var updated = change(current) with { InstanceId = projectId };
            if (await data.SaveAsync(Write(updated, version), cancellationToken).ConfigureAwait(false))
            {
                return updated;
            }
        }

        throw ApiException.Unavailable();
    }

    internal static Change Write(ProjectAuthSettingsRecord settings, string? version) =>
        version is null ? Change.Insert(settings) : Change.Update(settings, version);

    internal async Task<(ProjectAuthSettingsRecord Settings, string? Version)> ReadVersionedAsync(string projectId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.ProjectAuthSettings.Where(settings => settings.InstanceId == projectId).WithVersion().SingleOrDefaultAsync(token), cancellationToken)
            .ConfigureAwait(false) is { } stored
            ? (stored.Value, stored.Version)
            : (new ProjectAuthSettingsRecord(projectId), null);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<SocialProviderRecord>))]
[JsonSerializable(typeof(IReadOnlyList<FooterLinkBody>))]
[JsonSerializable(typeof(ConfigurationBody))]
internal sealed partial class ProjectAuthSettingsJson : JsonSerializerContext;
