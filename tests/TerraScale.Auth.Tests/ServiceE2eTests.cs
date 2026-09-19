namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>Instance lifecycle and SDK persistence against the embedded engine.</summary>
[NotInParallel]
public sealed class ServiceE2eTests
{
    private static Task<AuthServiceFixture> FixtureAsync() => AuthServiceFixture.GetAsync();

    [Test]
    public async Task HealthReportsOk()
    {
        var service = await FixtureAsync();
        var response = await service.Http.GetAsync("/healthz");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task OperatorKeyProtectsInstanceCreation()
    {
        var service = await FixtureAsync();
        var unauthorized = await service.Http.PostAsJsonAsync("/v1/instances", new { slug = "acme", name = "Acme" });
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task OperatorCreatesInstanceAndApiKey()
    {
        var service = await FixtureAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/instances")
        {
            Content = JsonContent.Create(new { slug = "acme-" + Guid.NewGuid().ToString("N")[..6], name = "Acme Corp" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "operator-test-key");
        var response = await service.Http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("api_key").GetString()!.StartsWith("sk_")).IsTrue();
        await Assert.That(body.GetProperty("instance").GetProperty("slug").GetString()!.StartsWith("acme-")).IsTrue();
    }

    [Test]
    public async Task DuplicateInstanceSlugConflicts()
    {
        var service = await FixtureAsync();
        var slug = "dupe-" + Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/instances")
            {
                Content = JsonContent.Create(new { slug, name = "Dupe" }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "operator-test-key");
            var response = await service.Http.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(i == 0 ? HttpStatusCode.OK : HttpStatusCode.Conflict);
        }
    }

    [Test]
    public async Task StoreWritesThroughTheSdKToTheEngine()
    {
        var service = await FixtureAsync();
        var store = new TerraScale.Auth.Store.AuthStore(service.Sdk, service.Database);
        var userId = "usr_" + Guid.NewGuid().ToString("N")[..12];
        await store.CreateUserAsync(new TerraScale.Auth.Store.User
        {
            Id = userId,
            InstanceId = "ins_test",
            Email = "store-e2e@example.com",
            EmailKey = "ins_test:store-e2e@example.com",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var fetched = await store.GetUserAsync(userId);
        await Assert.That(fetched!.Email).IsEqualTo("store-e2e@example.com");
        var found = await store.FindUserByEmailAsync("ins_test", "store-e2e@example.com");
        await Assert.That(found!.Id).IsEqualTo(userId);
    }
}
