using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Slow.Management;

/// <summary>
/// TS-873 shared-history erasure end to end: ts-auth on its platform database of a real TerraBase (built from this commit, its
/// own process so its SYSTEM and auth files are rewritten without disturbing other tests), through the public SDK over the
/// authority's peer transport. TerraBase's own passes run on its 10 s timer.
/// </summary>
public sealed class HistoryErasureTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [Test]
    public async Task Account_history_erasure_rewrites_the_shared_files_and_never_reports_completion_or_retires_the_fence_early()
    {
        var (started, unavailable) = await TerraBaseTestServer.StartAsync();
        await using var terrabase = started ?? throw new InvalidOperationException(unavailable);
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var auth = await AuthTestServer.StartAsync(
            settings: values =>
            {
                values["terrabase-url"] = terrabase.InternalAddress.GetLeftPart(UriPartial.Authority);
                values["dashboard-client-secret"] = OidcHarness.ClientSecret;
                values["dashboard-redirect-uris"] = OidcHarness.RedirectUri;
            },
            services: services => services.AddSingleton<TimeProvider>(clock));
        var store = auth.App.Services.GetRequiredService<AuthStore>();
        var users = auth.App.Services.GetRequiredService<Users>();
        var cleanup = auth.App.Services.GetRequiredService<DeletionCleanup>();
        // As the host does at startup; TerraBase seeds the platform database in the background first.
        var provisioned = DateTime.UtcNow + Deadline;
        while (true)
        {
            try
            {
                await store.ProvisionAsync();
                break;
            }
            catch (TerraBaseException) when (DateTime.UtcNow < provisioned)
            {
                await Task.Delay(500);
            }
        }

        var email = $"erased-{Guid.NewGuid():N}@example.test";
        var survivorEmail = $"kept-{Guid.NewGuid():N}@example.test";
        var user = (await users.CreateAsync(PlatformDirectory.Instance, email, null, CancellationToken.None))!;
        var survivor = (await users.CreateAsync(PlatformDirectory.Instance, survivorEmail, null, CancellationToken.None))!;

        // A subject-owned record carrying a unique payload: logical cleanup deletes it, the rewrites remove its history.
        var secret = "ts873-mfa-" + Guid.NewGuid().ToString("N");
        await store.CreateAsync(AuthStore.MfaCollection, user.InstanceId, user.Id, TerraBaseFixture.Json(
            $$"""{"instanceId":"{{user.InstanceId}}","userId":"{{user.Id}}","secretBox":"{{secret}}","enabled":false,"enrollmentEpoch":0,"enrollmentExpiresAt":"2099-01-01T00:00:00Z","lastAcceptedStep":-1,"recoveryHashes":[],"recoveryExpiresAt":null}"""));
        await users.DeleteAsync(PlatformDirectory.Instance, user.Id);
        await cleanup.RunOnceAsync();
        var job = await JobAsync(store, user.Id);
        await Assert.That(job.AuthLogicalVerifiedAt).IsNotNull();
        await Assert.That(job.FenceExpiresAt).IsEqualTo(job.RequestedAt + DeletionJob.CredentialBound);

        // The email is reusable at once (a new owner's claim), and the erasure must not touch it.
        var reused = (await users.CreateAsync(PlatformDirectory.Instance, email, null, CancellationToken.None))!;

        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline && (await JobAsync(store, user.Id)).HistoryStatus != "residue")
        {
            clock.UtcNow += TimeSpan.FromMinutes(11);
            await cleanup.RunHistoryOnceAsync();
            await Task.Delay(1000);
        }

        job = await JobAsync(store, user.Id);
        await Assert.That(job.HistoryStatus).IsEqualTo("residue").Because("every holder rewrote both files; tombstone and feed residue at or below the cut remain for up to 7 days");
        await Assert.That(job.HistoryCompletedAt).IsNull();

        // Shared-file rewrites retain the current outbox proof. Dashboard can recover it without a user or receipt.
        using var list = new HttpRequestMessage(HttpMethod.Get, "/v1/terrascale/deletions");
        list.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
        using var listed = await auth.Client.SendAsync(list);
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var proof = (await listed.Content.ReadFromJsonAsync<DashboardDeletionList>(DashboardDeletionJson.Default.DashboardDeletionList))!.Items.Single(item => item.Id == job.ErasureId);
        var document = (await store.GetAsync(AuthStore.DeletionJobCollection, PlatformDirectory.Instance, job.Id))!;
        await Assert.That(await store.RetireDeletionFenceAsync(job, document.Version)).IsFalse();
        using var ack = new HttpRequestMessage(HttpMethod.Post, $"/v1/terrascale/deletions/{proof.Id}/acknowledge");
        ack.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcIssuer.DashboardClientId}:{OidcHarness.ClientSecret}")));
        ack.Content = JsonContent.Create(new DashboardDeletionAcknowledgement(proof.Scope, proof.SubjectId, proof.OrganizationId), DashboardDeletionJson.Default.DashboardDeletionAcknowledgement);
        using var acknowledged = await auth.Client.SendAsync(ack);
        await Assert.That(acknowledged.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await JobAsync(store, user.Id)).DashboardVerifiedAt).IsNotNull();

        // Past the credential bound the fence stays: the erasure is not complete.
        clock.UtcNow = job.FenceExpiresAt!.Value.AddMinutes(1);
        await cleanup.RunHistoryOnceAsync();
        await Assert.That(await store.GetAsync(AuthStore.DeletionJobCollection, PlatformDirectory.Instance, job.Id)).IsNotNull();

        await Assert.That(await users.GetAsync(PlatformDirectory.Instance, survivor.Id)).IsNotNull();
        await Assert.That((await users.GetAsync(PlatformDirectory.Instance, reused.Id))?.Email).IsEqualTo(email);
        await Assert.That(await users.GetAsync(PlatformDirectory.Instance, user.Id)).IsNull();

        // The deleted record's payload is in no file of TerraBase's data directory: SYSTEM, the auth file, sidecars, scratch.
        await terrabase.StopAsync();
        var marker = Encoding.UTF8.GetBytes(secret);
        foreach (var file in Directory.EnumerateFiles(terrabase.StoppedDataDirectory, "*", SearchOption.AllDirectories))
        {
            await Assert.That((await File.ReadAllBytesAsync(file)).AsSpan().IndexOf(marker) >= 0).IsFalse().Because(file);
        }
    }

    private static async Task<DeletionJob> JobAsync(AuthStore store, string userId)
    {
        var key = DeletionJob.Key(PlatformDirectory.Instance, "account", userId);
        var document = await store.GetAsync(AuthStore.DeletionJobCollection, PlatformDirectory.Instance, key) ?? throw new InvalidOperationException("the deletion job is missing");
        return document.Document.Deserialize(DeletionJson.Default.DeletionJob)!;
    }
}
