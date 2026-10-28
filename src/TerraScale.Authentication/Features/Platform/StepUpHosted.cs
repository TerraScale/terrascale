using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;

namespace TerraScale.Authentication.Features.Platform;

internal static class StepUpHosted
{
    internal const string Route = "/ui/{slug}/step-up/{id}";

    internal static async Task<IResult> HandleAsync(HttpContext context, bool post, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;
        var proofs = services.GetRequiredService<StepUpProofs>();
        var hosted = services.GetRequiredService<HostedAuth>();
        var mfa = services.GetRequiredService<TotpCredentials>();
        var passcodes = services.GetRequiredService<Passcodes>();
        if (SignInFlow.WrongSlug(context) || SignInFlow.Tenant(context) != "terrascale"
            || post && await SignInFlow.ReadAsync(context, services.GetRequiredService<AuthOptions>(), cancellationToken) is null
            || await SecurityFlow.LiveAsync(context, services.GetRequiredService<Sessions>(), cancellationToken) is not { } live
            || context.GetRouteValue("id") is not string id || !PlatformDirectory.IsId(id)
            || await proofs.ReadAsync(live.User, id, cancellationToken) is not { } current)
        {
            throw StepUpProofs.Required();
        }

        var record = current.Record;
        var user = live.User;
        string? notice = null;
        if (post && !record.Approved)
        {
            var ip = SignInFlow.Ip(context);
            var form = context.Request.Form;
            if (form["action"] == "send" && user.PasswordHash is null && !record.PrimaryVerified && !record.MailRequested)
            {
                if (await hosted.LimitMailAsync(user.Email, ip, cancellationToken) is { } mailWait)
                {
                    return HostedAuth.TooMany(mailWait);
                }

                record = record with { MailRequested = true };
                if (!await proofs.AdvanceAsync(user, record, current.Version, cancellationToken))
                {
                    throw StepUpProofs.Required();
                }

                using (var reservation = services.GetRequiredService<SignInMail>().Reserve())
                {
                    await reservation.EnqueueStepUpAsync(user, id);
                }

                notice = "One verification code was requested. Check your email; restart from the application if it expires.";
            }
            else
            {
                if (await hosted.LimitIpAsync(ip, mails: false, cancellationToken) is { } wait)
                {
                    return HostedAuth.TooMany(wait);
                }

                UserRecord? primary = null;
                if (record.PrimaryVerified)
                {
                    primary = await mfa.VerifyAsync(user with { AuthenticationBindings = record.Bindings ?? [] }, form["code"].ToString().Trim(), cancellationToken)
                        ? user with { AuthenticationBindings = record.Bindings ?? [] } : null;
                }
                else if (user.PasswordHash is not null)
                {
                    var checkedPassword = await hosted.CheckPasswordAsync(user.Email, form["password"].ToString(), cancellationToken);
                    if (checkedPassword.Wait is { } passwordWait)
                    {
                        return HostedAuth.TooMany(passwordWait);
                    }

                    primary = checkedPassword.User;
                }
                else if (record.MailRequested)
                {
                    if (await hosted.LimitCodeAsync(user.Email, PasscodePurpose.StepUp, issuing: false, cancellationToken) is { } codeWait)
                    {
                        return HostedAuth.TooMany(codeWait);
                    }

                    primary = await passcodes.RedeemStepUpAsync(Users.BindAuthenticationState(user), id, form["code"].ToString(), cancellationToken);
                }

                if (primary is null || primary.Id != user.Id || primary.AuthEpoch != record.AuthEpoch || primary.AuthStamp != record.AuthStamp)
                {
                    notice = "Verification failed. Check your credentials or restart from the application.";
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                else
                {
                    record = record with
                    {
                        PrimaryVerified = true,
                        Approved = record.PrimaryVerified || !(await mfa.StatusAsync(user.InstanceId, user.Id, cancellationToken)).Enabled,
                        Bindings = primary.AuthenticationBindings,
                    };
                    if (!await proofs.AdvanceAsync(primary, record, current.Version, cancellationToken))
                    {
                        throw StepUpProofs.Required();
                    }
                }
            }
        }

        if (record.Approved)
        {
            return SignInPageModel.Render(context, new SignInPageModel(SignInStep.Continue, "terrascale", Next: record.ReturnUri));
        }

        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        var page = StepUpPage.Create(new StepUpPageModel(id, user.Email, record.Purpose, record.PrimaryVerified, user.PasswordHash is not null, record.MailRequested, notice, nonce));
        page.StatusCode = context.Response.StatusCode;
        return page;
    }
}
