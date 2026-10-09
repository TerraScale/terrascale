using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Bogus;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Cluster;
using TerraBase.Features.CrossRegion;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.CrossRegion;

public sealed class CrossRegionDiscoveryBudgetTests
{
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";
    private const string GruLabel = "region=\"gru-1\"";
    private static readonly string[] InSfo = ["sfo-1"];
    private static readonly string[] Members = ["gru-a", "gru-b", "gru-c"];

    [Test]
    [Arguments("members")]
    [Arguments("shared-page")]
    [Arguments("member-page")]
    [Arguments("refill")]
    [Arguments("rotation")]
    [Arguments("transitive")]
    [Arguments("shared-page-live")]
    [Arguments("deferred-hidden")]
    [Arguments("deferred-overflow")]
    [Arguments("scattered-pages")]
    [Arguments("scatter-invalid-before")]
    [Arguments("scatter-malformed-after")]
    [Arguments("page-overflow")]
    public async Task Discovery_refills_without_a_batch_barrier_and_expiry_preserves_heads_and_existing_replicas(string discovery)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var observation = discovery is "scattered-pages" or "page-overflow" or "transitive"
            ? SuiteDiagnostics.ObserveStorage("wan.discovery." + discovery) : null;
        ManagementApi? shared = null;
        var sharedCleanup = Task.CompletedTask;
        ManagementApi? direct = null;
        var directCleanup = Task.CompletedTask;
        ManagementApi? c = null;
        var cCleanup = Task.CompletedTask;
        ManagementApi? sfo = null;
        var sfoCleanup = Task.CompletedTask;
        Exception? bodyFailure = null;
        var overflow = discovery == "deferred-overflow";
        var rejected = discovery.StartsWith("scatter-", StringComparison.Ordinal) || discovery == "page-overflow";
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new DiscoveryClock(DateTimeOffset.UtcNow);
        var bus = new DiscoveryBus(network.Join("sfo", "sfo-a"), discovery);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-budget-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            // The singleton cross-link selects the first real responder; direct-page discovery starts at B.
            var sharedMember = discovery == "member-page" ? "gru-b" : "gru-a";
            var directMember = discovery == "member-page" ? "gru-a" : "gru-b";
            observation?.Phase("shared.host.start");
            shared = await StartAsync(network, credentials, "gru-1", "gru", sharedMember, failedStartCleanup: task => sharedCleanup = task);
            observation?.ObserveServer("shared", shared.Server);
            observation?.Phase("direct.host.start");
            direct = await StartAsync(network, credentials, "gru-1", "gru", directMember, failedStartCleanup: task => directCleanup = task);
            observation?.ObserveServer("direct", direct.Server);
            var a = discovery == "member-page" ? direct : shared;
            observation?.Phase("c.host.start");
            c = await StartAsync(network, credentials, "gru-1", "gru", "gru-c", failedStartCleanup: task => cCleanup = task);
            observation?.ObserveServer("c", c.Server);
            observation?.Phase("sfo.host.start");
            sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock, task => sfoCleanup = task);
            observation?.ObserveServer("sfo", sfo.Server);
            try
            {
                var organization = EntityIds.New().ToString();
                var project = EntityIds.New().ToString();
                var holderOrganization = EntityIds.New().ToString();
                var holderProject = EntityIds.New().ToString();
                var organizationName = faker.Company.CompanyName();
                var projectName = faker.Random.AlphaNumeric(12);
                // A cannot adopt C's database from SFO's replies: only C and SFO register its project.
                observation?.Phase("catalog.organization-project");
                foreach (var (api, registeredOrganization, registeredProject) in new[]
                {
                    (a, organization, project), (sfo, organization, project),
                    (c, holderOrganization, holderProject), (sfo, holderOrganization, holderProject),
                })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = EntityIds.New().ToString(),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = registeredOrganization, name = organizationName + (registeredOrganization == organization ? " source" : " holder") },
                            new { op = "create_project", organization = registeredOrganization, id = registeredProject, name = projectName },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                observation?.Phase("home.databases.create");
                var first = await CreatePlacedAsync(a, organization, project, "page-one", token);
                var second = await CreatePlacedAsync(a, organization, project, "page-two", token);
                // ULID text has the same ordering as the protocol's big-endian keys within this project.
                var healthy = string.CompareOrdinal(first.Id, second.Id) < 0 ? first : second;
                var retained = await CreatePlacedAsync(c, holderOrganization, holderProject, "retained", token);
                observation?.Phase("client.keys");
                using var source = await a.CreateClientAsync(organization, "documents:read", "documents:write");
                using var holder = await c.CreateClientAsync(holderOrganization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                using var retainedTarget = await sfo.CreateClientAsync(holderOrganization, "documents:read", "documents:write");
                observation?.Phase("source.documents.seed");
                foreach (var database in new[] { first, second })
                {
                    await ManagementApi.CreateCollectionAsync(source, database, "items");
                }

                await ManagementApi.CreateCollectionAsync(holder, retained, "items");
                var document = ManagementApi.Uri(ManagementApi.DocumentPath(healthy, "items", "value"));
                var retainedDocument = ManagementApi.Uri(ManagementApi.DocumentPath(retained, "items", "value"));
                var initial = faker.Lorem.Sentence();
                var retainedValue = faker.Lorem.Sentence();
                using var written = await source.PutAsJsonAsync(document, new { value = initial }, token);
                await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                using var retainedWrite = await holder.PutAsJsonAsync(retainedDocument, new { value = retainedValue }, token);
                await Assert.That(retainedWrite.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var unlisted = healthy.Id == first.Id ? second : first;
                var unlistedDocument = ManagementApi.Uri(ManagementApi.DocumentPath(unlisted, "items", "value"));
                var unlistedValue = faker.Lorem.Sentence();
                string? unlistedVersion = null;
                if (discovery is "scattered-pages" or "page-overflow")
                {
                    using var unlistedWrite = await source.PutAsJsonAsync(unlistedDocument, new { value = unlistedValue }, token);
                    await Assert.That(unlistedWrite.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    unlistedVersion = unlistedWrite.Headers.ETag!.Tag;
                }

                observation?.Phase("wan.cold-seed");
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                observation?.Phase("wan.round.verify");
                await AssertReadAsync(target, document, initial, written.Headers.ETag!.Tag, token);
                var retainedVersion = retainedWrite.Headers.ETag!.Tag;
                await AssertReadAsync(retainedTarget, retainedDocument, retainedValue, retainedVersion, token);
                if (discovery is "scattered-pages" or "page-overflow")
                {
                    await AssertReadAsync(target, unlistedDocument, unlistedValue, unlistedVersion!, token);
                }

                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(3);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                var file = Path.Combine(sfo.Server.DataDirectory, holderOrganization, holderProject, retained.Id + ".db");
                await Assert.That(File.Exists(file)).IsTrue();

                var update = initial + " " + faker.Lorem.Sentence();
                using var updated = await source.PutAsJsonAsync(document, new { value = update }, token);
                await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var previousRetainedValue = retainedValue;
                var previousRetainedVersion = retainedVersion;
                if (discovery is "refill" or "shared-page-live" || rejected)
                {
                    retainedValue += " " + faker.Lorem.Sentence();
                    using var holderUpdated = await holder.PutAsJsonAsync(retainedDocument, new { value = retainedValue }, token);
                    await Assert.That(holderUpdated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    retainedVersion = holderUpdated.Headers.ETag!.Tag;
                }

                bus.Faulted = true;
                if (rejected)
                {
                    var nextUnlistedValue = unlistedValue + " " + faker.Lorem.Sentence();
                    string? nextUnlistedVersion = null;
                    if (discovery == "page-overflow")
                    {
                        using var unlistedUpdated = await source.PutAsJsonAsync(unlistedDocument, new { value = nextUnlistedValue }, token);
                        await Assert.That(unlistedUpdated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        nextUnlistedVersion = unlistedUpdated.Headers.ETag!.Tag;
                    }

                    for (var round = 0; round < 2; round++)
                    {
                        bus.NextRound();
                        clock.Advance(WanPuller.Interval);
                        observation?.Phase("wan.round.wait");
                        await clock.PausedAsync(token);
                        observation?.Phase("wan.round.verify");
                        await AssertReadAsync(target, document, update, updated.Headers.ETag!.Tag, token);
                        await AssertReadAsync(retainedTarget, retainedDocument, previousRetainedValue, previousRetainedVersion, token);
                        using var saved = await sfo.Client.GetAsync(ManagementApi.Uri(retained.Path), token);
                        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        await Assert.That(File.Exists(file)).IsTrue();
                        await Assert.That(bus.Pulls.Any(pull => pull.DatabaseId == WanProtocol.Id(retained.Id))).IsFalse();
                        await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                        await Assert.That(bus.MaxActiveHeads).IsLessThanOrEqualTo(8);
                        await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                        await Assert.That(bus.PullRequests > 0).IsEqualTo(round == 0);
                        if (round == 0)
                        {
                            await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                            await Assert.That(bus.Pulls.Single(pull => pull.DatabaseId == WanProtocol.Id(healthy.Id)).After)
                                .IsEqualTo(bus.InitialSequences[WanProtocol.Id(healthy.Id)]);
                        }

                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(round + 1);
                        if (discovery == "page-overflow")
                        {
                            var advertised = bus.OverflowAdvertisement!;
                            var page = bus.OverflowReplies[round];
                            await Assert.That(advertised.Members.Count).IsEqualTo(1024);
                            await Assert.That(advertised.Members.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1024);
                            await Assert.That(advertised.Members.All(WanProtocol.IsMember)).IsTrue();
                            await Assert.That(advertised.Heads.Single().DatabaseId).IsEqualTo(WanProtocol.Id(healthy.Id));
                            await Assert.That(advertised.More).IsTrue();
                            await Assert.That(page.Members.Count).IsEqualTo(2);
                            await Assert.That(page.Members.All(WanProtocol.IsMember)).IsTrue();
                            await Assert.That(page.Heads.Single().DatabaseId).IsEqualTo(WanProtocol.Id(unlisted.Id));
                            await Assert.That(page.More).IsFalse();
                            await Assert.That(advertised.Members.Concat(page.Members).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1025);
                            await Assert.That(advertised.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                            await Assert.That(page.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                            // A repeated round must not inherit the rejected page's 1025th member or pull its head.
                            await Assert.That(bus.HeadRequests).IsEqualTo(1024);
                            await Assert.That(bus.Pulls.Any(pull => pull.DatabaseId == WanProtocol.Id(unlisted.Id))).IsFalse();
                            await AssertReadAsync(target, unlistedDocument, unlistedValue, unlistedVersion!, token);
                            using var unlistedSaved = await sfo.Client.GetAsync(ManagementApi.Uri(unlisted.Path), token);
                            await Assert.That(unlistedSaved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                            await Assert.That(File.Exists(Path.Combine(sfo.Server.DataDirectory, organization, project, unlisted.Id + ".db"))).IsTrue();
                        }
                        else
                        {
                            // The valid reply is otherwise complete: only the rejected scatter disables cleanup.
                            await Assert.That(bus.ScatteredReplies).IsEqualTo(2);
                            await Assert.That(bus.HeadRequests).IsEqualTo(0);
                        }
                    }

                    clock.Advance(TimeSpan.FromMilliseconds(500));
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(2.5);
                    bus.NextRound();
                    bus.Faulted = false;
                    clock.Advance(TimeSpan.FromMilliseconds(500));
                    observation?.Phase("wan.round.wait");
                    await clock.PausedAsync(token);
                    observation?.Phase("wan.round.verify");
                    await AssertReadAsync(retainedTarget, retainedDocument, retainedValue, retainedVersion, token);
                    await Assert.That(bus.Pulls.Single(pull => pull.DatabaseId == WanProtocol.Id(retained.Id)).After)
                        .IsEqualTo(bus.InitialSequences[WanProtocol.Id(retained.Id)]);
                    if (discovery == "page-overflow")
                    {
                        await AssertReadAsync(target, unlistedDocument, nextUnlistedValue, nextUnlistedVersion!, token);
                        await Assert.That(bus.Pulls.Single(pull => pull.DatabaseId == WanProtocol.Id(unlisted.Id)).After)
                            .IsEqualTo(bus.InitialSequences[WanProtocol.Id(unlisted.Id)]);
                    }

                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(3);
                    // The page case still knows its accepted silent members; healing cannot declare them caught up.
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(discovery == "page-overflow" ? 3 : 0);
                    return;
                }

                clock.Advance(WanPuller.Interval);
                if (discovery == "scattered-pages")
                {
                    observation?.Phase("wan.wait.ScatteredEnumerated");
                    await bus.ScatteredEnumerated.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    observation?.Phase("wan.round.verify");
                    await Assert.That(bus.ScatteredReplies).IsEqualTo(11);
                    await Assert.That(bus.HeadsAtScatterCompletion).IsEqualTo(0);
                    await Assert.That(bus.HolderQueried.Task.IsCompleted).IsFalse();
                }

                observation?.Phase("wan.wait.Pending");
                await bus.Pending.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                observation?.Phase("wan.round.verify");

                if (discovery == "refill")
                {
                    // B reveals C while all seven other requests remain held, without advancing the deadline.
                    observation?.Phase("wan.wait.HolderQueried");
                    await bus.HolderQueried.Task.WaitAsync(token);
                    observation?.Phase("wan.round.verify");
                    await Assert.That(bus.ActiveHeads).IsEqualTo(8);
                    await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                    await Assert.That(clock.GetUtcNow() - clock.Started).IsEqualTo(WanPuller.Interval);
                    bus.ReleaseHolder.TrySetResult();
                    bus.ReleaseSlow.TrySetResult();
                }
                else if (overflow)
                {
                    await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                    clock.Advance(TimeSpan.FromSeconds(10));
                    observation?.Phase("wan.wait.Cancelled");
                    await bus.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    observation?.Phase("wan.round.verify");
                    var advertised = bus.OverflowAdvertisement!;
                    await Assert.That(advertised.Member).IsEqualTo("gru-a");
                    await Assert.That(advertised.Members.Count).IsEqualTo(1024);
                    await Assert.That(advertised.Members.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1024);
                    await Assert.That(advertised.Members.All(WanProtocol.IsMember)).IsTrue();
                    await Assert.That(advertised.More).IsFalse();
                    await Assert.That(advertised.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                    await Assert.That(advertised.Members.Take(3).SequenceEqual(["gru-a", "gru-d", "gru-e"])).IsTrue();
                    await Assert.That(advertised.Members[^1]).IsEqualTo("gru-c");
                    await Assert.That(bus.OverflowReplies.Select(reply => reply.Member).SequenceEqual(["gru-d", "gru-e"])).IsTrue();
                    foreach (var reply in bus.OverflowReplies)
                    {
                        await Assert.That(reply.Members.Count).IsEqualTo(2);
                        await Assert.That(reply.Members.All(WanProtocol.IsMember)).IsTrue();
                        await Assert.That(reply.More).IsFalse();
                        await Assert.That(reply.Heads.Count).IsGreaterThan(0);
                        await Assert.That(reply.Heads.Count).IsLessThanOrEqualTo(WanProtocol.MaxHeadsPerReply);
                        await Assert.That(reply.Heads.SequenceEqual(advertised.Heads)).IsTrue();
                        await Assert.That(reply.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                        await Assert.That(advertised.Members.Concat(reply.Members).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1025);
                    }

                    // D and E's useful heads survive membership overflow; silent requests still hold the final join.
                    await Assert.That(bus.OverflowRepliesAtCancellation).IsEqualTo(2);
                    await Assert.That(bus.HeadRequests).IsEqualTo(10);
                    await Assert.That(bus.ActiveHeads).IsEqualTo(8);
                    await Assert.That(bus.HolderQueried.Task.IsCompleted).IsFalse();
                    await Assert.That(bus.PullRequests).IsEqualTo(0);
                    await Assert.That(clock.GetUtcNow() - clock.Started).IsEqualTo(TimeSpan.FromSeconds(11));
                    bus.ReleaseCancelled.TrySetResult();
                }
                else
                {
                    if (discovery == "shared-page-live")
                    {
                        // C answers while A's continuation still occupies a slot and its discovery budget.
                        observation?.Phase("wan.wait.HolderQueried");
                        await bus.HolderQueried.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                        observation?.Phase("wan.round.verify");
                        await Assert.That(bus.ActiveHeads).IsEqualTo(2);
                        await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                        await Assert.That(bus.HeadRequests).IsEqualTo(2);
                        await Assert.That(bus.PullRequests).IsEqualTo(0);
                        await Assert.That(clock.GetUtcNow() - clock.Started).IsEqualTo(WanPuller.Interval);
                        await AssertReadAsync(retainedTarget, retainedDocument, previousRetainedValue, previousRetainedVersion, token);
                        bus.ReleaseHolder.TrySetResult();
                        observation?.Phase("wan.wait.HolderReplied");
                        await bus.HolderReplied.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                        observation?.Phase("wan.round.verify");
                    }

                    await Assert.That(bus.ActiveHeads).IsEqualTo(bus.ExpectedPending);
                    clock.Advance(TimeSpan.FromSeconds(9));
                    await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                    clock.Advance(TimeSpan.FromSeconds(1));
                    observation?.Phase("wan.wait.Cancelled");
                    await bus.Cancelled.Task.WaitAsync(token);
                    observation?.Phase("wan.round.verify");
                    // Cancelled tasks deliberately have not returned yet: pulling must join them first.
                    await Assert.That(bus.ActiveHeads).IsEqualTo(bus.ExpectedPending);
                    await Assert.That(bus.PullRequests).IsEqualTo(0);
                    bus.ReleaseCancelled.TrySetResult();
                }

                observation?.Phase("wan.round.wait");
                await clock.PausedAsync(token);
                observation?.Phase("wan.round.verify");
                await AssertReadAsync(target, document, update, updated.Headers.ETag!.Tag, token);
                await AssertReadAsync(retainedTarget, retainedDocument, retainedValue, retainedVersion, token);
                using var preserved = await sfo.Client.GetAsync(ManagementApi.Uri(retained.Path), token);
                await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(File.Exists(file)).IsTrue();
                if (discovery is "shared-page-live" or "scattered-pages")
                {
                    // A's unreceived second page cannot authorize forgetting its other saved database.
                    using var unlistedPreserved = await sfo.Client.GetAsync(ManagementApi.Uri(unlisted.Path), token);
                    await Assert.That(unlistedPreserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(File.Exists(Path.Combine(sfo.Server.DataDirectory, organization, project, unlisted.Id + ".db"))).IsTrue();
                    await Assert.That(bus.MaxActiveHeads).IsEqualTo(discovery == "scattered-pages" ? 8 : 2);
                    if (discovery == "scattered-pages")
                    {
                        await Assert.That(bus.SyntheticFirstRequests).IsEqualTo(0);
                        await AssertReadAsync(target, unlistedDocument, unlistedValue, unlistedVersion!, token);
                    }
                }

                await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                await Assert.That(bus.MaxActiveHeads).IsLessThanOrEqualTo(8);
                await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                await Assert.That(bus.PullRequests).IsGreaterThan(0);

                var expectedMembers = discovery is "shared-page" or "scattered-pages" ? 0 : discovery is "refill" or "deferred-overflow" ? 3 : 1;
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(expectedMembers);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                var stale = await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token);
                await Assert.That(stale).IsEqualTo(discovery == "refill" ? 1 : 11);
                // Stay inside the next interval while proving that this incomplete round did not reset staleness.
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(stale + 0.5);
                if (discovery != "refill")
                {
                    await Assert.That(bus.CancelledHeads).IsEqualTo(bus.ExpectedPending);
                    await Assert.That(bus.HeadRequests).IsEqualTo(overflow ? 10 : discovery is "members" or "rotation" or "transitive" or "deferred-hidden" or "scattered-pages" ? 8 : discovery == "member-page" ? 3 : discovery is "shared-page" or "shared-page-live" ? 2 : 1);
                }

                if (discovery is "deferred-hidden" or "deferred-overflow")
                {
                    // C really was deferred; subsequent advertisements omit every unresolved member.
                    await Assert.That(bus.HolderQueried.Task.IsCompleted).IsFalse();
                    await Assert.That(bus.MaxActiveHeads).IsEqualTo(8);
                    // Finished D and E enter carryover after the first hidden round, then are queried again.
                    var hiddenRequests = overflow ? 1023 : 9;
                    var beforeHolderUpdate = retainedValue;
                    retainedValue += " " + faker.Lorem.Sentence();
                    using var holderUpdated = await holder.PutAsJsonAsync(retainedDocument, new { value = retainedValue }, token);
                    await Assert.That(holderUpdated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(holderUpdated.Headers.ETag!.Tag).IsNotEqualTo(retainedVersion);
                    for (var round = 0; round < 3; round++)
                    {
                        bus.NextRound();
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        observation?.Phase("wan.round.wait");
                        await clock.PausedAsync(token);
                        observation?.Phase("wan.round.verify");
                        await Assert.That(bus.HolderQueried.Task.IsCompleted).IsTrue();
                        await Assert.That(bus.HiddenAdvertisements).IsEqualTo(1);
                        await Assert.That(bus.HeadRequests).IsEqualTo(overflow && round == 0 ? 1021 : hiddenRequests);
                        await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                        await Assert.That(bus.MaxActiveHeads).IsLessThanOrEqualTo(8);
                        await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                        await Assert.That(bus.PullRequests).IsEqualTo(0);
                        await AssertReadAsync(target, document, update, updated.Headers.ETag!.Tag, token);

                        await AssertReadAsync(retainedTarget, retainedDocument, beforeHolderUpdate, retainedVersion, token);
                        using var hiddenPreserved = await sfo.Client.GetAsync(ManagementApi.Uri(retained.Path), token);
                        await Assert.That(hiddenPreserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        await Assert.That(File.Exists(file)).IsTrue();
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(stale + 1 + round);
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(stale + 1.5 + round);
                    }

                    bus.NextRound();
                    bus.HealHolder = true;
                    bus.ReleaseHolder.TrySetResult();
                    clock.Advance(TimeSpan.FromMilliseconds(500));
                    observation?.Phase("wan.round.wait");
                    await clock.PausedAsync(token);
                    observation?.Phase("wan.round.verify");
                    await Assert.That(bus.HolderQueried.Task.IsCompleted).IsTrue();
                    await Assert.That(bus.HiddenAdvertisements).IsEqualTo(2);
                    await Assert.That(bus.HeadRequests).IsEqualTo(hiddenRequests);
                    await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                    await Assert.That(bus.MaxActiveHeads).IsLessThanOrEqualTo(8);
                    await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                    await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                    await Assert.That(bus.PullRequests).IsGreaterThan(0);
                    await AssertReadAsync(retainedTarget, retainedDocument, retainedValue, holderUpdated.Headers.ETag!.Tag, token);
                    using var healedPreserved = await sfo.Client.GetAsync(ManagementApi.Uri(retained.Path), token);
                    await Assert.That(healedPreserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(File.Exists(file)).IsTrue();
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(2);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(stale + 4);
                }

                if (discovery == "scattered-pages")
                {
                    for (var round = 2; round <= 4; round++)
                    {
                        var queriesHolder = round % 2 == 0;
                        var beforeHolderUpdate = retainedValue;
                        var beforeHolderVersion = retainedVersion;
                        if (queriesHolder)
                        {
                            retainedValue += " " + faker.Lorem.Sentence();
                            using var holderUpdated = await holder.PutAsJsonAsync(retainedDocument, new { value = retainedValue }, token);
                            await Assert.That(holderUpdated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                            retainedVersion = holderUpdated.Headers.ETag!.Tag;
                            await Assert.That(retainedVersion).IsNotEqualTo(beforeHolderVersion);
                        }

                        bus.NextRound();
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        observation?.Phase("wan.wait.ScatteredEnumerated");
                        await bus.ScatteredEnumerated.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                        observation?.Phase("wan.round.verify");
                        await Assert.That(bus.ScatteredReplies).IsEqualTo(11);
                        await Assert.That(bus.HeadsAtScatterCompletion).IsEqualTo(0);
                        if (queriesHolder)
                        {
                            observation?.Phase("wan.wait.HolderQueried");
                            await bus.HolderQueried.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                            observation?.Phase("wan.round.verify");
                            await Assert.That(bus.ActiveHeads).IsEqualTo(8);
                            await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                            await Assert.That(bus.PullRequests).IsEqualTo(0);
                            await AssertReadAsync(retainedTarget, retainedDocument, beforeHolderUpdate, beforeHolderVersion, token);
                            bus.ReleaseHolder.TrySetResult();
                        }

                        observation?.Phase("wan.wait.Pending");
                        await bus.Pending.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                        observation?.Phase("wan.round.verify");
                        await Assert.That(bus.HolderQueried.Task.IsCompleted).IsEqualTo(queriesHolder);
                        await Assert.That(bus.ActiveHeads).IsEqualTo(8);
                        await Assert.That(bus.HeadRequests).IsEqualTo(queriesHolder ? 9 : 8);
                        await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                        await Assert.That(clock.GetUtcNow() - clock.Started).IsEqualTo(TimeSpan.FromSeconds(11 * (round - 1) + 1));
                        clock.Advance(TimeSpan.FromSeconds(10));
                        observation?.Phase("wan.wait.Cancelled");
                        await bus.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                        observation?.Phase("wan.round.verify");
                        await Assert.That(bus.ActiveHeads).IsEqualTo(8);
                        await Assert.That(bus.PullRequests).IsEqualTo(0);
                        bus.ReleaseCancelled.TrySetResult();
                        observation?.Phase("wan.round.wait");
                        await clock.PausedAsync(token);
                        observation?.Phase("wan.round.verify");
                        await AssertReadAsync(target, document, update, updated.Headers.ETag!.Tag, token);
                        await AssertReadAsync(target, unlistedDocument, unlistedValue, unlistedVersion!, token);
                        await AssertReadAsync(retainedTarget, retainedDocument, retainedValue, retainedVersion, token);
                        using var scatteredPreserved = await sfo.Client.GetAsync(ManagementApi.Uri(retained.Path), token);
                        await Assert.That(scatteredPreserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        await Assert.That(File.Exists(file)).IsTrue();
                        using var partialPreserved = await sfo.Client.GetAsync(ManagementApi.Uri(unlisted.Path), token);
                        await Assert.That(partialPreserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        await Assert.That(File.Exists(Path.Combine(sfo.Server.DataDirectory, organization, project, unlisted.Id + ".db"))).IsTrue();
                        await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                        await Assert.That(bus.MaxActiveHeads).IsEqualTo(8);
                        await Assert.That(bus.CancelledHeads).IsEqualTo(8);
                        await Assert.That(bus.SyntheticFirstRequests).IsEqualTo(0);
                        await Assert.That(bus.HeadRequests).IsEqualTo(queriesHolder ? 9 : 8);
                        await Assert.That(bus.PullRequests > 0).IsEqualTo(queriesHolder);
                        if (queriesHolder)
                        {
                            await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                        }

                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(queriesHolder ? 1 : 0);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(11 * round);
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(11 * round + 0.5);
                    }
                }

                if (discovery is "rotation" or "transitive")
                {
                    var beforeHolderUpdate = retainedValue;
                    retainedValue += " " + faker.Lorem.Sentence();
                    using var holderUpdated = await holder.PutAsJsonAsync(retainedDocument, new { value = retainedValue }, token);
                    await Assert.That(holderUpdated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    if (discovery == "transitive")
                    {
                        // A lists fourteen silent members before B; only B's real reply reveals C.
                        await Assert.That(bus.BridgeReplied.Task.IsCompleted).IsFalse();
                        await Assert.That(bus.HolderQueried.Task.IsCompleted).IsFalse();
                        bus.NextRound();
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        observation?.Phase("wan.wait.BridgeReplied");
                        await bus.BridgeReplied.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                        observation?.Phase("wan.round.verify");
                        observation?.Phase("wan.wait.Pending");
                        await bus.Pending.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                        observation?.Phase("wan.round.verify");
                        await Assert.That(bus.HolderQueried.Task.IsCompleted).IsFalse();
                        await Assert.That(bus.ActiveHeads).IsEqualTo(8);
                        await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                        await Assert.That(bus.HeadRequests).IsEqualTo(9);
                        await Assert.That(bus.PullRequests).IsEqualTo(0);
                        await Assert.That(clock.GetUtcNow() - clock.Started).IsEqualTo(TimeSpan.FromSeconds(12));
                        await AssertReadAsync(retainedTarget, retainedDocument, beforeHolderUpdate, retainedVersion, token);
                        clock.Advance(TimeSpan.FromSeconds(10));
                        observation?.Phase("wan.wait.Cancelled");
                        await bus.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                        observation?.Phase("wan.round.verify");
                        await Assert.That(bus.ActiveHeads).IsEqualTo(8);
                        await Assert.That(bus.PullRequests).IsEqualTo(0);
                        bus.ReleaseCancelled.TrySetResult();
                        observation?.Phase("wan.round.wait");
                        await clock.PausedAsync(token);
                        observation?.Phase("wan.round.verify");
                        await AssertReadAsync(retainedTarget, retainedDocument, beforeHolderUpdate, retainedVersion, token);
                        using var bridgeRoundPreserved = await sfo.Client.GetAsync(ManagementApi.Uri(retained.Path), token);
                        await Assert.That(bridgeRoundPreserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        await Assert.That(File.Exists(file)).IsTrue();
                        await Assert.That(bus.HolderQueried.Task.IsCompleted).IsFalse();
                        await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                        await Assert.That(bus.MaxActiveHeads).IsEqualTo(8);
                        await Assert.That(bus.CancelledHeads).IsEqualTo(8);
                        await Assert.That(bus.HeadRequests).IsEqualTo(9);
                        await Assert.That(bus.PullRequests).IsEqualTo(0);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(2);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(22);
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(22.5);
                    }

                    // Deferred C must precede repeated silent listings in the next discovery budget.
                    bus.NextRound();
                    clock.Advance(TimeSpan.FromMilliseconds(500));
                    observation?.Phase("wan.wait.HolderQueried");
                    await bus.HolderQueried.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    observation?.Phase("wan.round.verify");
                    await Assert.That(bus.ActiveHeads).IsEqualTo(8);
                    await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                    await Assert.That(clock.GetUtcNow() - clock.Started).IsEqualTo(TimeSpan.FromSeconds(discovery == "transitive" ? 23 : 12));
                    await AssertReadAsync(retainedTarget, retainedDocument, beforeHolderUpdate, retainedVersion, token);
                    bus.ReleaseHolder.TrySetResult();
                    observation?.Phase("wan.wait.Pending");
                    await bus.Pending.Task.WaitAsync(token);
                    observation?.Phase("wan.round.verify");
                    clock.Advance(TimeSpan.FromSeconds(10));
                    observation?.Phase("wan.wait.Cancelled");
                    await bus.Cancelled.Task.WaitAsync(token);
                    observation?.Phase("wan.round.verify");
                    await Assert.That(bus.PullRequests).IsEqualTo(0);
                    bus.ReleaseCancelled.TrySetResult();
                    observation?.Phase("wan.round.wait");
                    await clock.PausedAsync(token);
                    observation?.Phase("wan.round.verify");
                    await AssertReadAsync(retainedTarget, retainedDocument, retainedValue, holderUpdated.Headers.ETag!.Tag, token);
                    using var stillRegistered = await sfo.Client.GetAsync(ManagementApi.Uri(retained.Path), token);
                    await Assert.That(stillRegistered.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(File.Exists(file)).IsTrue();
                    await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                    await Assert.That(bus.MaxActiveHeads).IsEqualTo(8);
                    await Assert.That(bus.CancelledHeads).IsEqualTo(8);
                    await Assert.That(bus.HeadRequests).IsEqualTo(9);
                    await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                    await Assert.That(bus.PullRequests).IsGreaterThan(0);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(2);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(discovery == "transitive" ? 33 : 22);
                    if (discovery == "transitive")
                    {
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(33.5);
                    }
                }
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseSlow.TrySetResult();
                bus.ReleaseCancelled.TrySetResult();
                bus.ReleaseHolder.TrySetResult();
            }
        }
        catch (Exception error)
        {
            bodyFailure = error;
            observation?.BodyFailed(error);
            throw;
        }
        finally
        {
            observation?.Phase("cleanup");
            await SuiteDiagnostics.CleanupAfterBodyAsync(bodyFailure, () => Directory.Delete(directory, recursive: true),
                (sfo, sfo?.CleanupCompletion ?? sfoCleanup), (c, c?.CleanupCompletion ?? cCleanup),
                (direct, direct?.CleanupCompletion ?? directCleanup), (shared, shared?.CleanupCompletion ?? sharedCleanup));
        }
    }

    private static Task<ManagementApi> StartAsync(
        FakeRegionNetwork network, string credentials, string region, string cell, string node, IRegionBus? bus = null, DiscoveryClock? clock = null, Action<Task>? failedStartCleanup = null) =>
        ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = node;
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            services.AddSingleton(bus ?? network.Join(cell, node));
            services.AddSingleton<IMemberStore>(new FakeMemberStore(TimeProvider.System));
            if (clock is not null)
            {
                // Keep every application clock real except the WAN puller's budget, interval, and staleness.
                var registration = services.Single(service => service.ImplementationType == typeof(WanPuller));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(
                    provider => ActivatorUtilities.CreateInstance<WanPuller>(provider, clock));
                services.AddSingleton(provider => ActivatorUtilities.CreateInstance<WanMetrics>(provider, clock));
            }
        }, managementCredential: true, failedStartCleanup: failedStartCleanup);

    private static async Task<ManagedDatabaseRef> CreatePlacedAsync(ManagementApi api, string organization, string project, string name, CancellationToken token)
    {
        using var created = await api.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"),
            new { name, replicaRegions = InSfo }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var record = await created.Content.ReadFromJsonAsync<JsonElement>(token);
        return new ManagedDatabaseRef(organization, project, record.GetProperty("id").GetString()!, name);
    }

    private static async Task AssertReadAsync(HttpClient client, Uri document, string value, string version, CancellationToken token)
    {
        using var response = await client.GetAsync(document, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.ETag?.Tag).IsEqualTo(version);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(body.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
    }

    private sealed class DiscoveryClock(DateTimeOffset started) : FakeTimeProvider(started)
    {
        private readonly Channel<bool> _pauses = Channel.CreateBounded<bool>(1);

        public DateTimeOffset Started { get; } = started;

        public async Task PausedAsync(CancellationToken token) => _ = await _pauses.Reader.ReadAsync(token);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == WanPuller.Interval && !_pauses.Writer.TryWrite(true))
            {
                throw new InvalidOperationException("a WAN round pause was not consumed");
            }

            return timer;
        }
    }

    // Only heads discovery is changed; all heads and pulled groups originate from the real application's files.
    private sealed class DiscoveryBus(IRegionBus inner, string discovery) : IRegionBus
    {
        private readonly string[] _unreachable = [.. Enumerable.Range(0, discovery == "members" ? 1000 : discovery == "deferred-overflow" ? 1020 : discovery == "page-overflow" ? 1021 : discovery == "refill" ? 7 : discovery is "rotation" or "deferred-hidden" ? 8 : discovery == "transitive" ? 14 : 0)
            .Select(index => "gru-unreachable-" + index.ToString(CultureInfo.InvariantCulture))];
        private int _activeHeads;
        private int _maxActiveHeads;
        private int _heldHeads;
        private int _cancelledHeads;
        private int _headRequests;
        private int _pullRequests;
        private int _activeAtFirstPull = -1;
        private int _round;
        private int _hiddenAdvertisements;
        private int _scatteredReplies;
        private int _headsAtScatterCompletion = -1;
        private int _syntheticFirstRequests;
        private int _overflowRepliesAtCancellation = -1;

        public HeadsReply? OverflowAdvertisement { get; private set; }

        public List<HeadsReply> OverflowReplies { get; } = [];

        public Dictionary<ByteString, ulong> InitialSequences { get; } = [];

        public List<PullRequest> Pulls { get; } = [];

        private bool InvalidScatter => discovery.StartsWith("scatter-", StringComparison.Ordinal);

        public int OverflowRepliesAtCancellation => Volatile.Read(ref _overflowRepliesAtCancellation);

        public TaskCompletionSource Begin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Pending { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HolderQueried { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HolderReplied { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BridgeReplied { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ScatteredEnumerated { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource SevenPending { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSlow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCancelled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHolder { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExpectedPending => discovery is "members" or "rotation" or "transitive" or "deferred-hidden" or "deferred-overflow" or "scattered-pages" ? 8 : discovery == "refill" ? 7 : discovery is "shared-page" or "member-page" ? 2 : 1;

        public int ActiveHeads => Volatile.Read(ref _activeHeads);

        public int MaxActiveHeads => Volatile.Read(ref _maxActiveHeads);

        public int CancelledHeads => Volatile.Read(ref _cancelledHeads);

        public int HeadRequests => Volatile.Read(ref _headRequests);

        public int PullRequests => Volatile.Read(ref _pullRequests);

        public int ActiveAtFirstPull => Volatile.Read(ref _activeAtFirstPull);

        public int HiddenAdvertisements => Volatile.Read(ref _hiddenAdvertisements);

        public int ScatteredReplies => Volatile.Read(ref _scatteredReplies);

        public int HeadsAtScatterCompletion => Volatile.Read(ref _headsAtScatterCompletion);

        public int SyntheticFirstRequests => Volatile.Read(ref _syntheticFirstRequests);

        public bool HealHolder
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool Faulted
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public void NextRound()
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HolderQueried = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HolderReplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
            BridgeReplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScatteredEnumerated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SevenPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseHolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _heldHeads = 0;
            _cancelledHeads = 0;
            _headRequests = 0;
            _pullRequests = 0;
            _maxActiveHeads = 0;
            _activeAtFirstPull = -1;
            _ = Interlocked.Increment(ref _round);
            _hiddenAdvertisements = 0;
            _scatteredReplies = 0;
            _headsAtScatterCompletion = -1;
            _syntheticFirstRequests = 0;
            Pulls.Clear();
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(
            string subject, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Begin.Task.WaitAsync(cancellationToken);
            if (Faulted && InvalidScatter && discovery.EndsWith("-before", StringComparison.Ordinal))
            {
                yield return InvalidReply();
                _ = Interlocked.Increment(ref _scatteredReplies);
            }

            await foreach (var data in inner.RequestManyAsync(subject, request, cancellationToken))
            {
                yield return Rewrite(data, WanRequest.Parser.ParseFrom(request.Span));
                if (Faulted && InvalidScatter)
                {
                    _ = Interlocked.Increment(ref _scatteredReplies);
                }

                if (Faulted && discovery == "scattered-pages")
                {
                    _ = Interlocked.Increment(ref _scatteredReplies);
                    // Each accepted partial page owns real A heads; C appears only beyond the first eight replies.
                    for (var index = 0; index < 10; index++)
                    {
                        var reply = WanProtocol.DecodeHeads(data).Clone();
                        reply.Member = "gru-scattered-" + index.ToString(CultureInfo.InvariantCulture);
                        reply.Members.Clear();
                        reply.Members.Add(reply.Member);
                        if (index == 9)
                        {
                            reply.Members.Add("gru-c");
                        }

                        while (reply.Heads.Count > 1)
                        {
                            reply.Heads.RemoveAt(reply.Heads.Count - 1);
                        }

                        reply.More = true;
                        yield return WanProtocol.EncodeHeads(reply);
                        _ = Interlocked.Increment(ref _scatteredReplies);
                    }
                }
            }

            if (Faulted && InvalidScatter && discovery.EndsWith("-after", StringComparison.Ordinal))
            {
                yield return InvalidReply();
                _ = Interlocked.Increment(ref _scatteredReplies);
            }

            if (Faulted && discovery == "scattered-pages")
            {
                Volatile.Write(ref _headsAtScatterCompletion, HeadRequests);
                ScatteredEnumerated.TrySetResult();
            }

            ReadOnlyMemory<byte> InvalidReply()
            {
                return discovery.Contains("-invalid-", StringComparison.Ordinal)
                    ? WanProtocol.EncodeHeads(new HeadsReply { Member = "invalid.member", Members = { "gru-scatter-leaked" } })
                    : ApplicationEnvelope.Encode((byte[])[0x80]);
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            var parsed = WanRequest.Parser.ParseFrom(request.Span);
            if (parsed.Pull is { } pull && (InvalidScatter || discovery == "page-overflow"))
            {
                Pulls.Add(pull);
            }

            if (!Faulted)
            {
                return Rewrite(await inner.RequestAsync(subject, request, cancellationToken), parsed);
            }

            if (parsed.Pull is not null)
            {
                if (Interlocked.Increment(ref _pullRequests) == 1)
                {
                    Volatile.Write(ref _activeAtFirstPull, ActiveHeads);
                }

                // Synthetic responders hold real A's first page; any pulls still use the real application's store.
                var source = subject.Contains(".gru-scattered-", StringComparison.Ordinal)
                    ? WanProtocol.MemberSubject("gru", "gru-a") : subject;
                return await inner.RequestAsync(source, request, cancellationToken);
            }

            _ = Interlocked.Increment(ref _headRequests);
            var active = Interlocked.Increment(ref _activeHeads);
            var maximum = MaxActiveHeads;
            while (active > maximum)
            {
                var previous = Interlocked.CompareExchange(ref _maxActiveHeads, active, maximum);
                if (previous == maximum)
                {
                    break;
                }

                maximum = previous;
            }

            try
            {
                if (discovery == "page-overflow" && (subject.Contains(".gru-unreachable-", StringComparison.Ordinal)
                    || subject == WanProtocol.MemberSubject("gru", "gru-c")))
                {
                    throw new IOException("the advertised member has no heads reply");
                }

                if (discovery == "deferred-overflow" && Volatile.Read(ref _round) == 0
                    && (subject == WanProtocol.MemberSubject("gru", "gru-d") || subject == WanProtocol.MemberSubject("gru", "gru-e")))
                {
                    // No await: both valid replies finish while the caller fills its first eight pending tasks.
                    var reply = OverflowAdvertisement!.Clone();
                    reply.Member = subject == WanProtocol.MemberSubject("gru", "gru-d") ? "gru-d" : "gru-e";
                    reply.Members.Clear();
                    reply.Members.Add(reply.Member);
                    reply.Members.Add(reply.Member + "-overflow");
                    OverflowReplies.Add(reply);
                    return WanProtocol.EncodeHeads(reply);
                }

                if ((discovery is "deferred-hidden" or "deferred-overflow") && Volatile.Read(ref _round) > 0)
                {
                    if (subject.Contains(".gru-unreachable-", StringComparison.Ordinal))
                    {
                        throw new IOException("the unadvertised member has no heads reply");
                    }

                    if (subject == WanProtocol.MemberSubject("gru", "gru-c") && !HealHolder)
                    {
                        HolderQueried.TrySetResult();
                        throw new IOException("the unadvertised holder has no heads reply");
                    }
                }

                if (discovery == "scattered-pages" && subject.Contains(".gru-scattered-", StringComparison.Ordinal) && parsed.Heads.After.Length == 0)
                {
                    _ = Interlocked.Increment(ref _syntheticFirstRequests);
                }

                if (subject.Contains(".gru-unreachable-", StringComparison.Ordinal)
                    || subject.Contains(".gru-scattered-", StringComparison.Ordinal)
                    || (subject == WanProtocol.MemberSubject("gru", "gru-c") && discovery is not ("refill" or "rotation" or "transitive" or "shared-page-live" or "deferred-hidden" or "deferred-overflow" or "scattered-pages"))
                    || (parsed.Heads.After.Length > 0 && discovery is "shared-page" or "member-page" or "shared-page-live" or "scattered-pages"))
                {
                    var held = Interlocked.Increment(ref _heldHeads);
                    if (held == 7)
                    {
                        SevenPending.TrySetResult();
                    }

                    if (held == ExpectedPending)
                    {
                        Pending.TrySetResult();
                    }

                    try
                    {
                        await ReleaseSlow.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (discovery == "deferred-overflow")
                        {
                            Volatile.Write(ref _overflowRepliesAtCancellation, OverflowReplies.Count);
                        }

                        if (Interlocked.Increment(ref _cancelledHeads) == ExpectedPending)
                        {
                            Cancelled.TrySetResult();
                        }

                        await ReleaseCancelled.Task;
                        throw;
                    }

                    throw new IOException("the slow member has no heads reply");
                }

                if (discovery == "refill" && subject == WanProtocol.MemberSubject("gru", "gru-b"))
                {
                    await Pending.Task.WaitAsync(cancellationToken);
                }

                if ((discovery is "refill" or "rotation" or "transitive" or "shared-page-live" or "deferred-hidden" or "deferred-overflow" or "scattered-pages") && subject == WanProtocol.MemberSubject("gru", "gru-c"))
                {
                    if (discovery is "rotation" or "transitive" or "scattered-pages")
                    {
                        await SevenPending.Task.WaitAsync(cancellationToken);
                    }

                    var data = await inner.RequestAsync(subject, request, cancellationToken);
                    HolderQueried.TrySetResult();
                    await ReleaseHolder.Task.WaitAsync(cancellationToken);
                    return Rewrite(data, parsed);
                }

                return Rewrite(await inner.RequestAsync(subject, request, cancellationToken), parsed);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeHeads);
                if (discovery == "shared-page-live" && subject == WanProtocol.MemberSubject("gru", "gru-c"))
                {
                    HolderReplied.TrySetResult();
                }
            }
        }

        public Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken) =>
            inner.ServeAsync(subject, answer, cancellationToken);

        private ReadOnlyMemory<byte> Rewrite(ReadOnlyMemory<byte> data, WanRequest request)
        {
            if (request.Heads is null)
            {
                return data;
            }

            var reply = WanProtocol.DecodeHeads(data).Clone();
            reply.Members.Clear();
            if (!Faulted)
            {
                reply.Members.AddRange(Members);
                if (InvalidScatter || discovery == "page-overflow")
                {
                    foreach (var head in reply.Heads)
                    {
                        _ = InitialSequences.TryAdd(head.DatabaseId, head.Sequence);
                    }
                }
            }
            else if (InvalidScatter)
            {
                reply.Members.Add(reply.Member);
            }
            else if (discovery == "page-overflow")
            {
                reply.Members.Add(reply.Member);
                if (reply.Member == "gru-a" && request.Heads.After.Length == 0)
                {
                    reply.Members.AddRange(Members.Where(member => member != reply.Member));
                    reply.Members.AddRange(_unreachable);
                    while (reply.Heads.Count > 1)
                    {
                        reply.Heads.RemoveAt(reply.Heads.Count - 1);
                    }

                    reply.More = true;
                    OverflowAdvertisement = reply;
                }
                else if (reply.Member == "gru-a")
                {
                    reply.Members.Add("gru-page-overflow");
                    OverflowReplies.Add(reply);
                }
            }
            else if (discovery is "members" or "rotation" || (discovery == "deferred-hidden" && Volatile.Read(ref _round) == 0))
            {
                reply.Members.Add("gru-a");
                reply.Members.AddRange(_unreachable);
                reply.Members.Add("gru-c");
            }
            else if (discovery == "deferred-overflow" && Volatile.Read(ref _round) == 0)
            {
                reply.Members.AddRange(["gru-a", "gru-d", "gru-e"]);
                reply.Members.AddRange(_unreachable);
                reply.Members.Add("gru-c");
                OverflowAdvertisement = reply;
            }
            else if (discovery is "deferred-hidden" or "deferred-overflow")
            {
                reply.Members.Add("gru-a");
                _ = Interlocked.Increment(ref _hiddenAdvertisements);
            }
            else if (discovery == "scattered-pages")
            {
                reply.Members.Add(reply.Member);
                if (reply.Member == "gru-a" && request.Heads.After.Length == 0)
                {
                    while (reply.Heads.Count > 1)
                    {
                        reply.Heads.RemoveAt(reply.Heads.Count - 1);
                    }

                    reply.More = true;
                }
            }
            else if ((discovery is "refill" or "transitive") && reply.Member == "gru-a")
            {
                reply.Members.Add("gru-a");
                reply.Members.AddRange(_unreachable);
                reply.Members.Add("gru-b");
            }
            else if (discovery is "refill" or "transitive")
            {
                reply.Members.AddRange(Members);
                if (discovery == "transitive" && reply.Member == "gru-b")
                {
                    BridgeReplied.TrySetResult();
                }
            }
            else
            {
                reply.Members.AddRange(discovery == "member-page" ? Members : ["gru-a", "gru-c"]);
                if (reply.Member == "gru-a" && request.Heads.After.Length == 0)
                {
                    while (reply.Heads.Count > 1)
                    {
                        reply.Heads.RemoveAt(reply.Heads.Count - 1);
                    }

                    reply.More = true;
                }
            }

            return WanProtocol.EncodeHeads(reply);
        }
    }
}
