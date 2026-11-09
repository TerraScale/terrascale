using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed partial class AuthFlows(IDashboardState state, IDashboardIdentity identity, TimeProvider time)
    {
        private const string Table = "dashboard_auth_flows";
        private const string OidcCookiePrefix = "ts-dashboard-oidc-";
        private const string OidcPurpose = "ts-dashboard-oidc";
        public static readonly TimeSpan OidcLifetime = TimeSpan.FromHours(1);

        public async Task<OidcFlow> StartOidcAsync(HttpContext context, string returnTo, string tokenName = "", int tokenDays = 0, McpAuthorization? mcp = null, StepUpIntent? stepUp = null)
        {
            var flow = new OidcFlow(Random(), Random(), Random(), Csrf.LocalRedirect(returnTo),
                $"{context.Request.Scheme}://{context.Request.Host}/login/callback", time.GetUtcNow().Add(OidcLifetime), tokenName, tokenDays, mcp)
            { StepUp = stepUp };
            await StoreAsync(context, OidcCookie(flow.State), OidcPurpose, flow, flow.ExpiresAt);
            return flow;
        }

        public async Task<OidcTake> TakeOidcAsync(HttpContext context, string returnedState)
        {
            if (!IsToken(returnedState))
            {
                return new OidcTake.Unknown();
            }

            var cookie = OidcCookie(returnedState);
            var stored = await ReadAsync<OidcFlow>(context, cookie, OidcPurpose);
            context.Response.Cookies.Delete(cookie, Options(context));
            if (stored is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState), Encoding.UTF8.GetBytes(stored.Value.State)))
            {
                return new OidcTake.Unknown();
            }

            try { await state.DeleteAsync(Table, stored.Id, stored.Version, context.RequestAborted); }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            { return new OidcTake.Unknown(); }
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
            await state.PutAsync(Table, Key(token), Seal(value, credentials.SessionEncryptionKey, purpose), StatePrecondition.Absent, context.RequestAborted);
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

            var row = await state.GetAsync(Table, Key(token), context.RequestAborted);
            if (row is null)
            {
                return null;
            }

            var credentials = await identity.ReadAsync(context.RequestAborted);
            try { return new(row.Id, row.Version, Open<T>(row.Body, credentials.SessionEncryptionKey, purpose)); }
            catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
            { throw new IOException("Authentication flow is invalid."); }
        }

        private async Task EndAsync(HttpContext context, string cookie)
        {
            var token = context.Request.Cookies[cookie];
            if (IsToken(token))
            {
                var row = await state.GetAsync(Table, Key(token), context.RequestAborted);
                if (row is not null)
                {
                    await state.DeleteAsync(Table, row.Id, row.Version, context.RequestAborted);
                }
            }
            context.Response.Cookies.Delete(cookie, Options(context));
        }

        private static JsonElement Seal<T>(T value, string key, string purpose)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(value);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Convert.FromBase64String(key), 16);
            try
            {
                aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes("TerraScale.Dashboard/auth-flow/v1/" + purpose));
                return JsonSerializer.SerializeToElement(new SealedSession(Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag)));
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        private static T Open<T>(JsonElement json, string key, string purpose)
        {
            var sealedValue = json.Deserialize<SealedSession>() ?? throw new JsonException("Invalid auth flow.");
            var cipher = Convert.FromBase64String(sealedValue.Ciphertext);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Convert.FromBase64String(key), 16);
            try
            {
                aes.Decrypt(Convert.FromBase64String(sealedValue.Nonce), cipher, Convert.FromBase64String(sealedValue.Tag), plain, Encoding.UTF8.GetBytes("TerraScale.Dashboard/auth-flow/v1/" + purpose));
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

        private sealed record Stored<T>(string Id, string Version, T Value);
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
