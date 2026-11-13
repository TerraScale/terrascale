using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class VerifyEnrollmentInboxEndpoint(Passcodes passcodes, TotpCredentials mfa, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/mfa/enroll/verify-email");
        Name("ui_required_mfa_verify_email");
        Summary("Prove the inbox before required MFA enrollment");
        Description("Same-origin form post, independent of passcode sign-in. Requires the sealed five-minute enrollment-inbox challenge from primary authentication and its single-use emailed code for the same tenant, user and authentication state. Shares hosted IP and account code-attempt limits. Success only offers allowed factor enrollment and never opens a session.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var pending = await PendingAsync(this.HttpContext, form.Challenge, cancellationToken);

        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if ((await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken)
            ?? await hosted.LimitCodeAsync(pending.User.Email, PasscodePurpose.EnrollmentInbox, cancellationToken,
                AuthEmails.Locale(this.HttpContext.Request))) is { } wait)
        {
            return SignInFlow.Limited(this.HttpContext, SignInStep.EnrollmentInbox, wait, pending.User.Email, next, challenge: form.Challenge);
        }

        if (await passcodes.RedeemEnrollmentInboxAsync(pending.User, form.Challenge!, form.Code ?? string.Empty, cancellationToken) is not { } proven)
        {
            return Page(this.HttpContext, pending.User.Email, next, form.Challenge!, wrong: true);
        }

        return await SignInFlow.SecondFactorAsync(this.HttpContext, mfa, proven, next, cancellationToken)
            ?? SignInFlow.Refused(this.HttpContext);
    }

    internal static async Task<ChallengedSignIn> PendingAsync(HttpContext context, string? challenge, CancellationToken cancellationToken)
    {
        var tenant = SignInFlow.Tenant(context);
        var mfa = context.RequestServices.GetRequiredService<TotpCredentials>();
        if (!Tenancy.IsProject(tenant)
            || (await context.RequestServices.GetRequiredService<ProjectAuthSettings>().GetAsync(tenant, cancellationToken)).Configuration?.RequireMfa != true
            || await mfa.ChallengedAsync(tenant, challenge, "enrollment-inbox", cancellationToken) is not { } pending
            || await mfa.HasAllowedFactorAsync(pending.User, false, cancellationToken))
        {
            throw new ApiException(403, "mfa_required", "Start sign-in again.");
        }

        return pending with
        {
            User = pending.User with
            {
                AuthenticationBindings = [.. pending.User.AuthenticationBindings.Where(binding => binding != pending.Completion)],
            },
        };
    }

    internal static async Task<IResult> SendAsync(HttpContext context, UserRecord user, string? next, CancellationToken cancellationToken)
    {
        var challenge = await context.RequestServices.GetRequiredService<TotpCredentials>().InboxChallengeAsync(user, cancellationToken)
            ?? throw new ApiException(403, "mfa_required", "Start sign-in again.");
        using var reservation = context.RequestServices.GetRequiredService<SignInMail>().Reserve();
        if (await context.RequestServices.GetRequiredService<HostedAuth>().LimitMailAsync(user.Email, SignInFlow.Ip(context), cancellationToken) is { } refusal)
        {
            return SignInFlow.BudgetExhausted(context, SignInStep.EnrollmentInbox, refusal.Wait, user.Email, next, challenge: challenge);
        }

        await reservation.EnqueueEnrollmentInboxAsync(user, challenge, AuthEmails.Locale(context.Request));
        return Page(context, user.Email, next, challenge);
    }

    internal static IResult Page(HttpContext context, string email, string? next, string challenge, bool wrong = false)
    {
        var copy = SignInFlow.Copy(context);
        return SignInPageModel.Render(context, new SignInPageModel(SignInStep.EnrollmentInbox, SignInFlow.Tenant(context), email, next,
            wrong ? copy.ErrorWrongCode : null)
        {
            Challenge = challenge,
            ErrorTitle = wrong ? copy.ErrorWrongCodeTitle : null,
        }, wrong ? StatusCodes.Status401Unauthorized : StatusCodes.Status200OK);
    }
}
