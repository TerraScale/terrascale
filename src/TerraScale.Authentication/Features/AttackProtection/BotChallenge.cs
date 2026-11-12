using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;

namespace TerraScale.Authentication.Features.AttackProtection;

public static class BotChallenge
{
    public static void UseBotChallenge(this WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType
            && Tenancy.Resolved(context) is { } tenant && HostedTheme.Of(context).BotChallenge
            && context.Request.Path.Value?.Split('/', 4) is [_, "ui", _, var page]
            && page.TrimEnd('/') is "sign-in" or "sign-in/password" or "sign-up" or "sign-up/resend" or "reset" or "reset/resend" or "reset/complete")
        {
            var options = context.RequestServices.GetRequiredService<AuthOptions>();
            if (context.Request.Headers.Origin is not [{ } origin] || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin))
            {
                await SignInFlow.Refused(context).ExecuteAsync(context);
                return;
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!(page.TrimEnd('/') == "sign-in/password" && string.IsNullOrEmpty(form["password"]))
                && !await context.RequestServices.GetRequiredService<AttackProtectionHttp>().ChallengeAsync(
                    context.RequestServices.GetRequiredService<TurnstileKeys>(), form["cf-turnstile-response"], context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted))
            {
                var step = page.StartsWith("sign-up", StringComparison.Ordinal) ? SignInStep.SignUp
                    : page == "reset/complete" ? SignInStep.ResetCode
                    : page.StartsWith("reset", StringComparison.Ordinal) ? SignInStep.Reset
                    : page == "sign-in/password" ? SignInStep.Password : SignInStep.Email;
                await SignInPageModel.Render(context, new SignInPageModel(step, tenant, form["email"], SignInFlow.SafeNext(context, form["next"]),
                    "Complete the bot challenge and try again. If verification is unavailable, retry shortly."), StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    });
}
