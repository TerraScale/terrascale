using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>Real HTTP and SDK persistence for the durable dashboard deletion handoff.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DashboardDeletionTests(TerraBaseFixture fixture)
{
    private const string Route = "/v1/terrascale/deletions";

    [Test]
    public async Task Direct_auth_deletion_without_a_receipt_survives_restart_and_acknowledges_only_the_exact_proof()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var auth = await OidcHarness.StartAsync(fixture, clock: clock);
        var store = auth.Server.App.Services.GetRequiredService<AuthStore>();
        var cleanup = auth.Server.App.Services.GetRequiredService<DeletionCleanup>();
        await cleanup.StopAsync(CancellationToken.None);
        var (user, token) = await auth.SignInUserAsync("direct@example.test");
        await auth.OkAsync(HttpMethod.Delete, "/v1/terrascale/account", token, status: HttpStatusCode.NoContent);
        await cleanup.RunOnceAsync();
        var proof = (await ListAsync(auth.Client)).Single(item => item.SubjectId == user.Id);
        await Assert.That(proof.Scope).IsEqualTo("account");
        await Assert.That(proof.OrganizationId).IsNull();
        var job = await JobAsync(store, proof);
        await Assert.That(proof.Id).IsEqualTo(job.ErasureId);
        await Assert.That(job.DashboardVerifiedAt).IsNull();
        await Assert.That((await ListAsync(auth.Client)).Single(item => item.SubjectId == user.Id)).IsEqualTo(proof);
        await Assert.That((await JobAsync(store, proof)).DashboardVerifiedAt).IsNull();

        await auth.Server.App.StopAsync();
        await using var restarted = await auth.StartReplicaAsync(clock: clock);
        var restartedCleanup = restarted.App.Services.GetRequiredService<DeletionCleanup>();
        await restartedCleanup.StopAsync(CancellationToken.None);
        await Assert.That((await ListAsync(restarted.Client)).Single(item => item.SubjectId == user.Id)).IsEqualTo(proof);
        foreach (var wrong in new[]
        {
            proof with { Id = AuthStore.NewId() }, proof with { SubjectId = AuthStore.NewId() },
            proof with { OrganizationId = AuthStore.NewId() }, proof with { Scope = "organization" },
        })
        {
            using var refused = await SendAsync(restarted.Client, HttpMethod.Post, AckRoute(wrong), wrong);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // Simulate completed history and an expired credential fence: dashboard acknowledgement still owns retention.
        var restartedStore = restarted.App.Services.GetRequiredService<AuthStore>();
        var document = (await restartedStore.GetAsync(AuthStore.DeletionJobCollection, "terrascale", job.Id))!;
        clock.UtcNow = job.RequestedAt.AddDays(16);
        var complete = job with { HistoryCompletedAt = clock.UtcNow, HistoryStatus = "retire", NextAttemptAt = clock.UtcNow };
        await Assert.That(await restartedStore.CompareAndPutAsync(AuthStore.DeletionJobCollection, "terrascale", job.Id,
            JsonSerializer.SerializeToElement(complete, DeletionJson.Default.DeletionJob), document.Version)).IsTrue();
        await restartedCleanup.RunHistoryOnceAsync();
        await Assert.That((await JobAsync(restartedStore, proof)).DashboardVerifiedAt).IsNull();
        await Assert.That((await ListAsync(restarted.Client)).Any(item => item.Id == proof.Id)).IsTrue();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var accepted = await SendAsync(restarted.Client, HttpMethod.Post, AckRoute(proof), proof);
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        await Assert.That((await JobAsync(restartedStore, proof)).DashboardVerifiedAt).IsEqualTo(clock.UtcNow);
        await Assert.That((await ListAsync(restarted.Client)).Any(item => item.Id == proof.Id)).IsFalse();
        await restartedCleanup.RunHistoryOnceAsync();
        await Assert.That(await restartedStore.GetAsync(AuthStore.DeletionJobCollection, "terrascale", job.Id)).IsNull();
    }

    private static async Task<DeletionJob> JobAsync(AuthStore store, DashboardDeletionProof proof) =>
        (await store.GetAsync(AuthStore.DeletionJobCollection, "terrascale", DeletionJob.Key("terrascale", proof.Scope, proof.SubjectId)))!.Document.Deserialize(DeletionJson.Default.DeletionJob)!;

    private static string AckRoute(DashboardDeletionProof proof) => $"{Route}/{proof.Id}/acknowledge";

    private static string Basic(string id = OidcIssuer.DashboardClientId, string secret = OidcHarness.ClientSecret) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}"));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, DashboardDeletionProof? proof = null, string? credential = "default")
    {
        using var request = new HttpRequestMessage(method, path);
        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", credential == "default" ? Basic() : credential);
        }

        if (proof is not null && method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new DashboardDeletionAcknowledgement(proof.Scope, proof.SubjectId, proof.OrganizationId), DashboardDeletionJson.Default.DashboardDeletionAcknowledgement);
        }

        return await client.SendAsync(request);
    }

    private static async Task<IReadOnlyList<DashboardDeletionProof>> ListAsync(HttpClient client)
    {
        using var response = await SendAsync(client, HttpMethod.Get, Route);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync(DashboardDeletionJson.Default.DashboardDeletionList))!.Items;
    }
}
