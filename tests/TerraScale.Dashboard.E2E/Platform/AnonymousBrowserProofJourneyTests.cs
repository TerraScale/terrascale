using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Identity;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
public sealed class AnonymousBrowserProofJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task DiscardedHandlesHaveSharedAdmissionAndExpiredPayloadsAreReclaimedAcrossReplicas()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        var clock = new Clock();
        await using var first = Host();
        await using var second = Host();
        await first.StartAsync();
        await second.StartAsync();
        using var a = Client(first);
        using var b = Client(second);
        var original = await a.GetStringAsync("/__test/anonymous-proof");
        await Assert.That(original.Length).IsEqualTo(66);
        await Assert.That(original.StartsWith("a.", StringComparison.Ordinal)).IsTrue();
        await Assert.That(AnonymousBrowserProofs.IsHandle(original)).IsTrue();
        var stored = await Pool();
        await Assert.That(stored.GetProperty("count").GetInt32()).IsEqualTo(1);
        await Assert.That(stored.GetProperty("contains").GetBoolean()).IsTrue();
        await Assert.That(stored.GetProperty("valid").GetBoolean()).IsTrue();
        using (var retained = await Read(b, original))
            await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // Prepare capacity with production store operations behind one test-only HTTP route.
        using (var filled = await a.PostAsync("/__test/anonymous-proof/fill", null))
            await Assert.That(filled.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var raced = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => (index % 2 == 0 ? a : b).GetAsync("/__test/anonymous-proof")));
        await Assert.That(raced.Count(response => response.StatusCode == HttpStatusCode.OK)).IsEqualTo(2);
        await Assert.That(raced.Count(response => response.StatusCode == HttpStatusCode.ServiceUnavailable)).IsEqualTo(6);
        foreach (var response in raced) response.Dispose();
        await Assert.That((await Pool()).GetProperty("count").GetInt32()).IsEqualTo(AnonymousBrowserProofs.Capacity);
        await Assert.That(environment.State.Items.Keys.Count(key => key.StartsWith("auth-flows/", StringComparison.Ordinal))).IsEqualTo(1);
        var before = environment.State.Items["auth-flows/" + AnonymousBrowserProofs.Id].Document.GetRawText();
        using (var denied = await b.GetAsync("/__test/anonymous-proof"))
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(environment.State.Items["auth-flows/" + AnonymousBrowserProofs.Id].Document.GetRawText() == before).IsTrue();
        clock.Now += TimeSpan.FromHours(2);
        string replacement;
        using (var renewed = await b.GetAsync("/__test/anonymous-proof"))
        {
            await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            replacement = await renewed.Content.ReadAsStringAsync();
        }
        await Assert.That((await Pool()).GetProperty("count").GetInt32()).IsEqualTo(1).Because("expired ciphertext payloads are removed by the same CAS admission write");
        using (var expired = await Read(a, original))
            await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var removed = await a.PostAsync("/__test/anonymous-proof/remove", Form(replacement)))
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(environment.State.Items.ContainsKey("auth-flows/" + AnonymousBrowserProofs.Id)).IsFalse();
        await first.StopAsync();
        await second.StopAsync();

        WebApplication Host()
        {
            var app = environment.CreateTestHost(fixture.Root, 0, "https://127.0.0.1", builder =>
                builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
            AnonymousBrowserProofs Store(IDashboardIdentity identity) => new(environment.State, identity, clock);
            BrowserProof Proof() => new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), "", clock.Now.AddHours(1));
            app.MapGet("/__test/anonymous-proof", async Task<IResult> (HttpContext http, IDashboardIdentity identity) => Results.Text(await Store(identity).AddAsync(Proof(), http.RequestAborted)));
            app.MapPost("/__test/anonymous-proof/read", async Task<IResult> (HttpContext http, IDashboardIdentity identity) =>
                await Store(identity).ReadAsync((await http.Request.ReadFormAsync())["handle"].ToString(), http.RequestAborted) is null ? Results.NotFound() : Results.Ok());
            app.MapPost("/__test/anonymous-proof/remove", async Task<IResult> (HttpContext http, IDashboardIdentity identity) =>
            {
                await Store(identity).RemoveAsync((await http.Request.ReadFormAsync())["handle"].ToString(), http.RequestAborted);
                return Results.NoContent();
            });
            app.MapPost("/__test/anonymous-proof/fill", async Task<IResult> (HttpContext http, IDashboardIdentity identity) =>
            {
                for (var index = 0; index < AnonymousBrowserProofs.Capacity - 3; index++) await Store(identity).AddAsync(Proof(), http.RequestAborted);
                return Results.NoContent();
            });
            app.MapPost("/__test/anonymous-proof/pool", async Task<IResult> (HttpContext http, IDashboardIdentity identity) =>
            {
                var credentials = await identity.ReadAsync(http.RequestAborted);
                var proofs = AuthFlows.Open<Dictionary<string, BrowserProof>>(environment.State.Items["auth-flows/" + AnonymousBrowserProofs.Id].Document,
                    credentials.SessionEncryptionKey, AnonymousBrowserProofs.Purpose);
                var handle = (await http.Request.ReadFormAsync())["handle"].ToString();
                var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(handle)));
                return Results.Json(new { count = proofs.Count, contains = proofs.ContainsKey(key), valid = proofs.TryGetValue(key, out var proof) && proof.ExpiresAt > clock.Now });
            });
            return app;
        }

        async Task<JsonElement> Pool()
        {
            using var response = await a.PostAsync("/__test/anonymous-proof/pool", Form(original));
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }
    }

    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler
    { ServerCertificateCustomValidationCallback = TestHttps.Validate, UseCookies = false })
    { BaseAddress = new(app.Urls.Single()) };
    private static FormUrlEncodedContent Form(string handle) => new(new Dictionary<string, string> { ["handle"] = handle });
    private static Task<HttpResponseMessage> Read(HttpClient client, string handle) => client.PostAsync("/__test/anonymous-proof/read", Form(handle));
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
