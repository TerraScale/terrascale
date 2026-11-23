using System.Net.Http.Json;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyHostedVerifyEndpoint(Sessions sessions, PasskeyCredentials passkeys, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/security/passkeys/verify");
        Name("ui_security_passkey_verify");
        Summary("Finish passkey enrollment from the security page");
        Description("Same-origin JSON fetch from the hosted security page's live session, opened within the last 10 minutes, with the browser's WebAuthn registration answer; verifies exactly like create_passkey_verify and stores the passkey. An older session must sign in again before a passkey can be added. A refused ceremony, a cross-origin post or a missing session answers 403 passkey_denied.");
        Tags("Security");
        Produces<PasskeyEnrolledBody>(StatusCodes.Status201Created, "application/json");
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext))
        {
            return TypedResults.NotFound();
        }

        ChallengedSignIn? pending = null;
        if (this.HttpContext.Request.Headers[RequiredMfaEnrollment.Header] is [{ } challenge])
        {
            var enrollment = await RequiredMfaEnrollment.ReadAsync(this.HttpContext, challenge, "passkey", cancellationToken);
            var opened = enrollment switch
            {
                ChallengedSignIn allowed => (SignIn: (ChallengedSignIn?)allowed, Failure: (AuthForbidden?)null),
                AuthForbidden error => (SignIn: (ChallengedSignIn?)null, Failure: error),
            };
            if (opened.Failure is { } forbidden)
            {
                return ErrorResults.ToResult(forbidden);
            }

            pending = opened.SignIn;
        }

        var live = pending is null ? await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) : null;
        if (!SameOrigin(this.HttpContext, options)
            || pending is null && (live is null || !SecurityFlow.Recent(this.HttpContext, live.Value.Session, SecurityFlow.FactorEnrollmentWindow)))
        {
            return ErrorResults.ToResult(new AuthForbidden("Sign in again before enrolling a passkey.", "passkey_denied"));
        }

        var caller = pending?.User ?? live!.Value.User;

        PasskeyVerifyRequest body;
        try
        {
            body = await this.HttpContext.Request.ReadFromJsonAsync(WebAuthnJson.Default.PasskeyVerifyRequest, cancellationToken) ?? new(null, null, null);
        }
        catch (JsonException)
        {
            return ErrorResults.ToResult(PasskeyCredentials.DeniedEnroll());
        }

        if (pending is not null && !Tenancy.IsProject(caller.InstanceId)
            && !await this.HttpContext.RequestServices.GetRequiredService<SsoSignIn>().CanFinishAsync(this.HttpContext, caller, cancellationToken))
        {
            return ErrorResults.ToResult(new AuthForbidden("Start sign-in again before enrolling a passkey.", "passkey_denied"));
        }

        return PasskeyVerifyEndpoint.Map(body.Response) switch
        {
            AuthenticatorAttestationRawResponse raw => await VerifyAsync(raw),
            AuthValidation invalid => ErrorResults.ToResult(invalid),
            AuthForbidden denied => ErrorResults.ToResult(denied),
        };

        async ValueTask<IResult> VerifyAsync(AuthenticatorAttestationRawResponse raw)
        {
            return await passkeys.VerifyAsync(caller, body.Challenge, raw, body.Name is { Length: > 0 and <= 100 } ? body.Name : null, pending, cancellationToken) switch
            {
                PasskeyCredentialEntry entry => await Enrolled(this.HttpContext, sessions, caller, entry, pending is not null, cancellationToken),
                AuthForbidden error => ErrorResults.ToResult(error),
            };
        }
    }

    private static async Task<IResult> Enrolled(HttpContext context, Sessions sessions, UserRecord caller, PasskeyCredentialEntry entry, bool finishEnrollment, CancellationToken cancellationToken)
    {
        if (finishEnrollment)
        {
            var session = await sessions.CreateAsync(caller with { MultiFactor = true, MfaMethod = "passkey" }, cancellationToken);
            if (!await SignInFlow.EnrolledAsync(context, sessions, session, cancellationToken))
            {
                return ErrorResults.ToResult(new AuthForbidden("Start sign-in again before enrolling a passkey.", "passkey_denied"));
            }
        }

        return TypedResults.Json(new PasskeyEnrolledBody(entry.CredentialId, entry.Name, entry.Aaguid, entry.CreatedAt),
            WebAuthnJson.Default.PasskeyEnrolledBody, statusCode: StatusCodes.Status201Created);
    }

    internal static bool SameOrigin(HttpContext context, AuthOptions options) =>
        context.Request.Headers.Origin is [{ } origin] && SignInFlow.IsPublicOrigin(origin, options.PublicOrigin);
}
