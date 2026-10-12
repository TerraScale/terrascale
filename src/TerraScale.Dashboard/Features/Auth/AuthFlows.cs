using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Auth;

// Only a random bearer travels in the browser. Passwords and PKCE verifiers
// are encrypted in TerraBase, versioned across replicas, and never logged.
public sealed class AuthFlows(IDashboardState state, IDashboardIdentity identity, TimeProvider time)
{
    private const string Collection = "auth-flows";
    private const string SignUpCookie = "ts-dashboard-signup";
    private const string OidcCookie = "ts-dashboard-oidc";

    public int Remaining(SignUpFlow flow) => Math.Max(0, (int)Math.Ceiling((flow.ResendAt - time.GetUtcNow()).TotalSeconds));

    public async Task StartSignUpAsync(HttpContext context, SignUpFlow flow)
    {
        await EndAsync(context, SignUpCookie);
        await StoreAsync(context, SignUpCookie, flow, flow.ExpiresAt);
    }

    public async Task<SignUpFlow?> ReadSignUpAsync(HttpContext context)
    {
        var stored = await ReadAsync<SignUpFlow>(context, SignUpCookie);
        if (stored is null) return null;
        if (stored.Value.ExpiresAt > time.GetUtcNow()) return stored.Value;
        await EndAsync(context, SignUpCookie);
        return null;
    }

    public async Task<SignUpFlow?> ReserveResendAsync(HttpContext context)
    {
        var stored = await ReadAsync<SignUpFlow>(context, SignUpCookie);
        if (stored is null || stored.Value.ExpiresAt <= time.GetUtcNow()) return null;
        if (Remaining(stored.Value) > 0)
            throw new AuthRequestRejectedException($"Please wait {Remaining(stored.Value)} seconds before resending the code.", statusCode: 429, retryAfter: Remaining(stored.Value));
        var next = stored.Value with { ResendAt = time.GetUtcNow().AddMinutes(1) };
        try { await UpdateAsync(context, stored.Id, stored.Version, SignUpCookie, next); }
        catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
        { throw new AuthRequestRejectedException("Please wait 60 seconds before resending the code.", statusCode: 429, retryAfter: 60); }
        return next;
    }

    public async Task UpdateSignUpAsync(HttpContext context, SignUpFlow flow)
    {
        var stored = await ReadAsync<SignUpFlow>(context, SignUpCookie);
        if (stored is null) throw new AuthRequestRejectedException("Your verification session has expired. Create your account again to request a new code.", statusCode: 400);
        await UpdateAsync(context, stored.Id, stored.Version, SignUpCookie, flow);
    }

    public Task EndSignUpAsync(HttpContext context) => EndAsync(context, SignUpCookie);

    public async Task ReserveSignUpMailAsync(string email, CancellationToken cancellationToken)
    {
        var id = "signup-" + Key(email);
        var previous = await state.GetAsync("auth-mail-budgets", id, cancellationToken);
        var now = time.GetUtcNow();
        var until = previous?.Document.Deserialize<MailBudget>()?.Until;
        if (until > now)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((until.Value - now).TotalSeconds));
            throw new AuthRequestRejectedException($"Please wait {seconds} seconds before resending the code.", statusCode: 429, retryAfter: seconds);
        }
        try
        {
            await state.PutAsync("auth-mail-budgets", id, JsonSerializer.SerializeToElement(new MailBudget(now.AddMinutes(1))),
                previous is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(previous.Version), cancellationToken);
        }
        catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
        { throw new AuthRequestRejectedException("Please wait 60 seconds before resending the code.", statusCode: 429, retryAfter: 60); }
    }

    public async Task<OidcFlow> StartOidcAsync(HttpContext context, string returnTo)
    {
        var flow = new OidcFlow(Random(), Random(), Random(), Csrf.LocalRedirect(returnTo),
            $"{context.Request.Scheme}://{context.Request.Host}/login/callback", time.GetUtcNow().AddMinutes(10));
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

    private async Task UpdateAsync<T>(HttpContext context, string id, string version, string cookie, T value)
    {
        var credentials = await identity.ReadAsync(context.RequestAborted);
        await state.PutAsync(Collection, id, Seal(value, credentials.SessionEncryptionKey, cookie), TerraBasePrecondition.Matches(version), context.RequestAborted);
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

    private static CookieOptions Options(HttpContext context) => new() { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/" };
    private static string Random() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string Key(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Stored<T>(string Id, string Version, T Value);
    private sealed record MailBudget(DateTimeOffset Until);
}
