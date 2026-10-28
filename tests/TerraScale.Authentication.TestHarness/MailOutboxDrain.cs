using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Tests;

/// <summary>Waits for the durable authentication mail outbox, which replaced the in-memory delivery queue.</summary>
public static class MailOutboxDrain
{
    private static readonly JsonElement Open = JsonDocument.Parse("""{"or":[{"eq":["/state","queued"]},{"eq":["/state","ready"]},{"eq":["/state","sending"]}]}""").RootElement.Clone();

    /// <summary>Wakes <paramref name="server"/>'s mail worker and waits until no row in the outbox is open.</summary>
    public static async Task DrainMailAsync(this AuthTestServer server)
    {
        var services = server.App.Services;
        try
        {
            using var reservation = services.GetRequiredService<SignInMail>().Reserve();
            // An address without an account: the worker issues nothing and deletes the row.
            await reservation.EnqueueCodeAsync(OidcIssuer.InstanceId, $"drain-{Guid.NewGuid():N}@example.test", PasscodePurpose.SignIn);
        }
        catch (ApiException refused) when (refused.Code == "email_queue_unavailable")
        {
            // A full replica's worker is already busy with its rows; no wake-up needed.
        }

        var store = services.GetRequiredService<AuthStore>();
        var waited = Stopwatch.StartNew();
        while ((await store.QueryAsync(AuthStore.MailOutboxCollection, Open, 1)).Count > 0)
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException("The authentication mail outbox did not drain.");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>The state of every outbox row, in creation order.</summary>
    public static async Task<string[]> MailStatesAsync(this AuthTestServer server)
    {
        using var all = JsonDocument.Parse("""{"exists":"/state"}""");
        return [.. (await server.App.Services.GetRequiredService<AuthStore>().QueryAsync(AuthStore.MailOutboxCollection, all.RootElement, 1000))
            .Select(row => row.Document.GetProperty("state").GetString()!)];
    }
}
