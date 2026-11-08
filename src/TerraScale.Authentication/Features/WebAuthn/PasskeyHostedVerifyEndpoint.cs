using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SignIn;
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
            return Results.NotFound();
        }

        if (!SameOrigin(this.HttpContext, options)
            || await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is not { } live
            || !SecurityFlow.Recent(this.HttpContext, live.Session, SecurityFlow.FactorEnrollmentWindow))
        {
            throw new ApiException(403, "passkey_denied", "Sign in again before enrolling a passkey.");
        }

        PasskeyVerifyRequest body;
        try
        {
            body = await this.HttpContext.Request.ReadFromJsonAsync(WebAuthnJson.Default.PasskeyVerifyRequest, cancellationToken) ?? new(null, null, null);
        }
        catch (JsonException)
        {
            throw new ApiException(403, "passkey_denied", "The passkey registration failed verification; start again.");
        }

        var raw = PasskeyVerifyEndpoint.Map(body.Response);
        var entry = raw is null ? null
            : await passkeys.VerifyAsync(live.User, body.Challenge, raw, body.Name is { Length: > 0 and <= 100 } ? body.Name : null, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new ApiException(403, "passkey_denied", "The passkey registration failed verification; start again.");
        }

        return Results.Json(new PasskeyEnrolledBody(entry.CredentialId, entry.Name, entry.Aaguid, entry.CreatedAt),
            WebAuthnJson.Default.PasskeyEnrolledBody, statusCode: StatusCodes.Status201Created);
    }

    internal static bool SameOrigin(HttpContext context, AuthOptions options) =>
        context.Request.Headers.Origin is [{ } origin] && SignInFlow.IsPublicOrigin(origin, options.PublicOrigin);
}
