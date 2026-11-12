using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed partial class AuthFlows(IDbContextFactory<DashboardDbContext> storage, IDashboardIdentity identity, TimeProvider time)
    {
        private const string OidcCookiePrefix = "ts-dashboard-oidc-";
        public static readonly TimeSpan OidcLifetime = TimeSpan.FromHours(1);

        public async Task<OidcFlow> StartOidcAsync(HttpContext context, string returnTo, string tokenName = "", int tokenDays = 0, McpAuthorization? mcp = null, StepUpIntent? stepUp = null)
        {
            var flow = new OidcFlow(Random(), Random(), Random(), Csrf.LocalRedirect(returnTo),
                $"{context.Request.Scheme}://{context.Request.Host}/login/callback", time.GetUtcNow().Add(OidcLifetime), tokenName, tokenDays, mcp)
            { StepUp = stepUp };
            await StoreAsync(context, OidcCookie(flow.State), AuthFlowRow.OidcPurpose, flow, flow.ExpiresAt);
            return flow;
        }

        public async Task<OidcTake> TakeOidcAsync(HttpContext context, string returnedState)
        {
            if (!IsToken(returnedState))
            {
                return new OidcTake.Unknown();
            }

            var cookie = OidcCookie(returnedState);
            var stored = await ReadAsync<OidcFlow>(context, cookie, AuthFlowRow.OidcPurpose);
            context.Response.Cookies.Delete(cookie, Options(context));
            if (stored is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState), Encoding.UTF8.GetBytes(stored.Value.State)))
            {
                return new OidcTake.Unknown();
            }

            if (!await TakeAsync(stored.Row, context.RequestAborted))
            {
                return new OidcTake.Unknown();
            }

            return stored.Value.ExpiresAt > time.GetUtcNow() ? new OidcTake.Taken(stored.Value) : new OidcTake.Expired(stored.Value.ReturnTo);
        }

        private static string OidcCookie(string state)
        {
            return OidcCookiePrefix + Key(state)[..16];
        }

        private async Task StoreAsync<T>(HttpContext context, string cookie, string purpose, T value, DateTimeOffset expires)
        {
            var token = Random();
            var credentials = await identity.ReadAsync(context.RequestAborted);
            var (nonce, ciphertext, tag) = Seal(value, credentials.SessionEncryptionKey, purpose);
            await using (var db = await storage.CreateDbContextAsync(context.RequestAborted))
            {
                db.AuthFlows.Add(new AuthFlowRow
                {
                    Id = AccountService.NewId(),
                    TokenHash = Key(token),
                    Purpose = purpose,
                    Nonce = nonce,
                    Ciphertext = ciphertext,
                    Tag = tag,
                    CreatedAt = time.GetUtcNow().UtcDateTime,
                    ExpiresAt = expires.UtcDateTime,
                });
                await db.SaveChangesAsync(context.RequestAborted);
            }

            var options = Options(context);
            options.Expires = expires;
            context.Response.Cookies.Append(cookie, token, options);
        }

        private async Task<Stored<T>?> ReadAsync<T>(HttpContext context, string cookie, string purpose)
        {
            var token = context.Request.Cookies[cookie];
            if (!IsToken(token))
            {
                return null;
            }

            var hash = Key(token);
            AuthFlowRow? row;
            await using (var db = await storage.CreateDbContextAsync(context.RequestAborted))
            {
                row = await db.AuthFlows.AsNoTracking().SingleOrDefaultAsync(flow => flow.TokenHash == hash && flow.Purpose == purpose, context.RequestAborted);
            }

            if (row is null)
            {
                return null;
            }

            var credentials = await identity.ReadAsync(context.RequestAborted);
            try { return new(row, Open<T>(row, credentials.SessionEncryptionKey, purpose)); }
            catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
            { throw new IOException("Authentication flow is invalid."); }
        }

        private async Task<bool> TakeAsync(AuthFlowRow row, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            db.AuthFlows.Remove(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        public async Task HoldAlertSecretAsync(HttpContext context, AccountContext account, string ruleName, string secret)
        {
            await EndAsync(context, AlertSecretCookie, AuthFlowRow.AlertSecretPurpose);
            await StoreAsync(context, AlertSecretCookie, AuthFlowRow.AlertSecretPurpose, new AlertSecretHandoff(account.UserId, StepUpFlow.Browser(context), account.Project?.Id, ruleName, secret), time.GetUtcNow().AddMinutes(2));
        }

        public async Task<AlertSecretHandoff?> TakeAlertSecretAsync(HttpContext context, AccountContext account)
        {
            var stored = await ReadAsync<AlertSecretHandoff>(context, AlertSecretCookie, AuthFlowRow.AlertSecretPurpose);
            context.Response.Cookies.Delete(AlertSecretCookie, Options(context));
            if (stored is null || !await TakeAsync(stored.Row, context.RequestAborted))
            {
                return null;
            }

            return stored.Row.ExpiresAt > time.GetUtcNow().UtcDateTime && stored.Value.UserId == account.UserId &&
                stored.Value.Browser == StepUpFlow.Browser(context) && stored.Value.ProjectId == account.Project?.Id ? stored.Value : null;
        }

        private const string AlertSecretCookie = "ts-dashboard-alert-secret";

        private async Task EndAsync(HttpContext context, string cookie, string purpose)
        {
            var token = context.Request.Cookies[cookie];
            if (IsToken(token))
            {
                var hash = Key(token);
                await using var db = await storage.CreateDbContextAsync(context.RequestAborted);
                await db.AuthFlows.Where(flow => flow.TokenHash == hash && flow.Purpose == purpose).ExecuteDeleteAsync(context.RequestAborted);
            }
            context.Response.Cookies.Delete(cookie, Options(context));
        }

        private static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Seal<T>(T value, string key, string purpose)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(value);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Convert.FromBase64String(key), 16);
            try
            {
                aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes("TerraScale.Dashboard/auth-flow/v1/" + purpose));
                return (nonce, cipher, tag);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        private static T Open<T>(AuthFlowRow row, string key, string purpose)
        {
            var plain = new byte[row.Ciphertext.Length];
            using var aes = new AesGcm(Convert.FromBase64String(key), 16);
            try
            {
                aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plain, Encoding.UTF8.GetBytes("TerraScale.Dashboard/auth-flow/v1/" + purpose));
                return JsonSerializer.Deserialize<T>(plain) ?? throw new JsonException("Invalid auth flow.");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        private static CookieOptions Options(HttpContext context)
        {
            return new() { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/" };
        }

        private static string Random()
        {
            return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }

        private static bool IsToken([NotNullWhen(true)] string? value)
        {
            return value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
        }

        private static string Key(string value)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        private sealed record Stored<T>(AuthFlowRow Row, T Value);
    }

    public sealed record AlertSecretHandoff(string UserId, string Browser, string? ProjectId, string RuleName, string Secret)
    {
        public override string ToString()
        {
            return "Alert secret [REDACTED]";
        }
    }

    public abstract record OidcTake
    {
        private OidcTake()
        {
        }

        public sealed record Taken(OidcFlow Flow) : OidcTake;

        public sealed record Expired(string ReturnTo) : OidcTake;

        public sealed record Unknown : OidcTake;
    }
}
