using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Features.UserAuth;

/// <summary>
/// Authenticates a request on the internal listener as the platform operator. The internal listener is bound to the
/// node's NetBird address or local loopback and uses h2c. Listener middleware first verifies and records its shared peer
/// credential. With <see cref="UserHeader"/> the operator acts as that user on the Management API, and every use is logged. Without it, on a project's data route, the operator holds every scope of the
/// project's organization, as an API key of that organization would (ts-auth's own storage).
/// </summary>
internal sealed class OperatorHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IProjectCatalog catalog) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = UserAuthExtensions.OperatorScheme;

    /// <summary>The acting user, trusted only on the internal listener.</summary>
    public const string UserHeader = "X-TerraBase-User-Id";

    /// <summary>The operator's stand-in key on data routes.</summary>
    public const string KeyId = "operator";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // A bearer credential is supplemental live authority only on the explicit recovery route, with an acting user.
        if (Listeners.ArrivedOn(Context) != TerraBaseListener.Internal
            || Request.Headers.Authorization.Count > 0
                && (Context.GetEndpoint()?.Metadata.GetMetadata<OperatorAuthorityProof>() is null
                    || !Request.Headers.ContainsKey(UserHeader) || OperatorAuthorityProof.Read(Request) is null))
        {
            return AuthenticateResult.NoResult();
        }

        if (!Request.Headers.TryGetValue(UserHeader, out var values))
        {
            return await DataAsync().ConfigureAwait(false);
        }

        var user = values.Count == 1 ? values[0] : null;
        if (!EntityIds.IsValid(user))
        {
            return AuthenticateResult.Fail(UserHeader + " must be one canonical uppercase ULID");
        }

        // The audit trail of the operator: who it acted as and what it did.
        Logger.ZLogInformation($"The platform operator acted as user {user:@UserId}: {Request.Method:@Method} {Request.Path.Value:@Path}");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    // A project's data route: the operator reaches the project's organization with every scope.
    private async Task<AuthenticateResult> DataAsync()
    {
        // Data routes only: on the Management API the operator always names the user it acts as.
        if (Context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>() is not { } served || ReferenceEquals(served, ListenerMetadata.Management)
            || !served.Listeners.Contains(TerraBaseListener.Client)
            || !Request.RouteValues.TryGetValue("project", out var value) || value is not string project || !EntityIds.IsValid(project)
            || await catalog.ProjectOrganizationAsync(project, Context.RequestAborted).ConfigureAwait(false) is not string organization)
        {
            return AuthenticateResult.NoResult();
        }

        var key = new ApiKey(KeyId, "platform operator", [.. ApiKeyScopes.All, .. ApiKeyScopes.ProjectTokenScopes], organization, project);
        Context.Features.Set(key);
        Claim[] claims = [new(ClaimTypes.NameIdentifier, key.Id), .. key.Scopes.Select(scope => new Claim(ApiKeyAuthenticationHandler.ScopeClaim, scope))];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}
