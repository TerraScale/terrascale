using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Slow;

/// <summary>
/// Key-ring and attempt-limit state under concurrent requests to two replicas that share one
/// real TerraBase database and master key. Correctness comes from conditional writes alone.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SharedStateConcurrencyTests(TerraBaseFixture fixture)
{
    // Above eight, so contention outlasts a fixed retry budget and must still end in 200 or 429.
    private const int Maximum = 12;

    private static Task<AuthTestServer> StartReplicaAsync(SharedStateTarget target, string masterKey, TimeProvider clock) => AuthTestServer.StartAsync(
        settings: values =>
        {
            target.Bind(values);
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

    private async Task<(AuthTestServer First, AuthTestServer Second, SharedStateTarget Store)> StartPairAsync()
    {
        var store = await SharedStateTarget.CreateAsync(fixture);
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
    public async Task Concurrent_first_use_on_two_replicas_creates_exactly_one_key()
    {
        var (first, second, store) = await StartPairAsync();
        await using var _ = first;
        await using var __ = second;

        var kids = await Task.WhenAll(Enumerable.Range(0, 40).Select(async index =>
        {
            var server = index % 2 == 0 ? first : second;
            var body = await server.Client.GetFromJsonAsync<JsonElement>("/__test/instances/ins_race/kid");
            return body.GetProperty("kid").GetString();
        }));

        await Assert.That(kids.Distinct().Count()).IsEqualTo(1);
        await Assert.That((await KeysAsync(store, "ins_race")).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_rotations_on_two_replicas_lose_no_key()
    {
        var (first, second, store) = await StartPairAsync();
        await using var _ = first;
        await using var __ = second;
        var initial = (await first.Client.GetFromJsonAsync<JsonElement>("/__test/instances/ins_rotate/kid")).GetProperty("kid").GetString();

        // Six rotators: each lost write implies another rotation committed, so every one
        // finishes within the eight conditional attempts.
        var rotated = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            using var response = await (index % 2 == 0 ? first : second).Client.PostAsync("/__test/instances/ins_rotate/keys/rotate", null);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kid").GetString();
        }));
        var stored = await KeysAsync(store, "ins_rotate");

        await Assert.That(stored.Count).IsEqualTo(7);
        await Assert.That(stored.Distinct().Count()).IsEqualTo(7);
        await Assert.That(stored[0]).IsEqualTo(initial);
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
        [.. (await store.ReadAsync(KeyRing.Collection, instance, "ring"))!.Value.GetProperty("keys").EnumerateArray().Select(key => key.GetProperty("id").GetString()!)];
}
