using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using OpenIddict.Abstractions;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.SignIn;
using TerraScale.Auth.Hosting;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Auth.Features.Headless;

/// <summary>Starts an email-code sign-in or a password reset.</summary>
/// <param name="Email">The account's email address.</param>
/// <param name="IpAddress">The end user's IP address, as the client saw it; rate limits key on it.</param>
public sealed record HeadlessEmailRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("ip_address")] string? IpAddress);

/// <summary>Starts a sign-up.</summary>
/// <param name="Email">The new account's email address.</param>
/// <param name="Password">Its password, 8 to 1024 UTF-8 bytes.</param>
/// <param name="IpAddress">The end user's IP address, as the client saw it; rate limits key on it.</param>
public sealed record HeadlessSignUpRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("ip_address")] string? IpAddress);

/// <summary>The answer to a sign-up.</summary>
/// <param name="PendingToken">
/// Keep it with the user's sign-up (server-side) and send it with the code to the token endpoint; a sign-up code
/// redeems only with the token of the sign-up that issued it. Random even when the address already has an account.
/// </param>
public sealed record HeadlessSignUpResponse([property: JsonPropertyName("pending_token")] string PendingToken);

/// <summary>Completes a password reset.</summary>
/// <param name="Email">The account's email address.</param>
/// <param name="Code">The six-digit reset code it was mailed.</param>
/// <param name="NewPassword">The new password, 8 to 1024 UTF-8 bytes.</param>
/// <param name="IpAddress">The end user's IP address, as the client saw it; rate limits key on it.</param>
public sealed record HeadlessPasswordResetRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("new_password")] string? NewPassword,
    [property: JsonPropertyName("ip_address")] string? IpAddress);

/// <summary><c>POST /{slug}/headless/v1/email-code/start</c>: emails a sign-in code when the address has an account.</summary>
public sealed class HeadlessEmailCodeStartEndpoint(HeadlessAuth headless, IOpenIddictApplicationManager applications) : HeadlessEndpoint<HeadlessEmailRequest>(applications)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/{slug}/headless/v1/email-code/start");
        Name("headless_email_code_start");
        Summary("Email a sign-in code");
        Description("Always 202, whether or not the address has an account. If it has one, mails a six-digit code that works once, for 10 minutes, and dies after 5 wrong guesses; at most 5 codes per account per hour, and a new code replaces the previous one. Redeem it at the token endpoint with grant_type=urn:terrascale:params:oauth:grant-type:email-code.");
        this.Document(StatusCodes.Status202Accepted);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(HeadlessEmailRequest body, CancellationToken cancellationToken) =>
        Valid(body.Email, body.IpAddress) is { } invalid ? invalid
            : await headless.StartEmailCodeAsync(body.Email!, HeadlessAuth.Subject(body.IpAddress)!, cancellationToken) is { } wait ? TooMany(wait)
            : Results.StatusCode(StatusCodes.Status202Accepted);
}

/// <summary><c>POST /{slug}/headless/v1/sign-up</c>: emails a code that creates the account once redeemed.</summary>
public sealed class HeadlessSignUpEndpoint(HeadlessAuth headless, IOpenIddictApplicationManager applications) : HeadlessEndpoint<HeadlessSignUpRequest>(applications)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/{slug}/headless/v1/sign-up");
        Name("headless_sign_up");
        Summary("Sign up");
        Description("Always 202 with a pending_token for a valid request, whether or not the address has an account. A new address is mailed a verification code; the account exists only once the code is redeemed at the token endpoint with grant_type=urn:terrascale:params:oauth:grant-type:email-code together with this pending_token, and then has this password. Only the requester holding the token can redeem the code, so nobody can attach a password to someone else's address. A later sign-up of the same address replaces the code and token. An address that already has an account is mailed a sign-in code instead (redeemed the same way; the token is then ignored) and the password is discarded.");
        this.Document(StatusCodes.Status202Accepted, typeof(HeadlessSignUpResponse));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(HeadlessSignUpRequest body, CancellationToken cancellationToken)
    {
        if (Valid(body.Email, body.IpAddress) is { } invalid)
        {
            return invalid;
        }

        if (!PasswordHasher.Acceptable(body.Password))
        {
            return InvalidPassword();
        }

        var (pending, wait) = await headless.SignUpAsync(body.Email!, body.Password!, HeadlessAuth.Subject(body.IpAddress)!, cancellationToken);
        if (wait is { } retry)
        {
            return TooMany(retry);
        }

        this.HttpContext.Response.Headers.CacheControl = "no-store";
        return Results.Json(new HeadlessSignUpResponse(pending!), HeadlessJson.Default.HeadlessSignUpResponse, statusCode: StatusCodes.Status202Accepted);
    }
}

