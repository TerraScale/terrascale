using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Server.OpenIddictServerEvents;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

public sealed record MachineClientPolicy(IReadOnlyList<string> AllowedScopes, IReadOnlyList<string> AllowedAudiences, int TokenLifetimeMinutes);

internal static class MachineClients
{
    private const string PolicyProperty = "ts_machine_policy";

    public static MachineClientPolicy? Policy(OidcApplication client) =>
        client.Properties.TryGetValue(PolicyProperty, out var value) ? value.Deserialize(MachineClientJson.Default.MachineClientPolicy)
            ?? throw new InvalidOperationException("A machine client has no policy.") : null;

    public static bool ValidScopes(IReadOnlyList<string> scopes) => scopes.Count is >= 1 and <= 64
        && scopes.Distinct(StringComparer.Ordinal).Count() == scopes.Count
        && scopes.All(scope => scope is { Length: >= 1 and <= 128 } && scope.All(character => character is >= (char)0x21 and <= (char)0x7e and not '"' and not '\\')
            && scope is not (Scopes.OpenId or Scopes.OfflineAccess));

    public static MachineClientPolicy Validate(IReadOnlyList<string>? scopes, IReadOnlyList<string>? audiences, int? minutes)
    {
        audiences ??= [];
        if (scopes is null || !ValidScopes(scopes) || minutes is < 5 or > 60 || audiences.Count > 64
            || audiences.Distinct(StringComparer.Ordinal).Count() != audiences.Count
            || audiences.Any(audience => audience is not { Length: >= 1 and <= 512 } || audience.Any(char.IsWhiteSpace) || audience.Any(char.IsControl)
                || !Uri.TryCreate(audience, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment)))
        {
            throw new ApiException(400, "invalid_request", "Supply 1 to 64 unique OAuth allowed_scopes, at most 64 allowed_audiences and token_lifetime_minutes from 5 to 60.");
        }

        return new(scopes, audiences, minutes ?? 15);
    }

    public static void Configure(OpenIddictApplicationDescriptor descriptor, MachineClientPolicy policy)
    {
        descriptor.Permissions.Clear();
        descriptor.Permissions.UnionWith([Permissions.Endpoints.Token, Permissions.Endpoints.Revocation, Permissions.GrantTypes.ClientCredentials]);
        descriptor.Permissions.UnionWith(policy.AllowedScopes.Select(scope => Permissions.Prefixes.Scope + scope));
        descriptor.Permissions.UnionWith(policy.AllowedAudiences.Select(audience => Permissions.Prefixes.Resource + audience));
        descriptor.Requirements.Clear();
        descriptor.Properties[PolicyProperty] = JsonSerializer.SerializeToElement(policy, MachineClientJson.Default.MachineClientPolicy);
    }

    public static async Task<IResult> GrantAsync(HttpContext context, OpenIddictRequest request, OidcApplication application, CancellationToken cancellationToken)
    {
        if (Policy(application) is not { } policy || application.ClientType != ClientTypes.Confidential)
        {
            return OidcResults.Forbid(Errors.UnauthorizedClient, "Only a machine app can use client_credentials.");
        }

        if (await context.RequestServices.GetRequiredService<AttemptLimiter>().AcquireAsync(application.InstanceId!, "machine-token-client", application.ClientId!,
            1000, TimeSpan.FromMinutes(1), cancellationToken) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var scopes = request.GetScopes();
        var resources = request.GetResources();
        if (scopes.Length == 0 || scopes.Except(policy.AllowedScopes, StringComparer.Ordinal).Any())
        {
            return OidcResults.Forbid(Errors.InvalidScope, "Request one or more of the app's allowed scopes.");
        }

        if (resources.Length > 1 || resources.Except(policy.AllowedAudiences, StringComparer.Ordinal).Any())
        {
            return OidcResults.Forbid("invalid_target", "The resource must be one of the app's allowed audiences.");
        }

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role)
            .SetClaim(Claims.Subject, application.ClientId);
        var principal = new ClaimsPrincipal(identity).SetScopes(scopes).SetResources(resources)
            .SetAccessTokenLifetime(TimeSpan.FromMinutes(policy.TokenLifetimeMinutes));
        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(principal, null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MachineClientPolicy))]
internal sealed partial class MachineClientJson : JsonSerializerContext;

internal sealed class MachineTokenResources(IOpenIddictApplicationManager applications) : IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public async ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        if (context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) is not { } tenant
            || !Tenancy.IsProject(tenant) || !context.Request.IsClientCredentialsGrantType())
        {
            await new OpenIddictServerHandlers.Exchange.ValidateResources().HandleAsync(context);
            return;
        }

        var application = await applications.FindByClientIdAsync(context.ClientId!, context.CancellationToken) as OidcApplication;
        var resources = context.Request.GetResources();
        if (application is null || MachineClients.Policy(application) is not { } policy
            || resources.Length > 1 || resources.Except(policy.AllowedAudiences, StringComparer.Ordinal).Any())
        {
            context.Reject("invalid_target", "The resource must be one of the machine app's allowed audiences.");
        }
    }
}
