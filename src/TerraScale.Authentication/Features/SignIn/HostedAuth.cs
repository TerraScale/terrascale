using System.Globalization;
using TerraScale.Authentication.Features.AttackProtection;
using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Security;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class HostedAuth(UserProfiles profiles, Users users, Passcodes passcodes, PasswordHasher passwords, AttemptLimiter limiter, SignInMail mail, IHttpContextAccessor http, ILogger<HostedAuth> logger)
{
    public const int PerIp = 120;

    public const int MailPerIp = 30;

    public const int SignUpsPerIp = 20;

    public static readonly TimeSpan SignUpWindow = TimeSpan.FromHours(1);

    public const int PasswordPerEmail = 10;

    public const int SiteFactor = 4;

    public static readonly TimeSpan IpInterval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan MailInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan PasswordWindow = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan MailCooldown = TimeSpan.FromSeconds(60);

    public const int CodePerEmail = Passcodes.MaxAttempts;

    public static readonly TimeSpan CodeWindow = TimeSpan.FromMinutes(30);

    private string Tenant => Tenancy.Current(http.HttpContext);

    public sealed record MailRefusal(TimeSpan Wait, bool BudgetExhausted);

    public async Task<(string? PendingToken, MailRefusal? Refusal)> SignUpAsync(string email, string password, string ip, string locale, string? username, CancellationToken cancellationToken)
    {
        using var reservation = mail.Reserve();
        if (await limiter.AcquireWithDeadlineAsync(this.Tenant, "hosted-signup-ip", limiter.Fold(ip), SignUpsPerIp, SignUpWindow, cancellationToken) is { } signupLimit)
        {
            logger.ZLogWarning($"Audit: hosted request rate-limited by the {"hosted-signup-ip":@Purpose} budget");
            return (null, new MailRefusal(signupLimit.RetryAfter, BudgetExhausted: false));
        }

        if (await this.LimitMailAsync(email, ip, cancellationToken) is { } refusal)
        {
            return (null, refusal);
        }

        var hash = await passwords.HashAsync(this.Tenant, password, cancellationToken);
        var pending = Tokens.RandomToken(43);
        var tenant = this.Tenant;
        cancellationToken.ThrowIfCancellationRequested();
        await reservation.EnqueueSignUpAsync(tenant, email, hash, pending, locale, username);
        return (pending, null);
    }

    public async Task<MailRefusal?> StartPasswordResetAsync(string email, string ip, string locale, CancellationToken cancellationToken)
    {
        using var reservation = mail.Reserve();
        if (await this.LimitMailAsync(email, ip, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var tenant = this.Tenant;
        cancellationToken.ThrowIfCancellationRequested();
        await reservation.EnqueueCodeAsync(tenant, email, PasscodePurpose.PasswordReset, locale);
        return null;
    }

    public async Task<(bool Reset, TimeSpan? Wait)> CompletePasswordResetAsync(string email, string code, string password, string ip, string locale, CancellationToken cancellationToken)
    {
        const string method = "password-reset";
        if ((await this.LimitIpAsync(ip, mails: false, cancellationToken)
            ?? await this.LimitCodeAsync(email, PasscodePurpose.PasswordReset, cancellationToken, locale)) is { } wait)
        {
            return (false, wait);
        }

        if (!Passcodes.WellFormed(code))
        {
            logger.ZLogInformation($"Audit: hosted {method:@Method} refused");
            return (false, null);
        }

        var hash = await passwords.HashAsync(this.Tenant, password, cancellationToken);
        if (await passcodes.ResetPasswordAsync(this.Tenant, email, code, hash, cancellationToken) is not { } user)
        {
            logger.ZLogInformation($"Audit: hosted {method:@Method} refused");
            return (false, null);
        }

        logger.ZLogInformation($"Audit: hosted password reset for user {user.Id:@UserId}; every session and refresh token is refused");
        return (true, null);
    }

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

    public static IResult TooMany(TimeSpan wait) => new RetryAfterResult(wait,
        TypedResults.Json(new OAuthErrorBody("rate_limited", "Too many attempts; retry later."), statusCode: StatusCodes.Status429TooManyRequests));

    public async Task<(UserRecord? User, TimeSpan? Wait, AttemptLimiter.Reservation? Reservation)> CheckPasswordAsync(string email, string password, CancellationToken cancellationToken, string locale = AuthEmails.English)
    {
        if (Users.NormalizeEmail(email) is null && HostedTheme.Of(http.HttpContext ?? throw new InvalidOperationException("Hosted authentication requires an HTTP context.")).Username
            && await users.FindByUsernameAsync(this.Tenant, email, cancellationToken) is { } named)
        {
            email = named.Email;
        }

        var context = http.HttpContext ?? throw new InvalidOperationException("Hosted sign-in requires an HTTP context.");
        var normalized = Users.NormalizeEmail(email);
        AttemptLimiter.Reservation? reservation = null;
        if (normalized is not null && (!Tenancy.IsProject(this.Tenant) || !(await context.RequestServices.GetRequiredService<ProjectAuthSettings>()
            .GetAsync(this.Tenant, cancellationToken)).AttackProtection.AccountLockout))
        {
            var admission = await limiter.ReserveAsync(this.Tenant, "headless-password-email", limiter.Fold(normalized), PasswordPerEmail, PasswordWindow, cancellationToken);
            if (admission.Rejection is { } rejection)
            {
                return (null, this.Refused(normalized, "headless-password-email", rejection, notify: true, locale), null);
            }

            reservation = admission.Reservation;
        }

        var started = passwords.StartPasswordCheck();
        var user = normalized is not null ? await this.VerifyPasswordAsync(normalized, password, cancellationToken) : null;
        if (user is null)
        {
            await passwords.PadFailureAsync(started, cancellationToken);
        }

        return (user, null, user is null ? null : reservation);
    }

    private async Task<UserRecord?> VerifyPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(this.Tenant, email, cancellationToken);
        var valid = PasswordPolicy.Verifiable(password) && await passwords.VerifyAsync(this.Tenant, password, user?.PasswordHash, cancellationToken);
        if (valid && user?.PasswordHash is { } hash && ImportedPasswords.NeedsUpgrade(hash))
        {
            user = await profiles.UpgradePasswordAsync(user, password, cancellationToken);
            if (user is null)
            {
                return null;
            }
        }

        var context = http.HttpContext ?? throw new InvalidOperationException("Hosted sign-in requires an HTTP context.");
        if (!await context.RequestServices.GetRequiredService<AccountProtection>()
            .PasswordAttemptAsync(this.Tenant, email, user, valid, AuthEmails.Locale(context.Request), cancellationToken) || user is null)
        {
            return null;
        }

        return user;
    }

    private static string Prefix(IPAddress address, int bytes)
    {
        var raw = address.GetAddressBytes();
        Array.Clear(raw, bytes, raw.Length - bytes);
        return new IPAddress(raw).ToString();
    }

    internal async Task<MailRefusal?> LimitSocialSignUpAsync(string ip, CancellationToken cancellationToken) =>
        await limiter.AcquireWithDeadlineAsync(this.Tenant, "hosted-signup-ip", limiter.Fold(ip), SignUpsPerIp, SignUpWindow, cancellationToken) is { } limit
            ? new MailRefusal(limit.RetryAfter, BudgetExhausted: false) : null;

    public async Task<MailRefusal?> LimitMailAsync(string email, string ip, CancellationToken cancellationToken)
    {
        if (await this.LimitIpAsync(ip, mails: true, cancellationToken) is { } wait)
        {
            return new MailRefusal(wait, BudgetExhausted: false);
        }

        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        if (await limiter.AcquireCooldownAsync(this.Tenant, "headless-mail-email", limiter.Fold(normalized), MailCooldown, cancellationToken) is { } cooldownWait)
        {
            logger.ZLogWarning($"Audit: hosted request rate-limited by the {"headless-mail-email":@Purpose} budget");
            return new MailRefusal(cooldownWait, BudgetExhausted: false);
        }

        if (await limiter.AcquireWithDeadlineAsync(this.Tenant, "code-mail-email", limiter.Fold(normalized), Passcodes.MaxIssuesPerWindow, Passcodes.IssueWindow, cancellationToken)
            is { } rejection)
        {
            logger.ZLogWarning($"Audit: hosted request rate-limited by the {"code-mail-email":@Purpose} budget");
            return new MailRefusal(rejection.RetryAfter < MailCooldown ? MailCooldown : rejection.RetryAfter, BudgetExhausted: true);
        }

        return null;
    }

    public async Task<TimeSpan?> LimitIpAsync(string ip, bool mails, CancellationToken cancellationToken)
    {
        foreach (var (purpose, subject, burst, interval) in IpBudgets(ip, mails))
        {
            if (await limiter.AcquireTokenAsync(this.Tenant, purpose, limiter.Fold(subject), burst, interval, cancellationToken) is { } wait)
            {
                logger.ZLogWarning($"Audit: hosted request rate-limited by the {purpose:@Purpose} budget");
                return wait;
            }
        }

        return null;
    }

    public async Task<TimeSpan?> LimitIpDurablyAsync(string ip, CancellationToken cancellationToken)
    {
        foreach (var (purpose, subject, burst, interval) in IpBudgets(ip, mails: false))
        {
            if (await limiter.AcquireTokenDurableAsync(this.Tenant, purpose, limiter.Fold(subject), burst, interval, cancellationToken) is { } wait)
            {
                logger.ZLogWarning($"Audit: hosted request rate-limited by the {purpose:@Purpose} budget");
                return wait;
            }
        }

        return null;
    }

    private static List<(string Purpose, string Subject, int Burst, TimeSpan Interval)> IpBudgets(string ip, bool mails)
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

        return budgets;
    }

    public Task<TimeSpan?> LimitCodeAsync(string email, PasscodePurpose purpose, CancellationToken cancellationToken, string locale = AuthEmails.English)
    {
        var key = purpose == PasscodePurpose.PasswordReset ? "reset-code-email" : "sign-in-code-email";
        return this.LimitEmailAsync(email, key, CodePerEmail, CodeWindow, notify: true, cancellationToken, locale);
    }

    private async Task<TimeSpan?> LimitEmailAsync(string email, string purpose, int maximum, TimeSpan window, bool notify,
        CancellationToken cancellationToken, string locale = AuthEmails.English)
    {
        if (Users.NormalizeEmail(email) is not { } normalized
            || await limiter.AcquireWithDeadlineAsync(this.Tenant, purpose, limiter.Fold(normalized), maximum, window, cancellationToken) is not { } rejection)
        {
            return null;
        }

        return this.Refused(normalized, purpose, rejection, notify, locale);
    }

    private TimeSpan Refused(string normalized, string purpose, AttemptRejection rejection, bool notify, string locale)
    {
        logger.ZLogWarning($"Audit: hosted request rate-limited by the {purpose:@Purpose} budget");
        if (notify)
        {
            var tenant = this.Tenant;
            mail.EnqueueLockout(async () => await users.FindByEmailAsync(tenant, normalized, CancellationToken.None) is { } user
                && await limiter.ClaimLockoutNoticeAsync(tenant, purpose, user.Id, rejection.Until, CancellationToken.None)
                    ? new AuthEmailValues(user.Email) { LockoutUntil = rejection.Until, Tenant = tenant }
                    : null, locale);
        }

        return rejection.RetryAfter;
    }

    private sealed class RetryAfterResult(TimeSpan wait, IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            httpContext.Response.Headers.CacheControl = "no-store";
            return inner.ExecuteAsync(httpContext);
        }
    }
}