/// <summary><c>POST /{slug}/headless/v1/password-reset/start</c>: emails a reset code when the address has an account.</summary>
public sealed class HeadlessPasswordResetStartEndpoint(HeadlessAuth headless, IOpenIddictApplicationManager applications) : HeadlessEndpoint<HeadlessEmailRequest>(applications)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/{slug}/headless/v1/password-reset/start");
        Name("headless_password_reset_start");
        Summary("Email a password reset code");
        Description("Always 202, whether or not the address has an account. If it has one, mails a six-digit reset code (10 minutes, 5 wrong guesses); it shares the account's limit of 5 codes per hour and replaces any earlier code.");
        this.Document(StatusCodes.Status202Accepted);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(HeadlessEmailRequest body, CancellationToken cancellationToken) =>
        Valid(body.Email, body.IpAddress) is { } invalid ? invalid
            : await headless.StartPasswordResetAsync(body.Email!, HeadlessAuth.Subject(body.IpAddress)!, cancellationToken) is { } wait ? TooMany(wait)
            : Results.StatusCode(StatusCodes.Status202Accepted);
}

/// <summary><c>POST /{slug}/headless/v1/password-reset/complete</c>: sets a new password with an emailed reset code.</summary>
public sealed class HeadlessPasswordResetCompleteEndpoint(HeadlessAuth headless, IOpenIddictApplicationManager applications) : HeadlessEndpoint<HeadlessPasswordResetRequest>(applications)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/{slug}/headless/v1/password-reset/complete");
        Name("headless_password_reset_complete");
        Summary("Set a new password with a reset code");
        Description("204 once the password is set. The user is then signed out everywhere: hosted sessions and every refresh token are refused (access tokens already issued live out their hour). A wrong, expired, used or superseded code and an unknown address all answer 400 invalid_code.");
        this.Document(StatusCodes.Status204NoContent);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(HeadlessPasswordResetRequest body, CancellationToken cancellationToken)
    {
        if (Valid(body.Email, body.IpAddress) is { } invalid)
        {
            return invalid;
        }

        if (!PasswordHasher.Acceptable(body.NewPassword))
        {
            return InvalidPassword();
        }

        var (reset, wait) = await headless.CompletePasswordResetAsync(body.Email!, body.Code ?? string.Empty, body.NewPassword!, HeadlessAuth.Subject(body.IpAddress)!, cancellationToken);
        return wait is { } retry ? TooMany(retry)
            : reset ? Results.NoContent()
            : Error(StatusCodes.Status400BadRequest, "invalid_code", "The code is wrong or has expired. Request a new one if needed.");
    }
}

