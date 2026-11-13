using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal static class BearerChallenge
{
    internal static async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var presented = context.Request.Headers.Authorization.ToString();
        if (presented.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && context.RequestServices.GetRequiredService<TerraBaseOptions>().AuthIssuer is not null
            && context.RequestServices.GetRequiredService<IIssuerKeys>().Keys is null)
        {
            await ErrorResponse.From(new Unavailable("authentication is not ready; signing keys are not loaded", "auth_unavailable"))
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        context.Response.OnStarting(() =>
        {
            if (Listeners.ArrivedOn(context) == TerraBaseListener.Client
                && context.Response.StatusCode == StatusCodes.Status401Unauthorized
                && context.Response.Headers.WWWAuthenticate.Count == 0)
            {
                context.Response.Headers.WWWAuthenticate = context.Request.Headers.Authorization.Count > 0
                    ? "Bearer realm=\"terrascale\", error=\"invalid_token\""
                    : "Bearer realm=\"terrascale\"";
            }

            return Task.CompletedTask;
        });
        await next().ConfigureAwait(false);
        if (Listeners.ArrivedOn(context) == TerraBaseListener.Client
            && context.Response.StatusCode == StatusCodes.Status401Unauthorized
            && !context.Response.HasStarted
            && presented.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && TsAuthKeyExchange.IsKey(presented["Bearer ".Length..].Trim()))
        {
            await BadRequests.WriteAsync(
                context, StatusCodes.Status401Unauthorized, "invalid_token", KeyAuthorityMessages.KeyAsBearer, op: null).ConfigureAwait(false);
        }
    }
}
