using System.Globalization;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Security;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class RequestMagicLinkEndpoint(SignInMail mail, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/magic");
        Name("ui_sign_in_magic_request");
        Summary("Email a browser-bound sign-in link");
        Description("Same-origin form post. Shares the email and IP sign-in budgets. The ten-minute link works once in the requesting browser; another browser must enter the emailed code. Unknown addresses receive the same response.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var context = this.HttpContext;
        if (await SignInFlow.ReadAsync(context, options, cancellationToken) is not { } form || !HostedTheme.Of(context).MagicLink)
        {
            return SignInFlow.Refused(context);
        }

        var proof = MagicLinks.Open(options, SignInFlow.Tenant(context), form.Challenge,
            context.RequestServices.GetRequiredService<TimeProvider>());
        var email = proof?.Email ?? form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(context, form.Next);
        if (await SignInFlow.SsoAsync(context, email, next, cancellationToken) is { } sso)
        {
            return sso;
        }

        using var reservation = mail.Reserve();
        if (await hosted.LimitMailAsync(email, SignInFlow.Ip(context), cancellationToken) is { } refusal)
        {
            var seconds = Math.Max(1, (long)Math.Ceiling(refusal.Wait.TotalSeconds));
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.CacheControl = "no-store";
            return SignInPageModel.Render(context, new SignInPageModel(SignInStep.Code, SignInFlow.Tenant(context), proof is null ? email : MagicLinks.Mask(email), next,
                Error: $"No link was sent. Try again in {seconds} seconds.")
            {
                Limited = true,
                CodeBudgetExhausted = refusal.BudgetExhausted,
                WaitSeconds = seconds,
                CodeRequest = "sign-in/magic",
                Challenge = form.Challenge,
            }, StatusCodes.Status429TooManyRequests);
        }

        var tenant = SignInFlow.Tenant(context);
        context.Response.Cookies.Delete(PendingSocialLinks.Cookie(tenant), PendingSocialLinks.CookieOptions(tenant));
        var browser = Tokens.RandomToken(32);
        await reservation.EnqueueMagicLinkAsync(tenant, email, Tokens.Sha256Hex(browser), next, AuthEmails.Locale(context.Request));
        context.Response.Cookies.Append(MagicLinks.Cookie(tenant), browser, MagicLinks.CookieOptions(tenant));
        return SignInPageModel.Render(context, new SignInPageModel(SignInStep.Code, tenant, proof is null ? email : MagicLinks.Mask(email), next)
        { CodeRequest = "sign-in/magic", Challenge = form.Challenge });
    }
}
