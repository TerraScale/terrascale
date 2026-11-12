using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed record BotChallengeForm(string Action, SignInStep Step, bool PasswordPrompt = false);

public static class BotChallenge
{
    public static void UseBotChallenge(this WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<BotChallengeForm>() is { } challenge
            && Tenancy.Resolved(context) is { } tenant && HostedTheme.Of(context).BotChallenge)
        {
            var options = context.RequestServices.GetRequiredService<AuthOptions>();
            if (!context.Request.HasFormContentType || context.Request.Headers.Origin is not [{ } origin]
                || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin))
            {
                await SignInFlow.Refused(context).ExecuteAsync(context);
                return;
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!(challenge.PasswordPrompt && string.IsNullOrEmpty(form["password"]))
                && !await context.RequestServices.GetRequiredService<AttackProtectionHttp>().ChallengeAsync(
                    context.RequestServices.GetRequiredService<TurnstileKeys>(), form["cf-turnstile-response"],
                    context.Connection.RemoteIpAddress?.ToString(), options.PublicOrigin.IdnHost, challenge.Action, tenant, context.RequestAborted))
            {
                await SignInPageModel.Render(context, new SignInPageModel(challenge.Step, tenant, form["email"], SignInFlow.SafeNext(context, form["next"]),
                    "Complete the bot challenge and try again. If verification is unavailable, retry shortly."), StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    });
}
