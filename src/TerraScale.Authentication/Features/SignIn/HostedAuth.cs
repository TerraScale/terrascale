using System.Globalization;
using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Security;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>
/// The credential checks and attempt budgets behind the hosted sign-in, sign-up and password-reset pages and the device
/// verification page. Every call is limited per end-user IP (an IPv6 address per /64, and per /48 at
/// <see cref="SiteFactor"/> times the budget), and calls that send mail have a tighter per-IP budget. IP budgets are
/// refilling token buckets sized for many users behind one NAT, so a burst from one office never locks it out for long;
/// password and code attempts are also limited per email, which is what stops guessing against one account. Limits are shared by every replica and apply alike to addresses with and without an
/// account, and every failure answers the same whatever the account state, so nothing tells an account from a stranger.
/// </summary>
public sealed class HostedAuth(Users users, Passcodes passcodes, PasswordHasher passwords, AttemptLimiter limiter, SignInMail mail, IHttpContextAccessor http, ILogger<HostedAuth> logger)
{
    /// <summary>Calls an end-user IP may burst; one more is regained every <see cref="IpInterval"/>.</summary>
    public const int PerIp = 120;

    /// <summary>Mail-sending calls an end-user IP may burst; one more is regained every <see cref="MailInterval"/>.</summary>
    public const int MailPerIp = 30;

    /// <summary>Password attempts per email per <see cref="PasswordWindow"/>.</summary>
    public const int PasswordPerEmail = 10;

    /// <summary>How many times an IP's budgets an IPv6 /48 (a site) gets, so holding many /64s buys little.</summary>
    public const int SiteFactor = 4;

    /// <summary>How often an IP regains one call: 30 a minute, sustained.</summary>
    public static readonly TimeSpan IpInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often an IP regains one mail-sending call: 60 an hour, sustained.</summary>
    public static readonly TimeSpan MailInterval = TimeSpan.FromMinutes(1);

    /// <summary>The password budget's window.</summary>
    public static readonly TimeSpan PasswordWindow = TimeSpan.FromMinutes(15);

    /// <summary>Minimum interval between mail-start requests for one normalized email, shared across routes and replicas.</summary>
    public static readonly TimeSpan MailCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Code redemption attempts per email, shared by every sign-in page.</summary>
    public const int CodePerEmail = Passcodes.MaxAttempts;

    /// <summary>The code budget's window; password reset has its own budget.</summary>
    public static readonly TimeSpan CodeWindow = TimeSpan.FromMinutes(30);

    // The hosted page's tenant: accounts, codes and rate-limit buckets never cross tenants.
    private string Tenant => Tenancy.Current(http.HttpContext);

    /// <summary>
    /// Emails a sign-up code, or a sign-in code when the address has an account, and returns the pending token that
    /// must accompany a sign-up code's redemption; or how long to wait. The token is random either way, so it reveals
    /// nothing, and only its hash is stored.
    /// </summary>
    public async Task<(string? PendingToken, TimeSpan? Wait)> SignUpAsync(string email, string password, string ip, string locale, CancellationToken cancellationToken)
    {
        if (await this.LimitMailAsync(email, ip, cancellationToken) is { } wait)
        {
            return (null, wait);
        }

        using var reservation = mail.Reserve();
        // Hashed whether or not the address has an account, so both take the same time.
        var hash = await passwords.HashAsync(password, cancellationToken);
        var pending = Tokens.RandomToken(43);
        var tenant = this.Tenant;
        cancellationToken.ThrowIfCancellationRequested();
        reservation.EnqueueIssue(() => passcodes.RequestSignUpAsync(tenant, email, hash, pending, CancellationToken.None), locale);
        return (pending, null);
    }

    /// <summary>Emails a password-reset code when <paramref name="email"/> has an account. Null, or how long to wait.</summary>
    public async Task<TimeSpan?> StartPasswordResetAsync(string email, string ip, string locale, CancellationToken cancellationToken)
    {
        if (await this.LimitMailAsync(email, ip, cancellationToken) is { } wait)
        {
            return wait;
        }

        var tenant = this.Tenant;
        using var reservation = mail.Reserve();
        cancellationToken.ThrowIfCancellationRequested();
        reservation.EnqueueIssue(() => passcodes.RequestAsync(tenant, email, PasscodePurpose.PasswordReset, CancellationToken.None), locale);
        return null;
    }

