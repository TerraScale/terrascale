namespace TerraScale.Auth.Passkeys;

using System.Net;
using System.Text;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Auth.WebAuthn;
using TerraScale.DotNet;

internal static class PasskeySupport
{
    internal const int TimeoutMs = 300_000;

    // ---- Shared helpers --------------------------------------------------------------------

    internal static string Origin(AuthOptions options) => options.PublicOrigin.AbsoluteUri.TrimEnd('/');

    internal static async Task<User?> SessionUserAsync(HttpRequest request, SessionService sessions, string instanceId, CancellationToken ct)
    {
        var token = request.Cookies[SessionCookies.Name];
        if (string.IsNullOrEmpty(token))
            return null;
        return (await sessions.ResolveAsync(instanceId, token, ct))?.User;
    }

    internal static string? ClientDataChallenge(string clientDataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(clientDataJson);
            return document.RootElement.TryGetProperty("challenge", out var challenge) ? challenge.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task<T?> ReadJsonAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
}
