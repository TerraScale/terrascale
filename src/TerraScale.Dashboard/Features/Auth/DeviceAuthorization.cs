using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class DeviceAuthorization(DeviceGrants grants, DashboardSessions sessions, IAuthGateway auth,
    AccountService accounts, AccessTokens tokens, TimeProvider time)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string Client = "terrascale-cli";
    public const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    public async Task<object> IssueAsync(HttpContext http, CancellationToken cancellationToken)
    {
        await SweepAsync(cancellationToken);
        var now = time.GetUtcNow();
        var device = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string code;
        while (true)
        {
            code = new string(Enumerable.Range(0, 8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
            try
            {
                await grants.IssueAsync(new(DeviceGrants.Hash(device), DeviceGrants.Hash(code), ClientHash(http),
                    now.AddMinutes(10), now.AddSeconds(5)), cancellationToken);
                break;
            }
            catch (DeviceCodeTakenException) { }
        }
        code = code[..4] + "-" + code[4..];
        var fly = Environment.GetEnvironmentVariable("FLY_APP_NAME");
        var origin = fly is { Length: > 0 } ? "https://" + fly + ".fly.dev" : $"{http.Request.Scheme}://{http.Request.Host}";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new DeviceRejectedException("invalid_request");
        var verification = origin + "/cli/approve";
        return new
        {
            device_code = device,
            user_code = code,
            verification_uri = verification,
            verification_uri_complete = verification + "?user_code=" + Uri.EscapeDataString(code),
            expires_in = 600,
            interval = 5
        };
    }

    public Task<DeviceGrant?> LookupAsync(string raw, CancellationToken cancellationToken) =>
        Normalize(raw) is { } code ? grants.ByCodeAsync(code, cancellationToken) : Task.FromResult<DeviceGrant?>(null);

    public async Task<bool> DecideAsync(HttpContext http, SessionRecord session, string raw, string decision, string password)
    {
        var grant = await LookupAsync(raw, http.RequestAborted);
        if (grant is null || !grant.Pending(time.GetUtcNow())) return false;
        string binding = "", credential = "";
        SessionRecord? fresh = null;
        var retained = false;
        try
        {
            if (decision == "approve")
            {
                fresh = await auth.PasswordAsync(session.Email, password, EnrollmentEndpoints.ClientIp(http), http.RequestAborted);
                if (fresh.UserId != session.UserId || fresh.Email != session.Email) throw new SignInRejectedException();
                binding = await sessions.BindBrowserAsync(http, session) ?? throw new DeviceRejectedException("access_denied", 403);
                credential = await sessions.CreateCredentialAsync(fresh, time.GetUtcNow().AddDays(30), http.RequestAborted);
            }
            var won = false;
            await grants.ChangeAsync(grant.DeviceHash, current =>
            {
                won = current.Pending(time.GetUtcNow());
                return won ? current with { Denied = decision == "deny", UserId = session.UserId, SessionId = binding, CredentialId = credential } : current;
            }, http.RequestAborted);
            retained = won && credential.Length != 0;
            return won;
        }
        finally
        {
            if (!retained && fresh is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await auth.RevokeAsync(fresh, cleanup.Token); }
                finally { if (credential.Length != 0) await sessions.DeleteCredentialAsync(credential, cleanup.Token); }
            }
        }
    }

    public async Task<object> PollAsync(string device, CancellationToken cancellationToken)
    {
        if (device.Length != 43 || device.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new DeviceRejectedException("invalid_grant");
        var grant = await grants.ByDeviceAsync(device, cancellationToken) ?? throw new DeviceRejectedException("invalid_grant");
        string outcome = "";
        grant = await grants.ChangeAsync(grant.DeviceHash, current =>
        {
            outcome = "";
            var now = time.GetUtcNow();
            if (current.ExpiresAt <= now) { outcome = "expired_token"; return current; }
            if (current.NextPoll > now)
            {
                outcome = "slow_down";
                var interval = checked(current.Interval + 5);
                return current with { Interval = interval, NextPoll = now.AddSeconds(interval) };
            }
            if (current.Denied) { outcome = "access_denied"; return null; }
            if (current.SessionId.Length == 0 || current.Redeeming) outcome = "authorization_pending";
            return current with { NextPoll = now.AddSeconds(current.Interval) };
        }, cancellationToken);
        if (outcome.Length != 0)
        {
            if (outcome == "expired_token" && grant is not null) await CleanupAsync(grant, cancellationToken);
            throw new DeviceRejectedException(outcome);
        }
        if (grant is null) throw new DeviceRejectedException("invalid_grant");
        var browser = await sessions.ReadBoundAsync(grant.SessionId, cancellationToken);
        if (browser is null || browser.UserId != grant.UserId)
        {
            await CleanupAsync(grant, cancellationToken);
            throw new DeviceRejectedException("access_denied");
        }
        var context = await accounts.GetContextAsync(browser, cancellationToken);
        var claimed = false;
        await grants.ChangeAsync(grant.DeviceHash, current =>
        {
            claimed = current.ExpiresAt > time.GetUtcNow() && !current.Denied && !current.Redeeming &&
                current.SessionId == grant.SessionId && current.UserId == browser.UserId && current.CredentialId == grant.CredentialId;
            return claimed ? current with { Redeeming = true } : current;
        }, cancellationToken);
        if (!claimed) throw new DeviceRejectedException("authorization_pending");
        // Mark single-use persistently before creating the PAT. Ambiguous
        // delivery never resets this claim and cannot mint a second credential.
        var token = await tokens.CreateFromCredentialAsync(context, "TerraScale CLI", 30, grant.CredentialId, cancellationToken);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await grants.ChangeAsync(grant.DeviceHash, _ => null, cleanup.Token);
        return new { access_token = token.Secret, token_type = "Bearer", expires_in = (int)Math.Max(0, (token.Token.ExpiresAt - time.GetUtcNow()).TotalSeconds) };
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var grant in await grants.ExpiredAsync(cancellationToken))
            if (grant.CredentialId.Length != 0 || grant.ExpiresAt <= time.GetUtcNow().AddMinutes(-10)) await CleanupAsync(grant, cancellationToken);
    }

    private async Task CleanupAsync(DeviceGrant grant, CancellationToken cancellationToken)
    {
        if (grant.CredentialId.Length != 0)
        {
            var credential = await sessions.ReadCredentialAsync(grant.CredentialId, cancellationToken);
            if (credential is not null) await auth.RevokeAsync(credential, cancellationToken);
            await sessions.DeleteCredentialAsync(grant.CredentialId, cancellationToken);
        }
        await grants.ChangeAsync(grant.DeviceHash, _ => null, cancellationToken);
    }

    public static string? Normalize(string raw)
    {
        var code = raw.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return code.Length == 8 && code.All(Alphabet.Contains) ? code : null;
    }

    private static string ClientHash(HttpContext http)
    {
        var client = EnrollmentEndpoints.ClientIp(http);
        if (IPAddress.TryParse(client, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            { var bytes = address.GetAddressBytes(); Array.Clear(bytes, 8, 8); client = new IPAddress(bytes).ToString() + "/64"; }
            else client = address.ToString();
        }
        return DeviceGrants.Hash(client);
    }

    public static async Task<IFormCollection> PublicFormAsync(HttpContext http)
    {
        if (http.Request.ContentType?.Split(';')[0].Trim() != "application/x-www-form-urlencoded" || http.Request.ContentLength > 4096)
            throw new DeviceRejectedException("invalid_request");
        if (http.Request.Headers["Sec-Fetch-Site"] == "cross-site" || http.Request.Headers.Origin is { Count: > 0 } origins &&
            (origins.Count != 1 || origins[0] != $"{http.Request.Scheme}://{http.Request.Host}")) throw new DeviceRejectedException("invalid_request", 403);
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (form.Sum(field => field.Key.Length + field.Value.ToString().Length) > 4096) throw new DeviceRejectedException("invalid_request");
        if (form["client_id"] != Client) throw new DeviceRejectedException("invalid_client");
        return form;
    }
}

public sealed class DeviceCleanup(DeviceAuthorization device, ILogger<DeviceCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await device.SweepAsync(stoppingToken); }
            catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or TimeoutException)
            { logger.LogWarning("Expired device grant cleanup failed ({ErrorType})", error.GetType().Name); }
        }
    }
}