    /// <summary>
    /// Redeems a password-reset code and sets <paramref name="password"/>, which signs the user out everywhere. Returns
    /// whether it did, and how long to wait when the IP's budget is spent.
    /// </summary>
    public async Task<(bool Reset, TimeSpan? Wait)> CompletePasswordResetAsync(string email, string code, string password, string ip, string locale, CancellationToken cancellationToken)
    {
        const string method = "password-reset";
        if ((await this.LimitIpAsync(ip, mails: false, cancellationToken)
            ?? await this.LimitCodeAsync(email, PasscodePurpose.PasswordReset, issuing: false, cancellationToken, locale)) is { } wait)
        {
            return (false, wait);
        }

        if (!Passcodes.WellFormed(code))
        {
            logger.ZLogInformation($"Audit: hosted {method:@Method} refused");
            return (false, null);
        }

        // Hashed before the code is consumed, so a busy replica never burns the user's code.
        var hash = await passwords.HashAsync(password, cancellationToken);
        if (await passcodes.ResetPasswordAsync(this.Tenant, email, code, hash, cancellationToken) is not { } user)
        {
            logger.ZLogInformation($"Audit: hosted {method:@Method} refused");
            return (false, null);
        }

        logger.ZLogInformation($"Audit: hosted password reset for user {user.Id:@UserId}; every session and refresh token is refused");
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

    /// <summary>
    /// Charges the email's password budget, then checks <paramref name="password"/>. The account, null for any wrong
    /// email or password alike, or how long to wait. Callers charge the IP budget first and check MFA after.
    /// </summary>
    public async Task<(UserRecord? User, TimeSpan? Wait)> CheckPasswordAsync(string email, string password, CancellationToken cancellationToken, string locale = AuthEmails.English)
    {
        if (await this.LimitEmailAsync(email, "headless-password-email", PasswordPerEmail, PasswordWindow, notify: true, cancellationToken, locale) is { } wait)
        {
            return (null, wait);
        }

        return (Users.NormalizeEmail(email) is { } normalized ? await this.VerifyPasswordAsync(normalized, password, cancellationToken) : null, null);
    }

    private async Task<UserRecord?> VerifyPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (!PasswordHasher.Acceptable(password))
        {
            return null;
        }

        var user = await users.FindByEmailAsync(this.Tenant, email, cancellationToken);
        return await passwords.VerifyAsync(password, user?.PasswordHash, cancellationToken) ? user : null;
    }

    // The first bytes of an IPv6 address, the rest zeroed.
    private static string Prefix(IPAddress address, int bytes)
    {
        var raw = address.GetAddressBytes();
        Array.Clear(raw, bytes, raw.Length - bytes);
        return new IPAddress(raw).ToString();
    }

    /// <summary>Charges the shared IP, per-email cooldown and hourly budgets before any hosted code mail is issued.</summary>
    public async Task<TimeSpan?> LimitMailAsync(string email, string ip, CancellationToken cancellationToken)
    {
        if (await this.LimitIpAsync(ip, mails: true, cancellationToken) is { } wait)
        {
            return wait;
        }

        if (Users.NormalizeEmail(email) is { } normalized
            && await limiter.AcquireCooldownAsync(this.Tenant, "headless-mail-email", limiter.Fold(normalized), MailCooldown, cancellationToken) is { } cooldownWait)
        {
            logger.ZLogWarning($"Audit: hosted request rate-limited by the {"headless-mail-email":@Purpose} budget");
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
        List<(string Purpose, string Subject, int Burst, TimeSpan Interval)> budgets = [("hosted-ip", ip, PerIp, IpInterval)];
        if (site is not null)
        {
            budgets.Add(("hosted-ip-site", site, PerIp * SiteFactor, IpInterval / SiteFactor));
        }

        if (mails)
        {
            budgets.Add(("hosted-ip-mail", ip, MailPerIp, MailInterval));
            if (site is not null)
            {
                budgets.Add(("hosted-ip-site-mail", site, MailPerIp * SiteFactor, MailInterval / SiteFactor));
            }
        }

        foreach (var (purpose, subject, burst, interval) in budgets)
        {
            if (await limiter.AcquireTokenAsync(this.Tenant, purpose, limiter.Fold(subject), burst, interval, cancellationToken) is { } wait)
            {
                logger.ZLogWarning($"Audit: hosted request rate-limited by the {purpose:@Purpose} budget");
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
            || await limiter.AcquireWithDeadlineAsync(this.Tenant, purpose, limiter.Fold(normalized), maximum, window, cancellationToken) is not { } rejection)
        {
            return null;
        }

        logger.ZLogWarning($"Audit: hosted request rate-limited by the {purpose:@Purpose} budget");
        if (notify)
        {
            // Both known and unknown addresses enqueue the same work; only the worker looks up the account.
            var tenant = this.Tenant;
            mail.EnqueueLockout(async () => await users.FindByEmailAsync(tenant, normalized, CancellationToken.None) is { } user
                && await limiter.ClaimLockoutNoticeAsync(tenant, purpose, user.Id, rejection.Until, CancellationToken.None)
                    ? new AuthEmailValues(user.Email) { LockoutUntil = rejection.Until, Tenant = tenant }
                    : null, locale);
        }

        return rejection.RetryAfter;
    }

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
