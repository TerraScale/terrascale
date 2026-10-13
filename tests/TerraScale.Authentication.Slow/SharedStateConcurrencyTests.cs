using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow;

/// <summary>
/// Key-ring and attempt-limit state under concurrent requests to two replicas that share one
/// real TerraBase database and master key.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SharedStateConcurrencyTests(TerraBaseFixture fixture)
{
    // Above eight, so contention outlasts a fixed retry budget and must still end in 200 or 429.
    private const int Maximum = 12;

    private static Task<AuthTestServer> StartReplicaAsync(SharedStateTarget target, string masterKey, TimeProvider clock) => AuthTestServer.StartAsync(
        settings: values =>
        {
            values["master-key"] = masterKey;
        },
        services: services =>
        {
            target.Register(services);
            services.AddSingleton(clock);
        },
        routes: app =>
        {
            app.MapGet("/__test/instances/{instance}/kid", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
            {
                var keys = await ring.GetActiveAsync(instance, cancellationToken);
                var kid = keys[0].KeyId;
                keys.ToList().ForEach(key => key.Dispose());
                return new { kid };
            });
            app.MapPost("/__test/instances/{instance}/keys/rotate", async (string instance, KeyRing ring, CancellationToken cancellationToken) =>
            {
                using var key = await ring.RotateAsync(instance, cancellationToken);
                return new { kid = key.KeyId };
            });
            app.MapPost("/__test/attempts/{subject}", async (string subject, AttemptLimiter limiter, CancellationToken cancellationToken) =>
                await limiter.TryAcquireAsync("ins_a", "sign-in", subject, Maximum, cancellationToken)
                    ? Results.Ok()
                    : Results.StatusCode(StatusCodes.Status429TooManyRequests));
        });

    private async Task<(AuthTestServer First, AuthTestServer Second, SharedStateTarget Store)> StartPairAsync(string? projectId = null)
    {
        var store = await SharedStateTarget.CreateAsync(fixture);
        if (projectId is not null)
        {
            // Project keys are created only while their project exists at the storage authority.
            await store.PutAsync(AuthStore.ProjectCollection, OidcIssuer.InstanceId, projectId,
                TerraBaseFixture.Json($$"""{"id":"{{projectId}}"}"""));
        }

        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 4, 12, 0, 5, TimeSpan.Zero));
        var first = await StartReplicaAsync(store, masterKey, clock);
        try
        {
            return (first, await StartReplicaAsync(store, masterKey, clock), store);
        }
        catch
        {
            await first.DisposeAsync();
            throw;
        }
    }

    [Test]
    public async Task Concurrent_first_use_on_two_replicas_publishes_every_key_and_converges_on_one_signer()
    {
        var projectId = AuthStore.NewId();
        var (first, second, store) = await StartPairAsync(projectId);
        await using var _ = first;
        await using var __ = second;

        var kids = await Task.WhenAll(Enumerable.Range(0, 40).Select(async index =>
        {
            var server = index % 2 == 0 ? first : second;
            var body = await server.Client.GetFromJsonAsync<JsonElement>($"/__test/instances/{projectId}/kid");
            return body.GetProperty("kid").GetString();
        }));

        // Replicas may each add a first key; every key that signed is published, and all agree on the signer after.
        var stored = await KeysAsync(store, projectId);
        await Assert.That(kids.All(kid => kid is not null && stored.Contains(kid))).IsTrue();
        var signers = await Task.WhenAll(new[] { first, second }.Select(async server =>
            (await server.Client.GetFromJsonAsync<JsonElement>($"/__test/instances/{projectId}/kid")).GetProperty("kid").GetString()));
        await Assert.That(signers.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_rotations_on_two_replicas_lose_no_key()
    {
        var projectId = AuthStore.NewId();
        var (first, second, store) = await StartPairAsync(projectId);
        await using var _ = first;
        await using var __ = second;
        var initial = (await first.Client.GetFromJsonAsync<JsonElement>($"/__test/instances/{projectId}/kid")).GetProperty("kid").GetString();

        // Six rotators: every rotation stores its own key, so none is lost.
        var rotated = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            using var response = await (index % 2 == 0 ? first : second).Client.PostAsync($"/__test/instances/{projectId}/keys/rotate", null);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kid").GetString();
        }));
        var stored = await KeysAsync(store, projectId);

        await Assert.That(stored.Count).IsEqualTo(7);
        await Assert.That(stored.Distinct().Count()).IsEqualTo(7);
        await Assert.That(stored).Contains(initial!);
        await Assert.That(rotated.All(kid => kid is not null && stored.Contains(kid))).IsTrue();
    }

    [Test]
    public async Task Concurrent_attempts_on_two_replicas_admit_exactly_the_maximum()
    {
        var (first, second, _) = await StartPairAsync();
        await using var a = first;
        await using var b = second;

        var statuses = await Task.WhenAll(Enumerable.Range(0, 60).Select(async index =>
        {
            using var response = await (index % 2 == 0 ? first : second).Client.PostAsync("/__test/attempts/user_1", null);
            return response.StatusCode;
        }));

        await Assert.That(statuses.Count(status => status == HttpStatusCode.OK)).IsEqualTo(Maximum);
        await Assert.That(statuses.Count(status => status == HttpStatusCode.TooManyRequests)).IsEqualTo(60 - Maximum);
    }

    private static async Task<List<string>> KeysAsync(SharedStateTarget store, string instance) =>
        [.. (await store.Database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items
            .Where(document => document.Document.GetProperty("instanceId").GetString() == instance)
            .Select(document => document.Document.GetProperty("id").GetString()!)];
}
