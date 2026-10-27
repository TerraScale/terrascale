using System.Globalization;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Store;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>The exact, expiring read-only ts-orchestrator E2E workload trust. Inert until explicitly bound.</summary>
public static class GitHubActionsGrant
{
    /// <summary>The grant type. A custom grant, not RFC 8693: OpenIddict validates token-exchange subject tokens as its own.</summary>
    public const string GrantType = "urn:terrascale:params:oauth:grant-type:github-actions";

    /// <summary>The bound workload's client (a fixed ULID, as every client id): public, allowed only this grant and <see cref="Scopes"/>.</summary>
    public const string ClientId = "01M4GHA0E2E000000000000000";

    /// <summary>GitHub Actions' OIDC issuer; its discovery document names its JWKS.</summary>
    public const string Issuer = "https://token.actions.githubusercontent.com";

    /// <summary>The HTTP client that fetches GitHub's discovery document and keys.</summary>
    public const string HttpClientName = "github-actions-oidc";

    // The trust policy. Numeric ids, so a renamed or re-created repository or owner never matches.
    /// <summary>TerraScale/ts-orchestrator's repository id.</summary>
    public const string RepositoryId = "1422428095";

    /// <summary>The TerraScale organization's id on GitHub.</summary>
    public const string RepositoryOwnerId = "206395477";

    /// <summary>The repository's name, checked as well as its id.</summary>
    public const string Repository = "TerraScale/ts-orchestrator";

    /// <summary>Only runs of <c>main</c>.</summary>
    public const string Ref = "refs/heads/main";

    /// <summary>The exact GitHub subject of a production environment job.</summary>
    public const string Subject = "repo:TerraScale/ts-orchestrator:environment:production";

    /// <summary>Only explicit workflow dispatches.</summary>
    public const string EventName = "workflow_dispatch";

    /// <summary>The exact workflow; reusable workflows are refused even when their path matches.</summary>
    public const string WorkflowRef = "TerraScale/ts-orchestrator/.github/workflows/e2e.yml@refs/heads/main";

    /// <summary>The only scope this read-only workload may carry.</summary>
    public static IReadOnlyList<string> Scopes { get; } = [TerraBaseScopes.DocumentsRead];

    /// <summary>The exact audience GitHub must attest.</summary>
    public const string Audience = "https://auth.terrascale.tech/terrascale";

    /// <summary>The exact receiving TerraBase API audience.</summary>
    public const string ApiAudience = "https://api.terrascale.tech";

    /// <summary>The required GitHub environment.</summary>
    public const string Environment = "production";

    /// <summary>The database ULID restriction understood by TerraBase's verifier.</summary>
    public const string DatabaseProperty = "ts_database";

    /// <summary>The binding's activation time.</summary>
    public const string ActivatedProperty = "ts_activated_at";

    /// <summary>The binding's absolute UTC deadline.</summary>
    public const string ExpiresProperty = "ts_expires_at";

    /// <summary>Explicit UTC timestamps, at JWT second precision.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    internal static bool HasExactPermissions(IEnumerable<string>? permissions) => permissions is not null && permissions.ToHashSet(StringComparer.Ordinal).SetEquals(
        [Permissions.Endpoints.Token, Permissions.Prefixes.GrantType + GrantType, Permissions.Prefixes.Scope + TerraBaseScopes.DocumentsRead]);

    internal static bool TryReadBinding(IReadOnlyDictionary<string, JsonElement>? properties, DateTimeOffset now,
        out string organization, out string project, out string database, out DateTimeOffset expires)
    {
        string Text(string name) => properties is not null && properties.TryGetValue(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
        organization = Text(OrganizationProperty);
        project = Text(ProjectProperty);
        database = Text(DatabaseProperty);
        return DateTimeOffset.TryParseExact(Text(ExpiresProperty), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out expires)
            && DateTimeOffset.TryParseExact(Text(ActivatedProperty), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var activated)
            && PlatformDirectory.IsId(organization) && PlatformDirectory.IsId(project) && ApiKeys.IsDatabaseId(database)
            && activated <= now && expires > now && expires > activated && expires - activated <= TimeSpan.FromHours(24);
    }

    /// <summary>The binding client's property naming the e2e organization.</summary>
    public const string OrganizationProperty = "ts_organization";

    /// <summary>The binding client's property naming the e2e project.</summary>
    public const string ProjectProperty = "ts_project";

    /// <summary>The kind of the unique claim that makes each GitHub token's <c>jti</c> single-use.</summary>
    public const string TokenIdKind = "github-actions-jti";
}

/// <summary>GitHub Actions' discovery document and signing keys, cached and refreshed by the IdentityModel configuration manager.</summary>
public sealed class GitHubActionsKeys(IHttpClientFactory clients)
{
    /// <summary>The configuration manager token validation reads keys from; an unknown kid refreshes it.</summary>
    public ConfigurationManager<OpenIdConnectConfiguration> Configuration { get; } = new(
        GitHubActionsGrant.Issuer + "/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever(clients.CreateClient(GitHubActionsGrant.HttpClientName)) { RequireHttps = true });
}

/// <summary>Explicit operator activation/revocation; never creates organizations, projects or databases.</summary>
public static class GitHubActionsBinding
{
    /// <summary>The verb that binds.</summary>
    public const string BindCommand = "bind-github-actions-e2e";

