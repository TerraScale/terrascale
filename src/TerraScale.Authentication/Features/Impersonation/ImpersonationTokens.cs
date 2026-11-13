using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;
using TerraScale.Authentication.Features.Oidc;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed class ValidateImpersonationToken(Impersonations impersonations) : IOpenIddictServerHandler<ValidateTokenContext>
{
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal?.GetClaim(Impersonations.Claim) is not { } id)
        {
            return;
        }

        var tenant = Tenancy.Current(context.Transaction.GetHttpRequest()?.HttpContext);
        if (await impersonations.FindAsync(tenant, id, context.CancellationToken).ConfigureAwait(false) is not { } live
            || live.User.Id != context.Principal.GetClaim(Claims.Subject))
        {
            context.Reject(Errors.InvalidToken, "The impersonation has ended.");
        }
    }
}

public sealed class LimitImpersonationToken(Impersonations impersonations) : IOpenIddictServerHandler<GenerateTokenContext>
{
    public async ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal?.GetClaim(Impersonations.Claim) is not { } id)
        {
            return;
        }

        var tenant = Tenancy.Current(context.Transaction.GetHttpRequest()?.HttpContext);
        if (await impersonations.FindAsync(tenant, id, context.CancellationToken).ConfigureAwait(false) is not { } live
            || live.Record.ClientId != context.Request?.ClientId || live.User.Id != context.Principal.GetClaim(Claims.Subject))
        {
            context.Reject(Errors.InvalidGrant, "The impersonation has ended or belongs to another client.");
            return;
        }

        if (context.Principal.GetExpirationDate() is not { } expiry || live.Record.ExpiresAt < expiry)
        {
            context.Principal.SetExpirationDate(live.Record.ExpiresAt);
        }
    }
}
