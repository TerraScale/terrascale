using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class DashboardSessions(IDashboardState state, IDashboardIdentity identity, IAuthGateway auth, TimeProvider time)
{
    private const string Cookie = "ts-dashboard-session";

    public async Task StartAsync(HttpContext context, SessionRecord record)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var credentials = await identity.ReadAsync(context.RequestAborted);
        var encrypted = Seal(record, credentials.SessionEncryptionKey);
        await state.PutAsync("sessions", Key(token), JsonSerializer.SerializeToElement(encrypted), TerraBasePrecondition.Absent, context.RequestAborted);
        context.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = record.ExpiresAt
        });
    }

    public async Task<SessionRecord?> ReadAsync(HttpContext context)
    {
        var token = context.Request.Cookies[Cookie];
        if (token is not { Length: 64 } || token.Any(character => !char.IsAsciiHexDigitLower(character))) return null;
        var document = await state.GetAsync("sessions", Key(token), context.RequestAborted);
        if (document is null) return null;
        var credentials = await identity.ReadAsync(context.RequestAborted);
        var record = Open(document.Document.Deserialize<SealedSession>()!, credentials.SessionEncryptionKey);
        if (record.ExpiresAt <= time.GetUtcNow())
        {
            await state.DeleteAsync("sessions", document.Id, document.Version, context.RequestAborted);
            context.Response.Cookies.Delete(Cookie);
            return null;
        }
        if (!await auth.IsActiveAsync(record, context.RequestAborted))
        {
            await state.DeleteAsync("sessions", document.Id, document.Version, context.RequestAborted);
            context.Response.Cookies.Delete(Cookie);
            return null;
        }
        return record;
    }

    public async Task EndAsync(HttpContext context)
    {
        if (context.Request.Cookies[Cookie] is { Length: 64 } token)
        {
            var document = await state.GetAsync("sessions", Key(token), context.RequestAborted);
            if (document is not null)
                await state.DeleteAsync("sessions", document.Id, document.Version, context.RequestAborted);
        }
        context.Response.Cookies.Delete(Cookie);
    }

    private static string Key(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static SealedSession Seal(SessionRecord record, string key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(key), 16);
        try
        {
            aes.Encrypt(nonce, bytes, cipher, tag, "TerraScale.Dashboard/session/v1"u8);
            return new(Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static SessionRecord Open(SealedSession value, string key)
    {
        var cipher = Convert.FromBase64String(value.Ciphertext);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Convert.FromBase64String(key), 16);
        try
        {
            aes.Decrypt(Convert.FromBase64String(value.Nonce), cipher, Convert.FromBase64String(value.Tag), plain, "TerraScale.Dashboard/session/v1"u8);
            return JsonSerializer.Deserialize<SessionRecord>(plain) ?? throw new CryptographicException("Invalid session.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

public sealed record SessionRecord(string UserId, string Name, string Email, string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Dashboard session [REDACTED]";
}

public sealed record SealedSession(string Nonce, string Ciphertext, string Tag);
