using TerraScale.DotNet.Native;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeOriginTests
{
    [Test]
    public async Task CanonicalNonreservedRegionOriginsAreAccepted()
    {
        foreach (var origin in new[] { "https://gru.terrascale.tech", "https://fra.terrascale.tech/", "https://us-east-1.terrascale.tech" })
            await Assert.That(NativeOrigin.Validate(origin).GetLeftPart(UriPartial.Authority)).IsEqualTo(origin.TrimEnd('/'));
    }

    [Test]
    public async Task InvalidTerraScaleOriginsFailBeforeCredentialResolutionOrDispatch()
    {
        var calls = 0;
        using var http = new HttpClient(new NativeClientTests.Handler((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Unexpected data dispatch.");
        }));
        foreach (var origin in new[]
        {
            "https://terrascale.tech", "https://atlas.terrascale.tech", "https://auth.terrascale.tech",
            "https://status.terrascale.tech", "https://gru.auth.terrascale.tech", "https://a.gru.terrascale.tech",
            "https://GRU.terrascale.tech", "HTTPS://gru.terrascale.tech", "https://gru.terrascale.tech.",
            "https://gru.terrascale.tech:8443", "https://gru.terrascale.tech:443", "https://gru.terrascale.tech/path"
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                using var client = new NativeClient(http, NativeCodecTests.Scope, origin,
                    _ => { calls++; return ValueTask.FromResult("credential-canary"); }, TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            });
        }
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ExplicitIndependentProvisionedHttpsOriginsRemainAvailable()
    {
        foreach (var origin in new[] { "https://database.customer.example", "https://database.customer.example:8443" })
            await Assert.That(NativeOrigin.Validate(origin).GetLeftPart(UriPartial.Authority)).IsEqualTo(origin);
    }

    [Test]
    public async Task LoopbackOriginsRequireExplicitDevelopmentAndStillRejectOriginPayloads()
    {
        foreach (var origin in new[] { "http://127.0.0.1:12345", "http://localhost:12345", "https://localhost:12345" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => { NativeOrigin.Validate(origin); return Task.CompletedTask; });
            await Assert.That(NativeOrigin.Validate(origin, development: true).IsLoopback).IsTrue();
        }
        foreach (var origin in new[] { "http://database.customer.example", "https://user:secret@gru.terrascale.tech", "https://gru.terrascale.tech?secret=x", "https://gru.terrascale.tech/#secret" })
            await Assert.ThrowsAsync<ArgumentException>(() => { NativeOrigin.Validate(origin, development: true); return Task.CompletedTask; });
    }

    [Test]
    public async Task TrustedAtlasAuthorityIsSeparateFromDataOriginAdmission()
    {
        var requested = new List<string>();
        using var http = new HttpClient(new NativeClientTests.Handler((request, _) =>
        {
            requested.Add(request.RequestUri!.Host);
            return Task.FromResult(NativeClientTests.Json("{\"error\":{\"code\":\"discovery_forbidden\"}}", System.Net.HttpStatusCode.Forbidden));
        }));
        foreach (var origin in new[] { "https://atlas.terrascale.tech", "https://atlas.customer.example" })
        {
            using var router = new NativeDiscoveryRouter(http, TimeSpan.FromSeconds(1), new Uri(origin));
            await Assert.ThrowsAsync<NativeAdmissionException>(() => router.ResolveAsync(
                NativeCodecTests.Scope with { Shard = 1 }, "tbk1." + Encode(16) + "." + Encode(32), ["read"], CancellationToken.None).AsTask());
        }
        await Assert.That(requested.SequenceEqual(["atlas.terrascale.tech", "atlas.customer.example"])).IsTrue();
    }

    private static string Encode(int length) => Convert.ToBase64String(new byte[length]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
