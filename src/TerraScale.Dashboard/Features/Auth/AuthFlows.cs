using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Auth;

// Only a random bearer travels in the browser. PKCE verifiers, state and nonce
// are encrypted in TerraBase, shared across replicas, and never logged.
public sealed class AuthFlows(IDashboardState state, IDashboardIdentity identity, TimeProvider time)
{
    private const string Collection = "auth-flows";
    private const string OidcCookie = "ts-dashboard-oidc";

    public async Task<OidcFlow> StartOidcAsync(HttpContext context, string returnTo, string tokenName = "", int tokenDays = 0, TerraScale.Dashboard.Platform.Mcp.McpAuthorization? mcp = null)
    {
        var flow = new OidcFlow(Random(), Random(), Random(), Csrf.LocalRedirect(returnTo),
            $"{context.Request.Scheme}://{context.Request.Host}/login/callback", time.GetUtcNow().AddMinutes(10), tokenName, tokenDays, mcp);
        await EndAsync(context, OidcCookie);
        await StoreAsync(context, OidcCookie, flow, flow.ExpiresAt);
        return flow;
    }

    public async Task<OidcFlow?> TakeOidcAsync(HttpContext context)
    {
        var stored = await ReadAsync<OidcFlow>(context, OidcCookie);
        context.Response.Cookies.Delete(OidcCookie, Options(context));
        if (stored is null) return null;
        try { await state.DeleteAsync(Collection, stored.Id, stored.Version, context.RequestAborted); }
        catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.NotFound)
        { return null; }
        return stored.Value.ExpiresAt > time.GetUtcNow() ? stored.Value : null;
    }

    private async Task StoreAsync<T>(HttpContext context, string cookie, T value, DateTimeOffset expires)
    {
        var token = Random();
        var credentials = await identity.ReadAsync(context.RequestAborted);
        await state.PutAsync(Collection, Key(token), Seal(value, credentials.SessionEncryptionKey, cookie), TerraBasePrecondition.Absent, context.RequestAborted);
        var options = Options(context);
        options.Expires = expires;
        context.Response.Cookies.Append(cookie, token, options);
    }

    private async Task<Stored<T>?> ReadAsync<T>(HttpContext context, string cookie)
    {
        var token = context.Request.Cookies[cookie];
        if (token is not { Length: 64 } || token.Any(character => !char.IsAsciiHexDigitLower(character))) return null;
        var document = await state.GetAsync(Collection, Key(token), context.RequestAborted);
        if (document is null) return null;
        var credentials = await identity.ReadAsync(context.RequestAborted);
        try { return new(document.Id, document.Version, Open<T>(document.Document, credentials.SessionEncryptionKey, cookie)); }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        { throw new IOException("Authentication flow is invalid."); }
    }

    private async Task EndAsync(HttpContext context, string cookie)
    {
        if (context.Request.Cookies[cookie] is { Length: 64 } token && token.All(char.IsAsciiHexDigitLower))
        {
            var document = await state.GetAsync(Collection, Key(token), context.RequestAborted);
            if (document is not null) await state.DeleteAsync(Collection, document.Id, document.Version, context.RequestAborted);
        }
        context.Response.Cookies.Delete(cookie, Options(context));
    }

    internal static JsonElement Seal<T>(T value, string key, string purpose)
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

    internal static T Open<T>(JsonElement json, string key, string purpose)
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

    private static CookieOptions Options(HttpContext context) => new() { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/" };
    private static string Random() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string Key(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Stored<T>(string Id, string Version, T Value);
}
