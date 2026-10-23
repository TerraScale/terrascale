using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>A project tenant's sign-in methods and branding (collection <c>project_auth_settings</c>, one per project).</summary>
/// <param name="InstanceId">The project.</param>
/// <param name="Passcode">Email passcode sign-in.</param>
/// <param name="Password">Password sign-in.</param>
/// <param name="SignUp">Self sign-up.</param>
/// <param name="Name">Display name, or null for the project's name.</param>
/// <param name="LogoUrl">An https logo URL, or null.</param>
/// <param name="ThemeColor"><c>#rrggbb</c>, or null for the default theme.</param>
public sealed record ProjectAuthSettingsRecord(string InstanceId, bool Passcode = true, bool Password = true, bool SignUp = true, string? Name = null, string? LogoUrl = null, string? ThemeColor = null);

/// <summary>Per-project sign-in settings, read by the management API and the tenant's hosted pages.</summary>
public sealed class ProjectAuthSettings(AuthStore store)
{
    private const string Collection = AuthStore.ProjectAuthSettingsCollection;

    /// <summary>The project's settings; a project that never changed them has every method on and no branding.</summary>
    public async Task<ProjectAuthSettingsRecord> GetAsync(string projectId, CancellationToken cancellationToken = default) =>
        (await this.ReadAsync(projectId, cancellationToken).ConfigureAwait(false)).Settings;

    /// <summary>Applies <paramref name="change"/> to the settings read, retrying when another writer won.</summary>
    public async Task<ProjectAuthSettingsRecord> UpdateAsync(string projectId, Func<ProjectAuthSettingsRecord, ProjectAuthSettingsRecord> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        for (var round = 0; round < 3; round++)
        {
            var (current, version) = await this.ReadAsync(projectId, cancellationToken).ConfigureAwait(false);
            var updated = change(current) with { InstanceId = projectId };
            var record = JsonSerializer.SerializeToElement(updated, ProjectAuthSettingsJson.Default.ProjectAuthSettingsRecord);
            try
            {
                if (version is null)
                {
                    await store.CreateAsync(Collection, projectId, projectId, record, cancellationToken).ConfigureAwait(false);
                    return updated;
                }

                if (await store.CompareAndPutAsync(Collection, projectId, projectId, record, version, cancellationToken).ConfigureAwait(false))
                {
                    return updated;
                }
            }
            catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "precondition_failed")
            {
                // A concurrent first write created it; read it again.
            }
        }

        throw ApiException.Unavailable();
    }

    private async Task<(ProjectAuthSettingsRecord Settings, string? Version)> ReadAsync(string projectId, CancellationToken cancellationToken) =>
        await store.GetAsync(Collection, projectId, projectId, cancellationToken).ConfigureAwait(false) is { } document
        && document.Document.Deserialize(ProjectAuthSettingsJson.Default.ProjectAuthSettingsRecord) is { } settings && settings.InstanceId == projectId
            ? (settings, document.Version)
            : (new ProjectAuthSettingsRecord(projectId), null);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectAuthSettingsRecord))]
internal sealed partial class ProjectAuthSettingsJson : JsonSerializerContext;
