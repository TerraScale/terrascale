namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Connections;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// Quota enforcement end to end: every user-creation surface (management create, hosted
/// sign-up, SCIM provisioning, SSO auto-create) must refuse to mint users once the
/// instance's plan quota (free = 100 live users) is reached. Seeds go straight through
/// the store so only the surface under test pays the quota walk.
[NotInParallel]
public sealed class QuotaE2eTests
{
    private const int FreeQuota = 100;

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Seeds <paramref name="count"/> live users directly in the store.</summary>
    private static async Task SeedUsersAsync(AuthStore store, string instanceId, int count)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < count; i++)
        {
            var email = $"seed{i:000}-{Unique()}@example.com";
            await store.CreateUserAsync(new User
            {
                Id = $"usr_quota{i:000}_{SecureTokens.RandomToken(8)}",
                InstanceId = instanceId,
                Email = email,
                EmailKey = $"{instanceId}:{email}",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private static HttpRequestMessage JsonPost(string path, string? bearer, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearer is not null)
            request.Headers.Authorization = new("Bearer", bearer);
        return request;
    }

    private static HttpRequestMessage FormPost(string path, Dictionary<string, string> form) => new(HttpMethod.Post, path)
    {
        Content = new FormUrlEncodedContent(form),
    };

    /// <summary>Walks the shared users collection until it finds a live user of the
    /// instance — the first raw page may hold only foreign instances' users.</summary>
    private static async Task<User> FirstLiveUserAsync(AuthStore store, string instanceId)
    {
        string? after = null;
        while (true)
        {
            var (page, next) = await store.ListUsersAsync(instanceId, 1000, after);
            if (page.FirstOrDefault(u => !u.Deleted) is { } user)
                return user;
            if (next is null)
                throw new InvalidOperationException($"no live user found for instance {instanceId}");
            after = next;
        }
    }

    [Test]
    public async Task ConcurrentCreationCannotExceedThePlanQuota()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, _, _) = await service.CreateInstanceAsync("quota-race-" + Unique());
        var sessions = new SessionService(service.Store);
        var attempts = Enumerable.Range(0, FreeQuota + 8)
            .Select(index => sessions.CreateUserAsync(instanceId, $"race-{index}-{Unique()}@example.com", null, null, null))
            .ToArray();
        var results = await Task.WhenAll(attempts.Select(async task =>
        {
            try { await task; return true; }
            catch (QuotaExceededException) { return false; }
        }));

        await Assert.That(results.Count(created => created)).IsEqualTo(FreeQuota);
        await Assert.That(await BillingState.CountUsersAsync(service.Store, instanceId, FreeQuota + 1)).IsEqualTo(FreeQuota);
    }

    [Test]
    public async Task QuotaSeatReconciliationRemovesOrphanedClaims()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, _, _) = await service.CreateInstanceAsync("quota-reconcile-" + Unique());

        var sessions = new SessionService(service.Store);
        var user = await sessions.CreateUserAsync(instanceId, $"owner-{Unique()}@example.com", null, null, null);
        var orphanSeat = await service.Store.ClaimQuotaSeatAsync(instanceId, FreeQuota, "usr_missing");

        var result = await service.Store.ReconcileQuotaSeatsAsync(instanceId);

        await Assert.That(result.ClaimsExamined).IsEqualTo(2);
        await Assert.That(result.ClaimsRemoved).IsEqualTo(1);
        await Assert.That(result.ClaimsRetained).IsEqualTo(1);
        await Assert.That(orphanSeat).IsNotEqualTo((int)user.PrivateMetadata["quotaSeat"]);
    }

    private static async Task<string> CreateDirectoryAsync(HttpClient http, string apiKey, string slug)
    {
        using var request = JsonPost($"/v1/{slug}/directories", apiKey, new { });
        using var response = await http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    [Test]
    public async Task ManagementCreateAllowsTheLastSeatThenRejectsWithQuotaExceeded()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("quota-mgmt-" + Unique());
        await SeedUsersAsync(service.Store, instanceId, FreeQuota - 1);

        using var last = await service.Http.SendAsync(JsonPost($"/v1/{slug}/users", apiKey, new { email = $"last-{Unique()}@example.com" }));
        await Assert.That(last.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var over = await service.Http.SendAsync(JsonPost($"/v1/{slug}/users", apiKey, new { email = $"over-{Unique()}@example.com" }));
        await Assert.That(over.StatusCode).IsEqualTo(HttpStatusCode.PaymentRequired);
        var error = await over.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("quota_exceeded");
    }

    [Test]
    public async Task DeletedUsersFreeQuotaForNewCreation()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("quota-del-" + Unique());
        await SeedUsersAsync(service.Store, instanceId, FreeQuota);

        var victim = await FirstLiveUserAsync(service.Store, instanceId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/{slug}/users/{victim.Id}");
        request.Headers.Authorization = new("Bearer", apiKey);
        using var deleted = await service.Http.SendAsync(request);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var created = await service.Http.SendAsync(JsonPost($"/v1/{slug}/users", apiKey, new { email = $"after-del-{Unique()}@example.com" }));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task HostedSignUpRefusesAtQuotaWithoutCreatingAUser()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("quota-ui-" + Unique());
        await SeedUsersAsync(service.Store, instanceId, FreeQuota);

        var email = $"ui-{Unique()}@example.com";
        using var response = await service.Http.SendAsync(FormPost($"/ui/{slug}/sign-up", new()
        {
            ["email"] = email,
            ["password"] = "password123",
            ["name"] = "Over Quota",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await response.Content.ReadAsStringAsync();
        await Assert.That(page).Contains("user limit");
        await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, email)).IsNull();
    }

    [Test]
    public async Task ScimProvisioningRefusesAtQuotaWithScimErrorEnvelope()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("quota-scim-" + Unique());
        var token = await CreateDirectoryAsync(service.Http, apiKey, slug);
        await SeedUsersAsync(service.Store, instanceId, FreeQuota);

        var email = $"scim-over-{Unique()}@example.com";
        using var request = JsonPost($"/scim/{(await service.Store.ListScimDirectoriesAsync(instanceId)).Single().Id}/Users", token,
            new { userName = email });
        request.Content!.Headers.ContentType = new("application/scim+json");
        using var response = await service.Http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/scim+json");
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(error.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:api:messages:2.0:Error");
        await Assert.That(error.GetProperty("status").GetString()).IsEqualTo("403");
        await Assert.That(await service.Store.FindUserByEmailAsync(instanceId, email)).IsNull();
    }

    [Test]
    public async Task ScimProvisioningAllowsTheLastSeat()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("quota-scim-last-" + Unique());
        var token = await CreateDirectoryAsync(service.Http, apiKey, slug);
        await SeedUsersAsync(service.Store, instanceId, FreeQuota - 1);

        var directoryId = (await service.Store.ListScimDirectoriesAsync(instanceId)).Single().Id;
        using var request = JsonPost($"/scim/{directoryId}/Users", token, new { userName = $"scim-last-{Unique()}@example.com" });
        request.Content!.Headers.ContentType = new("application/scim+json");
        using var response = await service.Http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task SsoAutoCreateRefusesNewIdentitiesAtQuotaButExistingUsersStillSignIn()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("quota-sso-" + Unique());
        await SeedUsersAsync(service.Store, instanceId, FreeQuota);

        var instance = (await service.Store.GetInstanceAsync(instanceId))!;
        var sessions = new SessionService(service.Store);

        // A brand-new identity cannot be minted past the ceiling (OIDC and SAML both
        // funnel through FindOrCreateUserAsync).
        var created = await ConnectionFlow.FindOrCreateUserAsync(service.Store, sessions, instance, $"new-{Unique()}@example.com", emailVerified: true, null, default);
        await Assert.That(created).IsNull();

        // An existing user at quota is returned untouched — the ceiling never locks out
        // current users from SSO sign-in.
        var existingEmail = (await FirstLiveUserAsync(service.Store, instanceId)).Email!;
        var existing = await ConnectionFlow.FindOrCreateUserAsync(service.Store, sessions, instance, existingEmail, emailVerified: true, null, default);
        await Assert.That(existing).IsNotNull();
        await Assert.That(existing!.Email).IsEqualTo(existingEmail);
    }
}
