using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Store;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>
/// The e2e suite's credential without a stored secret: the OIDC token GitHub Actions gives the e2e job of
/// TerraScale/terrascale's <c>ci.yml</c> on a push to <c>main</c>, exchanged at the platform issuer's token endpoint for a
/// 15-minute project token of the isolated e2e project (<see cref="PlatformGrants.GitHubActionsAsync"/>). It is inert until
/// an operator binds it (<see cref="GitHubActionsBinding"/>): the binding is the public client <see cref="ClientId"/>,
/// whose properties name the e2e organization and project. Unbinding deletes that client. docs/auth/github-actions-oidc.md.
/// </summary>
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
    /// <summary>TerraScale/terrascale's repository id.</summary>
    public const string RepositoryId = "1414527764";

    /// <summary>The TerraScale organization's id on GitHub.</summary>
    public const string RepositoryOwnerId = "206395477";

    /// <summary>The repository's name, checked as well as its id.</summary>
    public const string Repository = "TerraScale/terrascale";

    /// <summary>Only runs of <c>main</c>.</summary>
    public const string Ref = "refs/heads/main";

    /// <summary>The exact GitHub subject of a main-branch job without an environment.</summary>
    public const string Subject = "repo:TerraScale/terrascale:ref:refs/heads/main";

    /// <summary>Only pushes: a pull request never runs as <c>main</c>.</summary>
    public const string EventName = "push";

    /// <summary>
    /// The workflow (<c>workflow_ref</c>), and the job's workflow when the token names one (<c>job_workflow_ref</c>, which GitHub
    /// documents for jobs of a reusable workflow): no reusable workflow matches.
    /// </summary>
    public const string WorkflowRef = "TerraScale/terrascale/.github/workflows/ci.yml@refs/heads/main";

    /// <summary>The most a token carries: databases and documents of the e2e project only.</summary>
    public static IReadOnlyList<string> Scopes { get; } =
        [TerraBaseScopes.DatabasesRead, TerraBaseScopes.DatabasesWrite, TerraBaseScopes.DocumentsRead, TerraBaseScopes.DocumentsWrite];

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

/// <summary>
/// The operator commands <c>ts-auth bind-github-actions-e2e</c> and <c>ts-auth unbind-github-actions-e2e</c>, run in the
/// ts-orch workload like <c>provision-signing-key</c>. Binding is the activation: it creates the public client
/// with a fresh e2e organization and project id (or reports the existing ones, changing nothing). Unbinding deletes it;
/// tokens already issued expire within 15 minutes. Both log an audit line.
/// </summary>
public static class GitHubActionsBinding
{
    /// <summary>The verb that binds.</summary>
    public const string BindCommand = "bind-github-actions-e2e";

    /// <summary>The verb that unbinds.</summary>
    public const string UnbindCommand = "unbind-github-actions-e2e";

    /// <summary>Creates the binding unless it exists. Returns its organization and project and whether it was created.</summary>
    public static async Task<(string Organization, string Project, bool Created)> BindAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await services.GetRequiredService<AuthStore>().ProvisionAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await applications.FindByClientIdAsync(GitHubActionsGrant.ClientId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            var properties = await applications.GetPropertiesAsync(existing, cancellationToken).ConfigureAwait(false);
            return (properties[GitHubActionsGrant.OrganizationProperty].GetString()!, properties[GitHubActionsGrant.ProjectProperty].GetString()!, false);
        }

        var organization = AuthStore.NewId();
        var project = AuthStore.NewId();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = GitHubActionsGrant.ClientId,
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "GitHub Actions e2e (TerraScale/terrascale ci.yml, push to main)",
            Permissions = { Permissions.Endpoints.Token, Permissions.Prefixes.GrantType + GitHubActionsGrant.GrantType },
            Properties =
            {
                [GitHubActionsGrant.OrganizationProperty] = JsonSerializer.SerializeToElement(organization),
                [GitHubActionsGrant.ProjectProperty] = JsonSerializer.SerializeToElement(project),
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

    /// <summary>Runs <paramref name="command"/>: exit code 0 when it finished, 1 when it failed (nothing half-done is left bound).</summary>
    public static async Task<int> RunAsync(string command, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GitHubActionsBinding));
        try
        {
            string outcome;
            if (command == BindCommand)
            {
                var (organization, project, created) = await BindAsync(services, cancellationToken).ConfigureAwait(false);
                outcome = $"{(created ? "bound" : "already bound; nothing changed")}: client {GitHubActionsGrant.ClientId}, organization {organization}, project {project}";
            }
            else
            {
                outcome = await UnbindAsync(services, cancellationToken).ConfigureAwait(false) ? "unbound: client deleted" : "not bound; nothing changed";
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
