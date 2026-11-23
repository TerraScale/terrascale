using System.Globalization;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Abstractions;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Platform;

public static class GitHubActionsGrant
{
    public const string GrantType = "urn:terrascale:params:oauth:grant-type:github-actions";

    public const string ClientId = "01M4GHA0E2E000000000000000";

    public const string Issuer = "https://token.actions.githubusercontent.com";

    public const string HttpClientName = "github-actions-oidc";

    public const string RepositoryId = "1422428095";

    public const string RepositoryOwnerId = "206395477";

    public const string Repository = "TerraScale/ts-orchestrator";

    public const string Ref = "refs/heads/main";

    public const string Subject = "repo:TerraScale/ts-orchestrator:environment:production";

    public const string EventName = "workflow_dispatch";

    public const string WorkflowRef = "TerraScale/ts-orchestrator/.github/workflows/e2e.yml@refs/heads/main";

    public static IReadOnlyList<string> Scopes { get; } = [TerraBaseScopes.SqlRead];

    public const string Audience = "https://auth.terrascale.tech/terrascale";

    public const string ApiAudience = "https://api.terrascale.tech";

    public const string Environment = "production";

    public const string DatabaseProperty = "ts_database";

    public const string ActivatedProperty = "ts_activated_at";

    public const string ExpiresProperty = "ts_expires_at";

    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    internal static bool HasExactPermissions(IEnumerable<string>? permissions) => permissions is not null && permissions.ToHashSet(StringComparer.Ordinal).SetEquals(
        [Permissions.Endpoints.Token, Permissions.Prefixes.GrantType + GrantType, Permissions.Prefixes.Scope + TerraBaseScopes.SqlRead]);

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

    public const string OrganizationProperty = "ts_organization";

    public const string ProjectProperty = "ts_project";

    public const string TokenIdKind = "github-actions-jti";
}

public sealed class GitHubActionsKeys(IHttpClientFactory clients)
{
    public ConfigurationManager<OpenIdConnectConfiguration> Configuration { get; } = new(
        GitHubActionsGrant.Issuer + "/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever(clients.CreateClient(GitHubActionsGrant.HttpClientName)) { RequireHttps = true });
}

public static class GitHubActionsBinding
{
    public const string BindCommand = "bind-github-actions-e2e";

    public const string UnbindCommand = "unbind-github-actions-e2e";

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
        if (await directory.OrganizationAsync(organization, cancellationToken) is null
            || await directory.ProjectAsync(project, cancellationToken) is not { } found
            || found.Value.OrganizationId != organization)
        {
            throw new InvalidOperationException("The approved project must already exist in the approved organization.");
        }

        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(GitHubActionsGrant.ClientId, cancellationToken) is { } existing)
        {
            var properties = await applications.GetPropertiesAsync(existing, cancellationToken);
            var permissions = await applications.GetPermissionsAsync(existing, cancellationToken);
            var clientType = await applications.GetClientTypeAsync(existing, cancellationToken);
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
        await applications.CreateAsync(descriptor, cancellationToken);
        return (organization, project, true);
    }

    public static async Task<bool> UnbindAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(GitHubActionsGrant.ClientId, cancellationToken) is not { } existing)
        {
            return false;
        }

        await applications.DeleteAsync(existing, cancellationToken);
        return true;
    }

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
                var (_, _, created) = await BindAsync(services, organization, project, database, deadline, cancellationToken);
                outcome = $"{(created ? "bound" : "already bound; nothing changed")}: client {GitHubActionsGrant.ClientId}, organization {organization}, project {project}, database {database}, expires {expiry}";
            }
            else
            {
                outcome = arguments is [UnbindCommand]
                    ? await UnbindAsync(services, cancellationToken) ? "unbound: client deleted" : "not bound; nothing changed"
                    : throw new ArgumentException("Usage: bind-github-actions-e2e <organization ULID> <project ULID> <database ULID> <yyyy-MM-ddTHH:mm:ssZ> | unbind-github-actions-e2e");
            }

            logger.ZLogWarning($"Audit: operator command {command}: {outcome}");
            await Console.Out.WriteLineAsync($"ts-auth {command}: {outcome}".AsMemory(), cancellationToken);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.ZLogError(error, $"Audit: operator command {command} failed");
            await Console.Error.WriteLineAsync($"ts-auth {command}: failed ({error.GetType().Name})".AsMemory(), CancellationToken.None);
            return 1;
        }
    }
}
