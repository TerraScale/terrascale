using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DeletionHistoryInspectionTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task History_correlation_is_scoped_to_the_receipts_generation_and_candidate_organizations()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (user, token) = await oidc.SignInUserAsync("erasure-reader@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var receipt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, "/v1/terrascale/account"))
        {
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            delete.Headers.Add("X-TerraScale-Deletion-Receipt", receipt);
            using var response = await oidc.Client.SendAsync(delete);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var jobKey = DeletionJob.Key("terrascale", "account", user.Id);
        var before = (await store.GetAsync(AuthStore.DeletionJobCollection, "terrascale", jobKey))!;
        var job = before.Document.Deserialize(DeletionJson.Default.DeletionJob)!;
        using (var result = await GetAsync())
        {
            await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(result.Headers.CacheControl!.NoStore).IsTrue();
            var raw = await result.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(raw);
            await Assert.That(json.RootElement.GetProperty("erasureId").GetString()).IsEqualTo(job.ErasureId);
            await Assert.That(json.RootElement.GetProperty("requestedAt").GetDateTimeOffset()).IsEqualTo(job.RequestedAt);
            await Assert.That(raw).DoesNotContain(user.Id);
            await Assert.That(raw).DoesNotContain(user.Email);
            await Assert.That(raw).DoesNotContain(receipt);
        }

        await Assert.That((await store.GetAsync(AuthStore.DeletionJobCollection, "terrascale", jobKey))!.Version).IsEqualTo(before.Version);
        using (var candidate = await GetAsync("?organization=" + organization))
        {
            await Assert.That(candidate.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var foreign = await GetAsync("?organization=" + AuthStore.NewId()))
        {
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using (var unknown = await GetAsync(capability: receipt[..42] + (receipt[^1] == 'A' ? 'E' : 'A')))
        {
            await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using (var malformed = await GetAsync(capability: "invalid"))
        {
            await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var missing = await oidc.Client.GetAsync("/v1/terrascale/account/deletion/history"))
        {
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(receipt)));
        var stored = (await store.GetAsync(AuthStore.AccountDeletionReceiptCollection, "terrascale", hash))!;
        var bound = stored.Document.Deserialize(DeletionJson.Default.AccountDeletionReceipt)!;
        await Assert.That(await store.CompareAndPutAsync(AuthStore.AccountDeletionReceiptCollection, "terrascale", hash,
            JsonSerializer.SerializeToElement(bound with { Generation = AuthStore.NewId() }, DeletionJson.Default.AccountDeletionReceipt), stored.Version)).IsTrue();
        using (var stale = await GetAsync())
        {
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }

        stored = (await store.GetAsync(AuthStore.AccountDeletionReceiptCollection, "terrascale", hash))!;
        await Assert.That(await store.CompareAndPutAsync(AuthStore.AccountDeletionReceiptCollection, "terrascale", hash,
            JsonSerializer.SerializeToElement(bound with { ExpiresAt = DateTimeOffset.UnixEpoch }, DeletionJson.Default.AccountDeletionReceipt), stored.Version)).IsTrue();
        using var expired = await GetAsync();
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        async Task<HttpResponseMessage> GetAsync(string query = "", string? capability = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/terrascale/account/deletion/history" + query);
            request.Headers.Add("X-TerraScale-Deletion-Receipt", capability ?? receipt);
            return await oidc.Client.SendAsync(request);
        }
    }
}