    /// <summary>The verb that unbinds.</summary>
    public const string UnbindCommand = "unbind-github-actions-e2e";

    /// <summary>Binds an existing organization/project and an explicitly approved database ULID until a UTC deadline.</summary>
    public static async Task<(string Organization, string Project, bool Created)> BindAsync(IServiceProvider services,
        string organization, string project, string database, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (!PlatformDirectory.IsId(organization) || !PlatformDirectory.IsId(project) || !ApiKeys.IsDatabaseId(database)
            || deadline.Offset != TimeSpan.Zero || deadline.Ticks % TimeSpan.TicksPerSecond != 0
            || deadline <= now || deadline - now > TimeSpan.FromHours(24))
        {
            throw new ArgumentException("Explicit canonical organization/project ULIDs, database ULID and a UTC deadline within 24 hours are required.");
        }

        var directory = services.GetRequiredService<PlatformDirectory>();
        if (await directory.OrganizationAsync(organization, cancellationToken).ConfigureAwait(false) is null
            || await directory.ProjectAsync(project, cancellationToken).ConfigureAwait(false) is not { } found
            || found.Value.OrganizationId != organization)
        {
            throw new InvalidOperationException("The approved project must already exist in the approved organization.");
        }

        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(GitHubActionsGrant.ClientId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            var properties = await applications.GetPropertiesAsync(existing, cancellationToken).ConfigureAwait(false);
            var permissions = await applications.GetPermissionsAsync(existing, cancellationToken).ConfigureAwait(false);
            var clientType = await applications.GetClientTypeAsync(existing, cancellationToken).ConfigureAwait(false);
            if (!GitHubActionsGrant.TryReadBinding(properties, now, out var boundOrganization, out var boundProject, out var boundDatabase, out var expires)
                || boundOrganization != organization || boundProject != project || boundDatabase != database || expires != deadline
                || clientType != ClientTypes.Public || !GitHubActionsGrant.HasExactPermissions(permissions))
            {
                throw new InvalidOperationException("An existing binding differs or is invalid; explicitly unbind it before a separately approved activation.");
            }

            return (organization, project, false);
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = GitHubActionsGrant.ClientId,
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "GitHub Actions E2E (ts-orchestrator e2e.yml, production read-only)",
            Permissions = { Permissions.Endpoints.Token, Permissions.Prefixes.GrantType + GitHubActionsGrant.GrantType },
            Properties =
            {
                [GitHubActionsGrant.OrganizationProperty] = JsonSerializer.SerializeToElement(organization),
                [GitHubActionsGrant.ProjectProperty] = JsonSerializer.SerializeToElement(project),
                [GitHubActionsGrant.DatabaseProperty] = JsonSerializer.SerializeToElement(database),
                [GitHubActionsGrant.ActivatedProperty] = JsonSerializer.SerializeToElement(now.ToString(GitHubActionsGrant.TimestampFormat, CultureInfo.InvariantCulture)),
                [GitHubActionsGrant.ExpiresProperty] = JsonSerializer.SerializeToElement(deadline.ToString(GitHubActionsGrant.TimestampFormat, CultureInfo.InvariantCulture)),
            },
        };
        descriptor.Permissions.UnionWith(GitHubActionsGrant.Scopes.Select(scope => Permissions.Prefixes.Scope + scope));
        await applications.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
        return (organization, project, true);
    }

    /// <summary>Deletes the binding. Returns whether there was one.</summary>
    public static async Task<bool> UnbindAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(GitHubActionsGrant.ClientId, cancellationToken).ConfigureAwait(false) is not { } existing)
        {
            return false;
        }

        await applications.DeleteAsync(existing, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Runs the explicit operator arguments: exit code 0 when it finished, 1 when it failed (nothing half-done is left bound).</summary>
    public static async Task<int> RunAsync(string[] arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var command = arguments.FirstOrDefault() ?? string.Empty;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GitHubActionsBinding));
        try
        {
            string outcome;
            if (arguments is [BindCommand, var organization, var project, var database, var expiry]
                && DateTimeOffset.TryParseExact(expiry, GitHubActionsGrant.TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var deadline))
            {
                var (_, _, created) = await BindAsync(services, organization, project, database, deadline, cancellationToken).ConfigureAwait(false);
                outcome = $"{(created ? "bound" : "already bound; nothing changed")}: client {GitHubActionsGrant.ClientId}, organization {organization}, project {project}, database {database}, expires {expiry}";
            }
            else
            {
                outcome = arguments is [UnbindCommand]
                    ? await UnbindAsync(services, cancellationToken).ConfigureAwait(false) ? "unbound: client deleted" : "not bound; nothing changed"
                    : throw new ArgumentException("Usage: bind-github-actions-e2e <organization ULID> <project ULID> <database ULID> <yyyy-MM-ddTHH:mm:ssZ> | unbind-github-actions-e2e");
            }

            logger.ZLogWarning($"Audit: operator command {command}: {outcome}");
            await Console.Out.WriteLineAsync($"ts-auth {command}: {outcome}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.ZLogError(error, $"Audit: operator command {command} failed");
            await Console.Error.WriteLineAsync($"ts-auth {command}: failed ({error.GetType().Name})".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }
}