/// <summary>
/// A headless JSON endpoint: answers 404 for other slugs, authenticates the confidential client with HTTP Basic before
/// reading the body (as the token endpoint's <c>client_secret_basic</c>), and reads a small JSON body. Only clients
/// allowed the email-code grant may call it.
/// </summary>
/// <typeparam name="TBody">The JSON body.</typeparam>
public abstract class HeadlessEndpoint<TBody>(IOpenIddictApplicationManager applications) : EndpointWithoutRequest<IResult>
    where TBody : class
{
    private const int MaxBody = 8 * 1024;

    /// <inheritdoc />
    public sealed override async Task<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext))
        {
            return Results.NotFound();
        }

        if (!await this.AuthenticateAsync(cancellationToken))
        {
            this.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"ts-auth\"";
            return Error(StatusCodes.Status401Unauthorized, "invalid_client", "Client authentication failed.");
        }

        if (this.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBody;
        }

        TBody? body;
        try
        {
            body = this.HttpContext.Request.HasJsonContentType()
                ? (TBody?)await this.HttpContext.Request.ReadFromJsonAsync(typeof(TBody), HeadlessJson.Default, cancellationToken)
                : null;
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException or NotSupportedException)
        {
            body = null;
        }

        return body is null
            ? Error(StatusCodes.Status400BadRequest, "invalid_request", $"The body must be a JSON object of at most {MaxBody} bytes.")
            : await this.HandleAsync(body, cancellationToken);
    }

    /// <summary>Handles a call from an authenticated client.</summary>
    protected abstract Task<IResult> HandleAsync(TBody body, CancellationToken cancellationToken);

    /// <summary>Declares the shared OpenAPI metadata, with <paramref name="success"/> as the success status and its JSON <paramref name="body"/>, if any.</summary>
    protected void Document(int success, Type? body = null)
    {
        Tags("Headless");
        // Accepts<TBody> would document EmptyRequest for a body this endpoint reads itself; the last metadata wins.
        Metadata(new AcceptsMetadata(["application/json"], typeof(TBody)));
        this.Definition.Produces(body ?? typeof(void), success);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    /// <summary>A 400 when the email or the end user's IP address is missing or malformed.</summary>
    protected static IResult? Valid(string? email, string? ipAddress) =>
        Users.NormalizeEmail(email) is null ? Error(StatusCodes.Status400BadRequest, "invalid_request", "email must be a valid email address.")
        : HeadlessAuth.Subject(ipAddress) is null ? Error(StatusCodes.Status400BadRequest, "invalid_request", "ip_address must be the end user's IP address.")
        : null;

    /// <summary>A 400 for a password outside the policy.</summary>
    protected static IResult InvalidPassword() =>
        Error(StatusCodes.Status400BadRequest, "invalid_password", $"The password must be {PasswordHasher.MinBytes} to {PasswordHasher.MaxBytes} bytes of UTF-8.");

    /// <summary>A 429 with <c>Retry-After</c>.</summary>
    protected IResult TooMany(TimeSpan wait)
    {
        this.HttpContext.Response.Headers.RetryAfter = ((long)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many attempts; retry later.");
    }

    /// <summary>The JSON error envelope.</summary>
    protected static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(new ErrorDetail(code, message)), HeadlessJson.Default.ErrorBody, statusCode: status);

    // client_secret_basic as OpenIddict reads it: Base64 of the form-encoded id and secret, joined by a colon.
    private async Task<bool> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (!AuthenticationHeaderValue.TryParse(this.HttpContext.Request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || header.Parameter is null)
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.ASCII.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var clientId = Uri.UnescapeDataString(decoded[..colon].Replace("+", "%20", StringComparison.Ordinal));
        var secret = Uri.UnescapeDataString(decoded[(colon + 1)..].Replace("+", "%20", StringComparison.Ordinal));
        return secret.Length > 0
            && await applications.FindByClientIdAsync(clientId, cancellationToken) is { } application
            && await applications.HasClientTypeAsync(application, ClientTypes.Confidential, cancellationToken)
            && await applications.HasPermissionAsync(application, Permissions.Prefixes.GrantType + HeadlessGrants.EmailCode, cancellationToken)
            && await applications.ValidateClientSecretAsync(application, secret, cancellationToken);
    }
}

[JsonSerializable(typeof(HeadlessEmailRequest))]
[JsonSerializable(typeof(HeadlessSignUpRequest))]
[JsonSerializable(typeof(HeadlessPasswordResetRequest))]
[JsonSerializable(typeof(HeadlessSignUpResponse))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class HeadlessJson : JsonSerializerContext;
