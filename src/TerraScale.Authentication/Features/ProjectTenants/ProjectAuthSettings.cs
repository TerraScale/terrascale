using System.Text.Json.Serialization;
using TerraScale.Authentication.Features.AttackProtection;
using TerraScale.Authentication.Contracts;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record ProjectAuthSettingsRecord(string InstanceId, bool Passcode = true, bool Password = true, string SignUpMode = "public", string? Name = null, string? LogoUrl = null, string? ThemeColor = null,
    IReadOnlyList<SocialProviderRecord>? Providers = null, int? TitleSize = null, string? DefaultTheme = null, IReadOnlyList<FooterLinkBody>? FooterLinks = null,
    string? BackgroundColor = null, string? TextColor = null, ConfigurationBody? Configuration = null, string? EmailMessage = null,
    IReadOnlyList<string>? SubscribedWebhookEvents = null, RestrictionsBody? Restrictions = null, bool MagicLink = false, bool Username = false, bool OrganizationPicker = false)
{
    public AttackProtectionSettings AttackProtection { get; init; } = new();

    public SocialProviderRecord? Provider(string provider) => this.Providers?.FirstOrDefault(record => record.Provider == provider);
}

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
