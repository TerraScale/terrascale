using TerraBase.Errors;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal static class BearerChallenge
{
    internal static async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var presented = context.Request.Headers.Authorization.ToString();
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
        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized && !context.Response.HasStarted && TokenKeysPending.On(context))
        {
            context.Response.Headers.RetryAfter = "1";
            await BadRequests.WriteAsync(
                context, StatusCodes.Status503ServiceUnavailable, TokenKeysPending.Code, TokenKeysPending.Message, op: null).ConfigureAwait(false);
            return;
        }

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
