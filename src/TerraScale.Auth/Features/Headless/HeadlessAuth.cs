using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OpenIddict.Abstractions;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Email;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Mfa;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Features.SignIn;
using TerraScale.Auth.Security;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Auth.Features.Headless;

/// <summary>The extension grants (RFC 6749 §4.5) a first-party confidential client redeems at the token endpoint.</summary>
public static class HeadlessGrants
{
    /// <summary>Email and password: <c>email</c>, <c>password</c>, <c>ip_address</c>.</summary>
    public const string Password = "urn:terrascale:params:oauth:grant-type:password";

    /// <summary>
    /// An emailed sign-in or sign-up code: <c>email</c>, <c>code</c>, <c>ip_address</c>, and for a sign-up code the
    /// <c>pending_token</c> the sign-up answered.
    /// </summary>
    public const string EmailCode = "urn:terrascale:params:oauth:grant-type:email-code";
}

/// <summary>
/// The headless authentication API (TS-582): a first-party confidential client (ts-dashboard) collects credentials on
/// its own pages and redeems them server-to-server, for the same tokens as the redirect flow. Every call is limited per
/// end-user IP, as the client reports it (an IPv6 address per /64, and per /48 at <see cref="SiteFactor"/> times the
/// budget), and calls that send mail have a tighter per-IP budget; password attempts are also limited per email. Limits are shared by every replica and apply alike to addresses with and without an account,
/// and every failure answers the same whatever the account state, so nothing tells an account from a stranger.
/// </summary>
public sealed partial class HeadlessAuth(Users users, Passcodes passcodes, TotpCredentials mfa, PasswordHasher passwords, AttemptLimiter limiter, SignInMail mail,
    TimeProvider clock, AuthOptions options, ILogger<HeadlessAuth> logger)
{
    /// <summary>Calls per end-user IP per minute.</summary>
    public const int PerIp = 30;

    /// <summary>Mail-sending calls per end-user IP per <see cref="MailWindow"/>.</summary>
    public const int MailPerIp = 10;

    /// <summary>Password attempts per email per <see cref="PasswordWindow"/>.</summary>
    public const int PasswordPerEmail = 10;

    /// <summary>How many times an IP's budgets an IPv6 /48 (a site) gets, so holding many /64s buys little.</summary>
    public const int SiteFactor = 4;

    /// <summary>The mail budget's window.</summary>
    public static readonly TimeSpan MailWindow = TimeSpan.FromMinutes(15);

    /// <summary>The password budget's window.</summary>
    public static readonly TimeSpan PasswordWindow = TimeSpan.FromMinutes(15);

    /// <summary>Minimum interval between mail-start requests for one normalized email, shared across routes and replicas.</summary>
    public static readonly TimeSpan MailCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Code redemption attempts per email, shared by hosted and headless sign-in.</summary>
    public const int CodePerEmail = Passcodes.MaxAttempts;

    /// <summary>The code budget's window; password reset has its own budget.</summary>
    public static readonly TimeSpan CodeWindow = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Redeems a <see cref="HeadlessGrants"/> request that OpenIddict has already authenticated and permission-checked:
    /// signs the user in with the requested scopes, or answers an OAuth error.
    /// </summary>
    public async Task<IResult> GrantAsync(OpenIddictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await this.RedeemAsync(request, cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            // Storage or Argon2id capacity: an OAuth error the client can retry, not the JSON API envelope.
            return Results.Json(new OAuthErrorBody(Errors.TemporarilyUnavailable, error.Message), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> RedeemAsync(OpenIddictRequest request, CancellationToken cancellationToken)
    {
        var email = (string?)request["email"];
        var secret = (string?)(request.GrantType == HeadlessGrants.Password ? request["password"] : request["code"]);
        if (Subject((string?)request["ip_address"]) is not { } ip || email is null || secret is null)
        {
            return OidcResults.Forbid(Errors.InvalidRequest, "email, ip_address and the grant's password or code are required; ip_address is the end user's IP address.");
        }

        if (await this.LimitIpAsync(ip, mails: false, cancellationToken) is { } wait)
        {
            return TooMany(wait);
        }

        UserRecord? user;
        var method = request.GrantType == HeadlessGrants.Password ? "password" : "email-code";
        if (request.GrantType == HeadlessGrants.Password)
        {
            var normalized = Users.NormalizeEmail(email);
            if (await this.LimitEmailAsync(email, "headless-password-email", PasswordPerEmail, PasswordWindow, notify: true, cancellationToken) is { } passwordWait)
            {
                return TooMany(passwordWait);
            }

            user = normalized is null ? null : await this.CheckPasswordAsync(normalized, secret, cancellationToken);
        }
        else
        {
            if (await this.LimitCodeAsync(email, PasscodePurpose.SignIn, issuing: false, cancellationToken) is { } codeWait)
            {
                return TooMany(codeWait);
            }

            user = await passcodes.RedeemAsync(OidcIssuer.InstanceId, email, secret, PasscodePurpose.SignIn, cancellationToken);
            if (user is null && (string?)request["pending_token"] is { } pending
                && await passcodes.RedeemSignUpAsync(OidcIssuer.InstanceId, email, secret, pending, cancellationToken) is { } created)
            {
                LogSignedUp(logger, created.Id);
                user = created;
            }
        }

        if (user is null || !await mfa.AllowsPrimaryAuthenticationAsync(user, cancellationToken))
        {
            LogRefused(logger, method);
            return OidcResults.Forbid(Errors.InvalidGrant, "The email, password or code is wrong, or the code has expired.");
        }

        LogSignedIn(logger, method, user.Id);
        return OidcResults.SignIn(user, clock.GetUtcNow(), request.GetScopes(), OidcIssuer.SignInAudiences(options));
    }

    /// <summary>Emails a sign-in code, or resends a sign-up bound to <paramref name="pendingToken"/>. Null, or how long to wait.</summary>
    public async Task<TimeSpan?> StartEmailCodeAsync(string email, string ip, CancellationToken cancellationToken, string? pendingToken = null)
    {
        if (await this.LimitMailAsync(email, ip, cancellationToken) is { } wait)
        {
            return wait;
        }

        // Issued on the mail worker, so the response takes the same work whether or not the address has an account.
        mail.EnqueueIssue(() => pendingToken is null
            ? passcodes.RequestAsync(OidcIssuer.InstanceId, email, PasscodePurpose.SignIn, CancellationToken.None)
            : passcodes.ResendSignUpAsync(OidcIssuer.InstanceId, email, pendingToken, CancellationToken.None));
        return null;
    }

    /// <summary>
    /// Emails a sign-up code, or a sign-in code when the address has an account, and returns the pending token that
    /// must accompany a sign-up code's redemption; or how long to wait. The token is random either way, so it reveals
    /// nothing, and only its hash is stored.
    /// </summary>
    public async Task<(string? PendingToken, TimeSpan? Wait)> SignUpAsync(string email, string password, string ip, CancellationToken cancellationToken)
    {
        if (await this.LimitMailAsync(email, ip, cancellationToken) is { } wait)
        {
            return (null, wait);
        }

        // Hashed whether or not the address has an account, so both take the same time.
        var hash = await passwords.HashAsync(password, cancellationToken);
        var pending = Tokens.RandomToken(43);
        mail.EnqueueIssue(() => passcodes.RequestSignUpAsync(OidcIssuer.InstanceId, email, hash, pending, CancellationToken.None));
        return (pending, null);
    }

    /// <summary>Emails a password-reset code when <paramref name="email"/> has an account. Null, or how long to wait.</summary>
    public async Task<TimeSpan?> StartPasswordResetAsync(string email, string ip, CancellationToken cancellationToken)
    {
        if (await this.LimitMailAsync(email, ip, cancellationToken) is { } wait)
        {
            return wait;
        }

        mail.EnqueueIssue(() => passcodes.RequestAsync(OidcIssuer.InstanceId, email, PasscodePurpose.PasswordReset, CancellationToken.None));
        return null;
    }

    /// <summary>
    /// Redeems a password-reset code and sets <paramref name="password"/>, which signs the user out everywhere. Returns
    /// whether it did, and how long to wait when the IP's budget is spent.
    /// </summary>
    public async Task<(bool Reset, TimeSpan? Wait)> CompletePasswordResetAsync(string email, string code, string password, string ip, CancellationToken cancellationToken)
    {
        if ((await this.LimitIpAsync(ip, mails: false, cancellationToken)
            ?? await this.LimitCodeAsync(email, PasscodePurpose.PasswordReset, issuing: false, cancellationToken)) is { } wait)
        {
            return (false, wait);
        }

        if (!Passcodes.WellFormed(code))
        {
            LogRefused(logger, "password-reset");
            return (false, null);
        }

        // Hashed before the code is consumed, so a busy replica never burns the user's code.
        var hash = await passwords.HashAsync(password, cancellationToken);
        if (await passcodes.RedeemAsync(OidcIssuer.InstanceId, email, code, PasscodePurpose.PasswordReset, cancellationToken) is not { } user
            || !await users.SetPasswordAsync(OidcIssuer.InstanceId, user.Id, user.AuthEpoch, hash, cancellationToken))
        {
            LogRefused(logger, "password-reset");
            return (false, null);
        }

        LogPasswordReset(logger, user.Id);
        return (true, null);
    }

    /// <summary>
    /// The rate-limit subject of an end user's IP address: the address itself, IPv4-mapped IPv6 as IPv4, and an IPv6
    /// address's /64 prefix, which one subscriber usually holds whole. Null when <paramref name="value"/> is not an IP.
    /// </summary>
    public static string? Subject(string? value)
    {
        if (value is not { Length: > 0 and <= 64 } || !IPAddress.TryParse(value, out var address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 ? Prefix(address, 8) : address.ToString();
    }

    /// <summary>A 429 OAuth error with <c>Retry-After</c>.</summary>
    public static IResult TooMany(TimeSpan wait) => new RetryAfterResult(wait,
        Results.Json(new OAuthErrorBody("rate_limited", "Too many attempts; retry later."), statusCode: StatusCodes.Status429TooManyRequests));

    private async Task<UserRecord?> CheckPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (!PasswordHasher.Acceptable(password))
        {
            return null;
        }

        var user = await users.FindByEmailAsync(OidcIssuer.InstanceId, email, cancellationToken);
        return await passwords.VerifyAsync(password, user?.PasswordHash, cancellationToken) ? user : null;
    }

    // The first bytes of an IPv6 address, the rest zeroed.
    private static string Prefix(IPAddress address, int bytes)
    {
        var raw = address.GetAddressBytes();
        Array.Clear(raw, bytes, raw.Length - bytes);
        return new IPAddress(raw).ToString();
    }

    private async Task<TimeSpan?> LimitMailAsync(string email, string ip, CancellationToken cancellationToken)
    {
        if (await this.LimitIpAsync(ip, mails: true, cancellationToken) is { } wait)
        {
            return wait;
        }

        if (Users.NormalizeEmail(email) is { } normalized
            && await limiter.AcquireCooldownAsync(OidcIssuer.InstanceId, "headless-mail-email", limiter.Fold(normalized), MailCooldown, cancellationToken) is { } cooldownWait)
        {
            LogLimited(logger, "headless-mail-email");
            return cooldownWait;
        }

        var issueWait = await this.LimitCodeAsync(email, PasscodePurpose.SignIn, issuing: true, cancellationToken);
        // The cooldown was just admitted. Cover it too when the hourly budget refuses this request,
        // even if the UTC hour ends first; cooldown refusals still spend no hourly allowance.
        return issueWait is { } hourlyWait && hourlyWait < MailCooldown ? MailCooldown : issueWait;
    }

    /// <summary>Charges the IP call and mail budgets, including the IPv6 /48 budget, across sign-in surfaces.</summary>
    public async Task<TimeSpan?> LimitIpAsync(string ip, bool mails, CancellationToken cancellationToken)
    {
        var site = IPAddress.Parse(ip) is { AddressFamily: AddressFamily.InterNetworkV6 } v6 ? Prefix(v6, 6) : null;
        List<(string Purpose, string Subject, int Maximum, TimeSpan Window)> budgets = [("headless-ip", ip, PerIp, IpWindow)];
        if (site is not null)
        {
            budgets.Add(("headless-ip-site", site, PerIp * SiteFactor, IpWindow));
        }

        if (mails)
        {
            budgets.Add(("headless-ip-mail", ip, MailPerIp, MailWindow));
            if (site is not null)
            {
                budgets.Add(("headless-ip-site-mail", site, MailPerIp * SiteFactor, MailWindow));
            }
        }

        foreach (var (purpose, subject, maximum, window) in budgets)
        {
            if (await limiter.AcquireAsync(OidcIssuer.InstanceId, purpose, limiter.Fold(subject), maximum, window, cancellationToken) is { } wait)
            {
                LogLimited(logger, purpose);
                return wait;
            }
        }

        return null;
    }

    /// <summary>
    /// Charges normalized emails before account lookup, including unknown addresses. Every code issue shares one
    /// hourly budget; sign-in redemption (including sign-up) and reset redemption have separate budgets so recovery
    /// remains available during a sign-in throttle. Replacing a code never renews its email's budget.
    /// </summary>
    public Task<TimeSpan?> LimitCodeAsync(string email, PasscodePurpose purpose, bool issuing, CancellationToken cancellationToken, string locale = AuthEmails.English)
    {
        var key = issuing ? "code-mail-email" : purpose == PasscodePurpose.PasswordReset ? "reset-code-email" : "sign-in-code-email";
        return this.LimitEmailAsync(email, key, issuing ? Passcodes.MaxIssuesPerWindow : CodePerEmail,
            issuing ? Passcodes.IssueWindow : CodeWindow, notify: !issuing, cancellationToken, locale);
    }

    private async Task<TimeSpan?> LimitEmailAsync(string email, string purpose, int maximum, TimeSpan window, bool notify,
        CancellationToken cancellationToken, string locale = AuthEmails.English)
    {
        if (Users.NormalizeEmail(email) is not { } normalized
            || await limiter.AcquireWithDeadlineAsync(OidcIssuer.InstanceId, purpose, limiter.Fold(normalized), maximum, window, cancellationToken) is not { } rejection)
        {
            return null;
        }

        LogLimited(logger, purpose);
        if (notify)
        {
            // Both known and unknown addresses enqueue the same work; only the worker looks up the account.
            mail.EnqueueLockout(async () => await users.FindByEmailAsync(OidcIssuer.InstanceId, normalized, CancellationToken.None) is { } user
                && await limiter.ClaimLockoutNoticeAsync(OidcIssuer.InstanceId, purpose, user.Id, rejection.Until, CancellationToken.None)
                    ? new AuthEmailValues(user.Email) { LockoutUntil = rejection.Until }
                    : null, locale);
        }

        return rejection.RetryAfter;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit: headless {Method} sign-in succeeded for user {UserId}")]
    private static partial void LogSignedIn(ILogger logger, string method, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit: headless sign-up created user {UserId}")]
    private static partial void LogSignedUp(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit: headless password reset for user {UserId}; every session and refresh token is refused")]
    private static partial void LogPasswordReset(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit: headless {Method} refused")]
    private static partial void LogRefused(ILogger logger, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit: headless request rate-limited by the {Purpose} budget")]
    private static partial void LogLimited(ILogger logger, string purpose);

    // Adds Retry-After (whole seconds) to another result.
    private sealed class RetryAfterResult(TimeSpan wait, IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.RetryAfter = ((long)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            httpContext.Response.Headers.CacheControl = "no-store";
            return inner.ExecuteAsync(httpContext);
        }
    }
}
