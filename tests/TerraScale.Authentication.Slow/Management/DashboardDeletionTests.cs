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

namespace TerraScale.Authentication.Slow.Management;

/// <summary>Real HTTP and SDK persistence for the durable dashboard deletion handoff.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DashboardDeletionTests(TerraBaseFixture fixture)
{
    private const string Route = "/v1/terrascale/deletions";

    [Test]
    public async Task Only_the_dashboard_basic_client_can_list_or_acknowledge_platform_proofs()
    {
        await using var auth = await OidcHarness.StartAsync(fixture);
        var proof = new DashboardDeletionProof(AuthStore.NewId(), "account", AuthStore.NewId(), null);
        foreach (var credential in new string?[] { null, "Bearer " + OidcHarness.ClientSecret, "Basic !!!", Basic("wrong"), Basic(OidcIssuer.DashboardClientId, "wrong") })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
            {
                using var response = await SendAsync(auth.Client, method, method == HttpMethod.Get ? Route : AckRoute(proof), proof, credential);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                await Assert.That(response.Headers.WwwAuthenticate.ToString()).StartsWith("Basic realm=\"ts-auth\"");
            }
        }

        using var receiptRequest = new HttpRequestMessage(HttpMethod.Get, Route);
        receiptRequest.Headers.Add("X-TerraScale-Deletion-Receipt", new string('A', 43));
        using var receiptResponse = await auth.Client.SendAsync(receiptRequest);
        await Assert.That(receiptResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var otherTenant = await SendAsync(auth.Client, HttpMethod.Get, "/v1/another/deletions");
        await Assert.That(otherTenant.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var absent = await SendAsync(auth.Client, HttpMethod.Post, AckRoute(proof), proof);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        var paths = (await auth.Client.GetFromJsonAsync<JsonElement>("/openapi/v1.json")).GetProperty("paths");
        await Assert.That(paths.GetProperty("/v1/{slug}/deletions").GetProperty("get").GetProperty("security")[0].TryGetProperty("clientBasic", out _)).IsTrue();
        await Assert.That(paths.GetProperty("/v1/{slug}/deletions/{id}/acknowledge").GetProperty("post").GetProperty("security")[0].TryGetProperty("clientBasic", out _)).IsTrue();
    }

    [Test]
    public async Task Listing_is_bounded_and_requires_an_exact_verified_platform_job_and_an_absent_root()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var auth = await OidcHarness.StartAsync(fixture, clock: clock);
        var store = auth.Server.App.Services.GetRequiredService<AuthStore>();
        await auth.Server.App.Services.GetRequiredService<DeletionCleanup>().StopAsync(CancellationToken.None);
        var live = await auth.CreateUserAsync("live@example.test");
        var jobs = new[]
        {
            NewJob(clock, "account", live.Id), NewJob(clock, "account") with { AuthLogicalVerifiedAt = null },
            NewJob(clock, "project"),
            NewJob(clock, "account") with { Id = AuthStore.NewId() },
        };
        foreach (var invalid in jobs)
        {
            await SaveAsync(store, invalid);
            var proof = Proof(invalid);
            using var refused = await SendAsync(auth.Client, HttpMethod.Post, AckRoute(proof), proof);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        var tenant = NewJob(clock, "account");
        tenant = tenant with { InstanceId = "another", Id = DeletionJob.Key("another", tenant.Scope, tenant.SubjectId) };
        await SaveAsync(store, tenant);
        using var tenantAck = await SendAsync(auth.Client, HttpMethod.Post, AckRoute(Proof(tenant)), Proof(tenant));
        await Assert.That(tenantAck.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await ListAsync(auth.Client)).Count).IsEqualTo(0);
        // Tenant-owned jobs retain their existing retirement behavior without dashboard acknowledgement.
        var tenantDocument = (await store.GetAsync(AuthStore.DeletionJobCollection, "another", tenant.Id))!;
        await Assert.That(await store.RetireDeletionFenceAsync(tenant, tenantDocument.Version)).IsTrue();
        var organization = NewJob(clock, "organization");
        var project = NewJob(clock, "project") with { OrganizationId = AuthStore.NewId() };
        await SaveAsync(store, organization);
        await SaveAsync(store, project);
        var scoped = await ListAsync(auth.Client);
        await Assert.That(scoped.Single(item => item.Scope == "organization").OrganizationId).IsEqualTo(organization.SubjectId);
        await Assert.That(scoped.Single(item => item.Scope == "project").OrganizationId).IsEqualTo(project.OrganizationId);
        foreach (var proof in scoped)
        {
            using var accepted = await SendAsync(auth.Client, HttpMethod.Post, AckRoute(proof), proof);
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        for (var index = 0; index < 21; index++)
        {
            await SaveAsync(store, NewJob(clock, "account"));
        }

        // Invalid rows may reduce a batch, but no response or storage query is unbounded.
        var batch = await ListAsync(auth.Client);
        await Assert.That(batch.Count).IsLessThanOrEqualTo(20);
        await Assert.That(batch.Count).IsGreaterThan(0);
        await Assert.That(batch.All(item => item.Scope == "account" && item.SubjectId != live.Id && item.OrganizationId is null)).IsTrue();
        await Assert.That(await auth.Server.App.Services.GetRequiredService<Users>().GetAsync("terrascale", live.Id)).IsNotNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Forty_unacknowledged_or_rejected_candidates_do_not_starve_later_proofs_after_restart(bool rejected)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var auth = await OidcHarness.StartAsync(fixture, clock: clock);
        await auth.Server.App.Services.GetRequiredService<DeletionCleanup>().StopAsync(CancellationToken.None);
        var database = auth.Server.App.Services.GetRequiredService<TerraBaseDatabase>();
        var jobs = Enumerable.Range(0, 45).Select(_ => NewJob(clock, "account") with { NextAttemptAt = clock.UtcNow.AddDays(1) })
            .OrderBy(job => job.FenceId, StringComparer.Ordinal).ToArray();
        var stored = new List<TerraBaseDocument>();
        for (var index = 0; index < jobs.Length; index++)
        {
            var value = JsonSerializer.SerializeToNode(jobs[index], DeletionJson.Default.DeletionJob)!.AsObject();
            if (rejected && index < 40)
            {
                // Include incorrect document bindings and malformed typed fields; delivery must never rewrite them.
                switch (index % 4)
                {
                    case 0:
                        value["id"] = AuthStore.NewId();
                        break;
                    case 1:
                        value["erasureId"] = "invalid";
                        break;
                    case 2:
                        value["requestedAt"] = "invalid";
                        break;
                    default:
                        value["requestedAt"] = JsonValue.Create(DateTimeOffset.MaxValue);
                        break;
                }
            }

            await database.PutDocumentAsync(AuthStore.DeletionJobCollection, jobs[index].FenceId,
                JsonSerializer.SerializeToElement(value, DashboardDeletionJson.Default.JsonObject), TerraBasePrecondition.Absent);
            stored.Add((await database.GetDocumentAsync(AuthStore.DeletionJobCollection, jobs[index].FenceId))!);
        }

        var first = await ListAsync(auth.Client);
        await Assert.That(first.Count).IsEqualTo(rejected ? 0 : 20);
        if (!rejected)
        {
            await Assert.That(first.Select(item => item.Id).SequenceEqual(jobs.Take(20).Select(job => job.ErasureId))).IsTrue();
        }

        // Dashboard's Stripe cleanup can fail for all first forty jobs; leave them unacknowledged.
        // Persisted traversal must survive an auth restart with a frozen clock.
        await auth.Server.App.StopAsync();
        await using var restarted = await auth.StartReplicaAsync(clock: clock);
        await restarted.App.Services.GetRequiredService<DeletionCleanup>().StopAsync(CancellationToken.None);
        var second = await ListAsync(restarted.Client);
        await Assert.That(second.Count).IsEqualTo(rejected ? 0 : 20);
        await Assert.That(second.Any(item => first.Contains(item))).IsFalse();
        var third = await ListAsync(restarted.Client);
        await Assert.That(third.Count).IsEqualTo(5);
        var later = jobs.Skip(40).Select(Proof).ToArray();
        await Assert.That(later.All(third.Contains)).IsTrue();
        var restartedDatabase = restarted.App.Services.GetRequiredService<TerraBaseDatabase>();
        var cursor = (await restartedDatabase.ListDocumentsAsync(AuthStore.DashboardDeletionCursorCollection)).Items.Single();
        await Assert.That(cursor.Document.GetProperty("nextCursor").ValueKind).IsEqualTo(JsonValueKind.Null);
        var wrapped = await ListAsync(restarted.Client);
        await Assert.That(wrapped.Count).IsEqualTo(rejected ? 0 : 20);
        if (!rejected)
        {
            await Assert.That(wrapped.SequenceEqual(first)).IsTrue();
        }

        for (var index = 0; index < jobs.Length; index++)
        {
            var document = (await restartedDatabase.GetDocumentAsync(AuthStore.DeletionJobCollection, jobs[index].FenceId))!;
            await Assert.That(document.Version).IsEqualTo(stored[index].Version);
            await Assert.That(document.Document.GetRawText()).IsEqualTo(stored[index].Document.GetRawText());
        }

        foreach (var proof in later)
        {
            using var accepted = await SendAsync(restarted.Client, HttpMethod.Post, AckRoute(proof), proof);
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        var store = restarted.App.Services.GetRequiredService<AuthStore>();
        for (var index = 0; index < 40; index++)
        {
            var document = (await restartedDatabase.GetDocumentAsync(AuthStore.DeletionJobCollection, jobs[index].FenceId))!;
            await Assert.That(document.Document.GetProperty("dashboardVerifiedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
            if (rejected)
            {
                using var refused = await SendAsync(restarted.Client, HttpMethod.Post, AckRoute(Proof(jobs[index])), Proof(jobs[index]));
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
        }

        foreach (var proof in later)
        {
            await Assert.That((await JobAsync(store, proof)).DashboardVerifiedAt).IsEqualTo(clock.UtcNow);
        }
    }

    [Test]
    public async Task A_malformed_candidate_at_the_4096_value_limit_cannot_block_the_next_page()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var auth = await OidcHarness.StartAsync(fixture, clock: clock);
        await auth.Server.App.Services.GetRequiredService<DeletionCleanup>().StopAsync(CancellationToken.None);
        var database = auth.Server.App.Services.GetRequiredService<TerraBaseDatabase>();
        var jobs = Enumerable.Range(0, 21).Select(_ => NewJob(clock, "account") with { NextAttemptAt = clock.UtcNow.AddDays(1) })
            .OrderBy(job => job.FenceId, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < jobs.Length; index++)
        {
            var value = JsonSerializer.SerializeToNode(jobs[index], DeletionJson.Default.DeletionJob)!.AsObject();
            if (index < 20)
            {
                value["id"] = AuthStore.NewId();
            }

            if (index == 0)
            {
                // All members are scalars: the root plus 4095 members exactly fills TerraBase's value budget.
                while (value.Count < 4095)
                {
                    value["padding" + value.Count] = 0;
                }
            }

            await database.PutDocumentAsync(AuthStore.DeletionJobCollection, jobs[index].FenceId,
                JsonSerializer.SerializeToElement(value, DashboardDeletionJson.Default.JsonObject), TerraBasePrecondition.Absent);
            if (index == 0)
            {
                var maximum = (await database.GetDocumentAsync(AuthStore.DeletionJobCollection, jobs[index].FenceId))!;
                value["anotherValue"] = 0;
                await Assert.ThrowsAsync<TerraBaseValidationException>(() => database.PutDocumentAsync(AuthStore.DeletionJobCollection, jobs[index].FenceId,
                    JsonSerializer.SerializeToElement(value, DashboardDeletionJson.Default.JsonObject), TerraBasePrecondition.Matches(maximum.Version)));
            }
        }

        var before = (await database.GetDocumentAsync(AuthStore.DeletionJobCollection, jobs[0].FenceId))!;
        await Assert.That((await ListAsync(auth.Client)).Count).IsEqualTo(0);
        await Assert.That((await ListAsync(auth.Client)).Single()).IsEqualTo(Proof(jobs[^1]));
        await Assert.That((await ListAsync(auth.Client)).Count).IsEqualTo(0);
        var unchanged = (await database.GetDocumentAsync(AuthStore.DeletionJobCollection, jobs[0].FenceId))!;
        await Assert.That(unchanged.Version).IsEqualTo(before.Version);
        await Assert.That(unchanged.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
    }

    private static DeletionJob NewJob(StoreTestClock clock, string scope, string? subject = null)
    {
        subject ??= AuthStore.NewId();
        var now = clock.UtcNow;
        return new(DeletionJob.Key("terrascale", scope, subject), "terrascale", scope, subject, null, now, now.AddDays(30), now,
            AuthLogicalVerifiedAt: now, FenceExpiresAt: now + DeletionJob.CredentialBound, ErasureId: AuthStore.NewId());
    }

    private static DashboardDeletionProof Proof(DeletionJob job) => new(job.ErasureId!, job.Scope, job.SubjectId, job.Scope == "organization" ? job.SubjectId : job.OrganizationId);

    private static async Task SaveAsync(AuthStore store, DeletionJob job) =>
        _ = await store.CreateAsync(AuthStore.DeletionJobCollection, job.InstanceId, job.Id, JsonSerializer.SerializeToElement(job, DeletionJson.Default.DeletionJob));

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
