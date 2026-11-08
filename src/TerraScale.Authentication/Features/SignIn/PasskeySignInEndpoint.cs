using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>
/// <c>POST /ui/{slug}/sign-in/passkey</c>: verifies the browser's WebAuthn assertion against the sealed challenge from
/// <see cref="PasskeySignInOptionsEndpoint"/> and opens the session like a password sign-in. A user-verified passkey
/// proves the second factor, so the session is marked multi-factor and no second-factor step follows.
/// </summary>
public sealed class PasskeySignInEndpoint(Sessions sessions, PasskeyCredentials passkeys, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    private const int MaxBody = 16384;

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/passkey");
        Name("ui_sign_in_passkey");
        Summary("Sign in with a passkey");
        Description("Same-origin JSON post with the challenge from ui_sign_in_passkey_options, the email address, the browser's assertion and next. Verifies origin, RP id, the challenge (single-use, five minutes, dead after any account authentication change), user verification and the signature, refuses a non-increasing sign count without recording anything, and updates the credential's sign count and last use in the same transaction. Charges 10 attempts per normalized email per 15 minutes; exhaustion answers 429 with Retry-After. Every refusal answers the same generic 403 passkey_denied, whether or not the address has an account. Success sets the tsauth_session cookie, skips the second-factor step (a user-verified passkey satisfies organizations that require one) and continues to next.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/json"], typeof(PasskeySignInRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext))
        {
            return Results.NotFound();
        }

        if (this.HttpContext.Request.Headers.Origin is not [{ } origin] || !SignInFlow.IsPublicOrigin(origin, options.PublicOrigin))
        {
            throw Denied();
        }

        var body = await this.BodyAsync(cancellationToken);
        var raw = Map(body.Response);
        var next = SignInFlow.SafeNext(this.HttpContext, body.Next);
        var user = raw is null
            ? null
            : await passkeys.VerifySignInAsync(SignInFlow.Tenant(this.HttpContext), body.Challenge, body.Email ?? string.Empty, raw, cancellationToken);
        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_passkey", user is null ? "refused" : "expired");
            throw Denied();
        }

        AuthMetrics.SignIn("hosted_passkey", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, next, cancellationToken);
    }

    private static ApiException Denied() => new(StatusCodes.Status403Forbidden, "passkey_denied", "The passkey sign-in failed; start again.");

    private async Task<PasskeySignInRequest> BodyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = this.HttpContext.Request;
            if (request.HasJsonContentType()
                && Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
                && (media.Charset.Length == 0 || media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length + read <= MaxBody)
                {
                    buffer.Write(chunk, 0, read);
                }

                if (read == 0 && JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), WebAuthnJson.Default.PasskeySignInRequest) is { } parsed)
                {
                    return parsed;
                }
            }
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        {
        }

        throw Denied();
    }

    private static AuthenticatorAssertionRawResponse? Map(PasskeyAssertionRequest? response)
    {
        if (response?.Response is not { } inner
            || response.RawId is not { Length: > 0 }
            || response.Type?.Equals("public-key", StringComparison.Ordinal) != true
            || inner.ClientDataJson is not { Length: > 0 } || inner.AuthenticatorData is not { Length: > 0 } || inner.Signature is not { Length: > 0 })
        {
            return null;
        }

        return new AuthenticatorAssertionRawResponse
        {
            Id = response.Id ?? string.Empty,
            RawId = PasskeyCredentials.Base64UrlDecode(response.RawId),
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                ClientDataJson = PasskeyCredentials.Base64UrlDecode(inner.ClientDataJson),
                AuthenticatorData = PasskeyCredentials.Base64UrlDecode(inner.AuthenticatorData),
                Signature = PasskeyCredentials.Base64UrlDecode(inner.Signature),
                UserHandle = inner.UserHandle is { Length: > 0 } handle ? PasskeyCredentials.Base64UrlDecode(handle) : null,
            },
        };
    }
}
