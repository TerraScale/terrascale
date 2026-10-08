using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using TerraBase.Features.CrossRegion;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.CrossRegion;

public sealed partial class CrossRegionPagingProgressTests
{
    private static readonly string LargeName = new('n', 64_000);

    [Test]
    public async Task A_finished_member_keeps_paging_and_retains_its_cursor_while_another_member_is_silent()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-finished-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                var organizationName = faker.Company.CompanyName();
                var projectName = faker.Random.AlphaNumeric(12);
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = organizationName },
                            new { op = "create_project", organization, id = project, name = projectName },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 3; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "finished-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var tail = databases[^1];
                var document = ManagementApi.Uri(ManagementApi.DocumentPath(tail, "items", "saved"));
                var value = faker.Lorem.Sentence();
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(source, tail, "items");
                using var initial = await source.PutAsJsonAsync(document, new { value }, token);
                await Assert.That(initial.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var version = initial.Headers.ETag!.Tag;
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                HashSet<string> removed = [with(StringComparer.Ordinal)];
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                await AssertReadAsync(target, document, value, version, token);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);

                bus.Slow = true;
                bus.AdvertiseSilentMember = true;
                for (var round = 0; round < 3; round++)
                {
                    var previousValue = value;
                    var previousVersion = version;
                    if (round > 0)
                    {
                        value += " " + faker.Lorem.Sentence();
                        using var written = await source.PutAsJsonAsync(document, new { value }, token);
                        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        version = written.Headers.ETag!.Tag;
                    }

                    // A reaches EOF, then a new scan stops after its middle, then resumes at its tail. B never answers.
                    await AdvanceRetainedMemberRoundAsync(bus, clock, Cursor(databases[round == 2 ? 1 : 0]),
                        interrupted: round == 1, resumed: round == 2, token);
                    var advertisement = bus.LastAdvertisement!;
                    await Assert.That(advertisement.Member).IsEqualTo("gru-a");
                    await Assert.That(advertisement.Members.SequenceEqual(round == 0 ? ["gru-a", "gru-silent-0"] : ["gru-a"])).IsTrue();
                    await Assert.That(advertisement.More).IsTrue();
                    await Assert.That(advertisement.Heads.Single().DatabaseId).IsEqualTo(WanProtocol.Id(databases[0].Id));
                    await Assert.That(bus.PageRequests.Count).IsEqualTo(round == 2 ? 2 : 3);
                    await Assert.That(bus.PageRequests.Where(request => request.Member == "gru-silent-0").Select(request => request.After)
                        .SequenceEqual([ByteString.Empty])).IsTrue();
                    ByteString[] expectedCursors = round == 2 ? [Cursor(databases[1])] : [Cursor(databases[0]), Cursor(databases[1])];
                    await Assert.That(bus.PageRequests.Where(request => request.Member == "gru-a").Select(request => request.After)
                        .SequenceEqual(expectedCursors)).IsTrue();
                    await Assert.That(bus.RequestedMembers.SetEquals(["gru-a", "gru-silent-0"])).IsTrue();
                    await Assert.That(bus.PageReplies.Count).IsEqualTo(round == 0 ? 2 : 1);
                    var expectedHeads = round == 0 ? databases.Skip(1) : databases.Skip(round == 1 ? 1 : 2).Take(1);
                    await Assert.That(bus.PageReplies.SelectMany(reply => reply.Heads).Select(head => head.DatabaseId)
                        .SequenceEqual(expectedHeads.Select(database => WanProtocol.Id(database.Id)))).IsTrue();
                    await Assert.That(bus.PageReplies.All(reply => reply.Member == "gru-a" && reply.Heads.Count == 1
                        && reply.More == (reply.Heads.Single().DatabaseId == WanProtocol.Id(databases[1].Id)))).IsTrue();
                    await Assert.That(bus.PageReplies.Append(advertisement).All(reply => reply.CalculateSize() <= WanProtocol.MaxMessageBytes)).IsTrue();
                    await Assert.That(bus.Pulls.Any(pull => pull.DatabaseId == WanProtocol.Id(tail.Id))).IsEqualTo(round == 2);
                    if (round == 2)
                    {
                        await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                    }

                    await AssertReadAsync(target, document, round == 1 ? previousValue : value, round == 1 ? previousVersion : version, token);
                    await AssertSweepInventoryAsync(sfo, databases, removed, token);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token))
                        .IsEqualTo((clock.GetUtcNow() - clock.CaughtUpAt).TotalSeconds);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(round == 1 ? 0 : 1);
                    await AssertStaleGrowthAsync(sfo, clock, token);
                    // B is retained despite disappearing from all fresh advertisements.
                    bus.AdvertiseSilentMember = false;
                }

                // B reaches EOF while A's fresh scan stops at the deadline. Both hold EOF certificates: the sweep ends.
                bus.SilentMemberAnswers = true;
                bus.NextRound();
                clock.Advance(clock.NextInterval);
                var finalCalls = new[]
                {
                    await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token),
                    await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token),
                };
                var answering = finalCalls.Single(call => call.Member == "gru-silent-0");
                var rescanning = finalCalls.Single(call => call.Member == "gru-a");
                await Assert.That(bus.PageRequests.Single(request => request.Member == "gru-a").After).IsEqualTo(Cursor(databases[0]));
                answering.Release.TrySetResult();
                await answering.Replied.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                clock.Advance(TimeSpan.FromSeconds(10));
                await rescanning.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                rescanning.Join.TrySetResult();
                await clock.PausedAsync(token);
                await Assert.That(bus.CancelledHeads).IsEqualTo(1);
                await AssertReadAsync(target, document, value, version, token);
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_seventeen_page_inventory_completes_across_rounds_and_advances_the_local_cleanup_page(bool malformedPrefix)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false, pageSize: 6);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-sweep-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                var organizationName = faker.Company.CompanyName();
                var projectName = faker.Random.AlphaNumeric(12);
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = organizationName },
                            new { op = "create_project", organization, id = project, name = projectName },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 103; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "sweep-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                // Within one project, canonical UUID text orders like both the catalog and 48-byte WAN cursors.
                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var firstPageDeleted = databases[1];
                var nextPageDeleted = databases[^2];
                var observed = new[] { databases[0], databases[50], databases[^1] };
                var documents = observed.Select(database => ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "saved"))).ToArray();
                var values = observed.Select(_ => faker.Lorem.Sentence()).ToArray();
                var versions = new string[observed.Length];
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                for (var index = 0; index < observed.Length; index++)
                {
                    await ManagementApi.CreateCollectionAsync(source, observed[index], "items");
                    using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    versions[index] = written.Headers.ETag!.Tag;
                }

                // One unlimited real heads scan seeds every record and replica file before pagination is constrained.
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                HashSet<string> removed = [with(StringComparer.Ordinal)];
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                for (var index = 0; index < observed.Length; index++)
                {
                    await AssertReadAsync(target, documents[index], values[index], versions[index], token);
                }

                await Assert.That(bus.LastAdvertisement!.Heads.Count).IsEqualTo(103);
                await Assert.That(bus.LastAdvertisement.More).IsFalse();
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                foreach (var database in new[] { firstPageDeleted, nextPageDeleted })
                {
                    using var unplaced = await gru.Client.PatchAsJsonAsync(ManagementApi.Uri(database.Path), new { replicaRegions = Array.Empty<string>() }, token);
                    await Assert.That(unplaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    using var deleted = await gru.Client.DeleteAsync(ManagementApi.Uri(database.Path), token);
                    await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                    using var absent = await gru.Client.GetAsync(ManagementApi.Uri(database.Path), token);
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                var live = databases.Where(database => database != firstPageDeleted && database != nextPageDeleted).ToArray();
                await Assert.That(live.Length).IsEqualTo(101);
                bus.Slow = true;
                if (malformedPrefix)
                {
                    bus.MalformedFirstReply = true;
                    for (var round = 0; round < 2; round++)
                    {
                        await AdvanceSweepRoundAsync(bus, clock, token);
                        await Assert.That(bus.MalformedFirstReply).IsFalse();
                        await AssertSweepPagesAsync(bus, live, resumed: round == 1);
                        // A clean final suffix retires tainted evidence, but must not clean or advance this local page.
                        await AssertSweepInventoryAsync(sfo, databases, removed, token);
                        for (var index = 0; index < observed.Length; index++)
                        {
                            await AssertReadAsync(target, documents[index], values[index], versions[index], token);
                        }

                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token))
                            .IsEqualTo((clock.GetUtcNow() - clock.CaughtUpAt).TotalSeconds);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(round);
                        await AssertStaleGrowthAsync(sfo, clock, token);
                    }
                }

                // Both cases must now start at the prefix and qualify the same first local catalog page, then the next.
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    var previousTailValue = values[^1];
                    var previousTailVersion = versions[^1];
                    for (var index = 0; index < observed.Length; index++)
                    {
                        values[index] += " " + faker.Lorem.Sentence();
                        using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
                        versions[index] = written.Headers.ETag!.Tag;
                    }

                    for (var round = 0; round < 2; round++)
                    {
                        await AdvanceSweepRoundAsync(bus, clock, token);
                        await AssertSweepPagesAsync(bus, live, resumed: round == 1);
                        await Assert.That(bus.Pulls.Any(pull => pull.DatabaseId == WanProtocol.Id(observed[^1].Id))).IsEqualTo(round == 1);
                        await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                        await AssertReadAsync(target, documents[0], values[0], versions[0], token);
                        await AssertReadAsync(target, documents[1], values[1], versions[1], token);
                        await AssertReadAsync(target, documents[^1], round == 0 ? previousTailValue : values[^1], round == 0 ? previousTailVersion : versions[^1], token);
                        if (round == 1)
                        {
                            // Only this cycle's fixed local page may be cleaned: first 100, then the last three.
                            _ = removed.Add(cycle == 0 ? firstPageDeleted.Id : nextPageDeleted.Id);
                            await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                            clock.CaughtUpAt = clock.GetUtcNow();
                        }
                        else
                        {
                            await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token))
                                .IsEqualTo((clock.GetUtcNow() - clock.CaughtUpAt).TotalSeconds);
                        }

                        // The HTTP catalog and all files prove no unwitnessed prefix or later-page deletion slipped in.
                        await AssertSweepInventoryAsync(sfo, databases, removed, token);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(round);
                        await AssertStaleGrowthAsync(sfo, clock, token);
                    }
                }
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("database-id")]
    [Arguments("origin")]
    [Arguments("sequence")]
    public async Task A_malformed_head_preserves_replicas_and_staleness_until_a_clean_sweep(string field)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false, pageSize: 2);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-head-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 2; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "head-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var documents = databases.Select(database => ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "saved"))).ToArray();
                var values = databases.Select(_ => faker.Lorem.Sentence()).ToArray();
                var versions = new string[databases.Count];
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                for (var index = 0; index < databases.Count; index++)
                {
                    await ManagementApi.CreateCollectionAsync(source, databases[index], "items");
                    using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    versions[index] = written.Headers.ETag!.Tag;
                }

                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                HashSet<string> removed = [with(StringComparer.Ordinal)];
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                for (var index = 0; index < databases.Count; index++)
                {
                    await AssertReadAsync(target, documents[index], values[index], versions[index], token);
                }

                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                var previousValue = values[0];
                var previousVersion = versions[0];
                for (var index = 0; index < databases.Count; index++)
                {
                    values[index] += " " + faker.Lorem.Sentence();
                    using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    versions[index] = written.Headers.ETag!.Tag;
                }

                bus.Slow = true;
                bus.CorruptFirstHead = head =>
                {
                    switch (field)
                    {
                        case "database-id": head.DatabaseId = ByteString.Empty; break;
                        case "origin": head.Origin = ByteString.Empty; break;
                        case "sequence": head.Sequence = ulong.MaxValue; break;
                        default: throw new InvalidOperationException("unknown malformed-head field");
                    }
                };

                for (var round = 0; round < 2; round++)
                {
                    await AdvanceSweepRoundAsync(bus, clock, token);
                    var advertisement = bus.LastAdvertisement!;
                    await Assert.That(bus.CorruptFirstHead is null).IsTrue();
                    await Assert.That(advertisement.Member).IsEqualTo("gru-a");
                    await Assert.That(advertisement.Members.SequenceEqual(["gru-a"])).IsTrue();
                    await Assert.That(advertisement.More).IsFalse();
                    await Assert.That(advertisement.Heads.Count).IsEqualTo(2);
                    await Assert.That(advertisement.Heads[0].DatabaseId)
                        .IsEqualTo(round == 0 && field == "database-id" ? ByteString.Empty : WanProtocol.Id(databases[0].Id));
                    await Assert.That(advertisement.Heads[0].Origin.Length).IsEqualTo(round == 0 && field == "origin" ? 0 : 16);
                    await Assert.That(advertisement.Heads[0].Sequence > long.MaxValue).IsEqualTo(round == 0 && field == "sequence");
                    await Assert.That(advertisement.Heads[1].DatabaseId).IsEqualTo(WanProtocol.Id(databases[1].Id));
                    await Assert.That(advertisement.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                    await Assert.That(bus.PageRequests.Count).IsEqualTo(0);
                    await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                    await Assert.That(bus.MaxActiveHeads).IsEqualTo(0);
                    await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).SequenceEqual([WanProtocol.Id(databases[round == 0 ? 1 : 0].Id)])).IsTrue();
                    await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                    await AssertSweepInventoryAsync(sfo, databases, removed, token);
                    await AssertReadAsync(target, documents[0], round == 0 ? previousValue : values[0], round == 0 ? previousVersion : versions[0], token);
                    await AssertReadAsync(target, documents[1], values[1], versions[1], token);
                    if (round == 1)
                    {
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                        clock.CaughtUpAt = clock.GetUtcNow();
                    }
                    else
                    {
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token))
                            .IsEqualTo((clock.GetUtcNow() - clock.CaughtUpAt).TotalSeconds);
                    }

                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                    await AssertStaleGrowthAsync(sfo, clock, token);
                }
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_head_left_behind_on_a_resumed_page_is_retried_before_a_fresh_scan_reaches_it(bool unanswered)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-retry-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 3; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "retry-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var middle = databases[1];
                var document = ManagementApi.Uri(ManagementApi.DocumentPath(middle, "items", "saved"));
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(source, middle, "items");
                var value = faker.Lorem.Sentence();
                string version;
                using (var initial = await source.PutAsJsonAsync(document, new { value }, token))
                {
                    await Assert.That(initial.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    version = initial.Headers.ETag!.Tag;
                }

                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                await AssertReadAsync(target, document, value, version, token);
                var previousValue = value;
                var previousVersion = version;
                value += " " + faker.Lorem.Sentence();
                using (var written = await source.PutAsJsonAsync(document, new { value }, token))
                {
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    version = written.Headers.ETag!.Tag;
                }

                // Round 0: the scan saves its cursor after the middle page at the deadline, and pulling the middle fails.
                bus.Slow = true;
                bus.FailPullsOf = WanProtocol.Id(middle.Id);
                bus.NextRound();
                clock.Advance(clock.NextInterval);
                clock.NextInterval = WanPuller.Interval;
                var page = await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
                page.Release.TrySetResult();
                await page.Replied.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                var held = await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
                clock.Advance(TimeSpan.FromSeconds(10));
                await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                held.Join.TrySetResult();
                await clock.PausedAsync(token);
                await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual([Cursor(databases[0]), Cursor(middle)])).IsTrue();
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).SequenceEqual([WanProtocol.Id(middle.Id)])).IsTrue();
                await AssertReadAsync(target, document, previousValue, previousVersion, token);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsGreaterThan(0);

                // Round 1 either resumes past the middle database to EOF or gets no heads answer at all. Either way only the
                // retained head can still bring it up to date.
                bus.FailPullsOf = null;
                bus.Unanswered = unanswered;
                bus.NextRound();
                clock.Advance(clock.NextInterval);
                clock.NextInterval = WanPuller.Interval;
                if (!unanswered)
                {
                    page = await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
                    page.Release.TrySetResult();
                    await page.Replied.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                }

                await clock.PausedAsync(token);
                ByteString[] expectedCursors = unanswered ? [] : [Cursor(middle)];
                await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual(expectedCursors)).IsTrue();
                ByteString[] expectedHeads = unanswered ? [] : [WanProtocol.Id(databases[2].Id)];
                await Assert.That(bus.PageReplies.SelectMany(reply => reply.Heads).Select(head => head.DatabaseId).SequenceEqual(expectedHeads)).IsTrue();
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).Distinct().SequenceEqual([WanProtocol.Id(middle.Id)])).IsTrue();
                await AssertReadAsync(target, document, value, version, token);
                // Unanswered discovery measures no lag and keeps the member count at 0.
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(unanswered ? 0 : 1);
                if (!unanswered)
                {
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                }
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_malformed_fresh_head_does_not_supersede_a_valid_retry()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-retry-malformed-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 2; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "malformed-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var first = databases[0];
                var firstId = WanProtocol.Id(first.Id);
                var document = ManagementApi.Uri(ManagementApi.DocumentPath(first, "items", "saved"));
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(source, first, "items");
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                var value = faker.Lorem.Sentence();
                string version;
                using (var written = await source.PutAsJsonAsync(document, new { value }, token))
                {
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    version = written.Headers.ETag!.Tag;
                }

                // Round 0 keeps the first database's head as a retry after its pull fails.
                bus.Slow = true;
                bus.FailPullsOf = firstId;
                await AdvanceSweepRoundAsync(bus, clock, token);
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).SequenceEqual([firstId])).IsTrue();

                // Round 1 advertises the same database with an unusable origin: the valid retry must still pull it.
                bus.FailPullsOf = null;
                bus.CorruptFirstHead = head => head.Origin = ByteString.Empty;
                await AdvanceSweepRoundAsync(bus, clock, token);
                await Assert.That(bus.CorruptFirstHead is null).IsTrue();
                await Assert.That(bus.LastAdvertisement!.Heads[0].DatabaseId).IsEqualTo(firstId);
                await Assert.That(bus.LastAdvertisement.Heads[0].Origin.IsEmpty).IsTrue();
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).Distinct().SequenceEqual([firstId])).IsTrue();
                await AssertReadAsync(target, document, value, version, token);
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_retry_whose_origin_its_member_no_longer_holds_is_dropped()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-retry-origin-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 4; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "origin-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var middle = databases[1];
                var middleId = WanProtocol.Id(middle.Id);
                var document = ManagementApi.Uri(ManagementApi.DocumentPath(middle, "items", "saved"));
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(source, middle, "items");
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                var value = faker.Lorem.Sentence();
                string version;
                using (var written = await source.PutAsJsonAsync(document, new { value }, token))
                {
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    version = written.Headers.ETag!.Tag;
                }

                // Round 0 keeps the middle head as a retry: its pull fails and the scan stops after its page.
                bus.Slow = true;
                bus.FailPullsOf = middleId;
                await AdvanceCutRoundAsync(bus, clock, released: 1, token);
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).SequenceEqual([middleId])).IsTrue();

                // Round 1 resumes past it, and its member answers that it no longer holds the retried origin.
                bus.FailPullsOf = null;
                bus.UnknownOriginOf = middleId;
                await AdvanceCutRoundAsync(bus, clock, released: 1, token);
                await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual([Cursor(middle), Cursor(databases[2])])).IsTrue();
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).SequenceEqual([middleId])).IsTrue();

                // Round 2 still resumes past it: a dropped retry sends no further futile pull.
                bus.UnknownOriginOf = null;
                await AdvanceSweepRoundAsync(bus, clock, token);
                await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual([Cursor(databases[2])])).IsTrue();
                await Assert.That(bus.Pulls.Count).IsEqualTo(0);

                // The next fresh scan advertises the middle database again, and its current head brings it up to date.
                await AdvanceSweepRoundAsync(bus, clock, token);
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).Distinct().SequenceEqual([middleId])).IsTrue();
                await AssertReadAsync(target, document, value, version, token);
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_retry_never_brings_back_a_database_its_sweep_certified_deleted()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-retry-deleted-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 3; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "gone-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var middle = databases[1];
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(source, middle, "items");
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                HashSet<string> removed = [with(StringComparer.Ordinal)];
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                using (var written = await source.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(middle, "items", "saved")), new { value = faker.Lorem.Sentence() }, token))
                {
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                }

                // Round 0 ends a complete sweep with the middle database's head retained after its pull fails.
                bus.Slow = true;
                bus.FailPullsOf = WanProtocol.Id(middle.Id);
                await AdvanceSweepRoundAsync(bus, clock, token);
                await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual([Cursor(databases[0]), Cursor(middle)])).IsTrue();
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).SequenceEqual([WanProtocol.Id(middle.Id)])).IsTrue();
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsGreaterThan(0);
                await AssertSweepInventoryAsync(sfo, databases, removed, token);

                // The home region deletes it before the next sweep, whose complete inventory certifies its absence.
                using (var unplaced = await gru.Client.PatchAsJsonAsync(ManagementApi.Uri(middle.Path), new { replicaRegions = Array.Empty<string>() }, token))
                {
                    await Assert.That(unplaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
                }

                using (var deleted = await gru.Client.DeleteAsync(ManagementApi.Uri(middle.Path), token))
                {
                    await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                }

                bus.FailPullsOf = null;
                _ = removed.Add(middle.Id);
                for (var round = 0; round < 2; round++)
                {
                    await AdvanceSweepRoundAsync(bus, clock, token);
                    await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual([Cursor(databases[0])])).IsTrue();
                    await Assert.That(bus.Pulls.Count).IsEqualTo(0);
                    // Neither the record nor the replica file comes back from the stale retried head.
                    await AssertSweepInventoryAsync(sfo, databases, removed, token);
                }
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_round_holds_a_bounded_total_of_heads_across_members_and_resumes_their_cursors()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-round-budget-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                string[] large = ["gru-large-1", "gru-large-2"];
                var tenant = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
                bus.Answer = (member, after) => Array.IndexOf(large, member) is var which and >= 0 ? LargePage(tenant, which, member, after, advertise: true) : null;
                bus.Slow = true;
                HashSet<string> advertised = [with(StringComparer.Ordinal)];
                for (var round = 0; round < 2; round++)
                {
                    bus.CorruptFirstReply = reply => reply.Members.Add(large);
                    await AdvanceSweepRoundAsync(bus, clock, token, maxPages: 64);
                    var requests = bus.PageRequests.Where(request => large.Contains(request.Member)).ToList();
                    await Assert.That(bus.PageReplies.Where(reply => large.Contains(reply.Member))
                        .All(reply => reply.CalculateSize() is > 950_000 and <= WanProtocol.MaxMessageBytes)).IsTrue();
                    // Unbounded, both members would each send 16 pages, about 32 MB. The round stops near 16 MiB instead.
                    await Assert.That(requests.Count).IsBetween(16, 20);
                    foreach (var member in large)
                    {
                        var resumed = requests.Where(request => request.Member == member && !request.After.IsEmpty)
                            .Select(request => BinaryPrimitives.ReadInt64BigEndian(request.After.Span[^8..])).ToList();
                        // Round 1 resumes each member after the cursor its last accepted page left in round 0.
                        await Assert.That(resumed.Count).IsGreaterThan(0);
                        await Assert.That(resumed.Min() > 14).IsEqualTo(round == 1);
                    }

                    // Every page lists one more member, including pages refused for the budget: the same round asks each one.
                    advertised.UnionWith(bus.PageReplies.Where(reply => large.Contains(reply.Member))
                        .SelectMany(reply => reply.Members).Where(name => name.StartsWith("gru-hidden-", StringComparison.Ordinal)));
                    await Assert.That(advertised.IsSubsetOf(bus.RequestedMembers)).IsTrue();
                }

                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsGreaterThan(0);
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Saturating_scattered_first_pages_still_leave_room_for_saved_cursors()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-scatter-budget-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                // Twenty members scatter about 1 MB first pages every round: about 20 MB against the round's 16 MiB.
                var wide = Enumerable.Range(0, 20).Select(index => "gru-wide-" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
                var tenant = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
                bus.Scatter = () => wide.Select((member, which) => LargePage(tenant, which, member, ByteString.Empty));
                bus.Answer = (member, after) => Array.IndexOf(wide, member) is var which and >= 0 ? LargePage(tenant, which, member, after) : null;
                bus.Slow = true;
                HashSet<string> paged = [with(StringComparer.Ordinal)];
                // How many members a round serves depends on which replies land first; rotation still reaches every one.
                for (var round = 0; round < 12 && !paged.SetEquals(wide); round++)
                {
                    await AdvanceSweepRoundAsync(bus, clock, token, maxPages: 64);
                    var resumed = bus.PageRequests.Where(request => wide.Contains(request.Member) && !request.After.IsEmpty).ToList();
                    if (round == 0)
                    {
                        // Continuations get the half that scattered first pages cannot take, so saved cursors advance.
                        await Assert.That(resumed.Max(request => BinaryPrimitives.ReadInt64BigEndian(request.After.Span[^8..])))
                            .IsGreaterThanOrEqualTo(29);
                    }

                    // A member asks for a later page only after one of its pages was accepted.
                    paged.UnionWith(resumed.Select(request => request.Member));
                }

                // Members served this round queue behind the rest, so every member gets pages under sustained saturation.
                await Assert.That(paged.SetEquals(wide)).IsTrue();
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("unordered")]
    [Arguments("old-key")]
    public async Task A_misordered_heads_page_cannot_skip_databases_or_authorize_cleanup(string fault)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false, pageSize: 2);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-order-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 6; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "order-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                HashSet<string> removed = [with(StringComparer.Ordinal)];
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);

                bus.Slow = true;
                // The second page would hold 2 and 3. Both faults skip 2 while the last key, 3, still advances past 1.
                bus.CorruptNextPage = reply =>
                {
                    var tail = reply.Heads.ToArray();
                    reply.Heads.Clear();
                    reply.Heads.Add(fault == "old-key" ? bus.LastAdvertisement!.Heads[0].Clone() : tail[2]);
                    reply.Heads.Add(tail[1]);
                    reply.More = true;
                };

                // Round 0 rejects the page, round 1 reaches EOF on a tainted sweep, round 2 is a clean sweep.
                for (var round = 0; round < 3; round++)
                {
                    await AdvanceSweepRoundAsync(bus, clock, token);
                    await Assert.That(bus.CorruptNextPage is null).IsTrue();
                    await Assert.That(bus.LastAdvertisement!.Heads.Select(head => head.DatabaseId)
                        .SequenceEqual(databases.Take(2).Select(database => WanProtocol.Id(database.Id)))).IsTrue();
                    ByteString[] expectedCursors = round == 0 ? [Cursor(databases[1])] : [Cursor(databases[1]), Cursor(databases[3])];
                    await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual(expectedCursors)).IsTrue();
                    await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                    // Every replica and record survives: no misordered page or its later EOF certifies 2's absence.
                    await AssertSweepInventoryAsync(sfo, databases, removed, token);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(round == 0 ? 0 : 1);
                    if (round == 2)
                    {
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                    }
                    else
                    {
                        await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token))
                            .IsEqualTo((clock.GetUtcNow() - clock.CaughtUpAt).TotalSeconds);
                        await AssertStaleGrowthAsync(sfo, clock, token);
                    }
                }
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task An_empty_partial_reply_taints_a_retained_EOF_certificate_until_a_fresh_clean_sweep()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false, pageSize: 2);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-empty-partial-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var silent = await StartAsync(network, credentials, "gru-1", "gru", "gru-silent-0");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 2; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "empty-partial-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var saved = databases[0];
                var live = databases[1];
                var documents = databases.Select(database => ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "saved"))).ToArray();
                var values = databases.Select(_ => faker.Lorem.Sentence()).ToArray();
                var versions = new string[databases.Count];
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                for (var index = 0; index < databases.Count; index++)
                {
                    await ManagementApi.CreateCollectionAsync(source, databases[index], "items");
                    using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    versions[index] = written.Headers.ETag!.Tag;
                }

                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                HashSet<string> removed = [with(StringComparer.Ordinal)];
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                for (var index = 0; index < databases.Count; index++)
                {
                    await AssertReadAsync(target, documents[index], values[index], versions[index], token);
                }

                await Assert.That(bus.LastAdvertisement!.Heads.Count).IsEqualTo(2);
                await Assert.That(bus.LastAdvertisement.More).IsFalse();
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                using (var unplaced = await gru.Client.PatchAsJsonAsync(ManagementApi.Uri(saved.Path), new { replicaRegions = Array.Empty<string>() }, token))
                {
                    await Assert.That(unplaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
                }

                using (var deleted = await gru.Client.DeleteAsync(ManagementApi.Uri(saved.Path), token))
                {
                    await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                }

                using (var absent = await gru.Client.GetAsync(ManagementApi.Uri(saved.Path), token))
                {
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                bus.Slow = true;
                bus.AdvertiseSilentMember = true;
                bus.NextRound();
                clock.Advance(clock.NextInterval);
                clock.NextInterval = WanPuller.Interval;
                var pending = await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
                await Assert.That(pending.Member).IsEqualTo("gru-silent-0");
                var advertisement = bus.LastAdvertisement!;
                await Assert.That(advertisement.Member).IsEqualTo("gru-a");
                await Assert.That(advertisement.Members.SequenceEqual(["gru-a", "gru-silent-0"])).IsTrue();
                await Assert.That(advertisement.More).IsFalse();
                await Assert.That(advertisement.Heads.Single().DatabaseId).IsEqualTo(WanProtocol.Id(live.Id));
                await Assert.That(advertisement.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                await Assert.That(bus.ActiveHeads).IsEqualTo(1);
                clock.Advance(TimeSpan.FromSeconds(10));
                await pending.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                await Assert.That(bus.ActiveHeads).IsEqualTo(1);
                await Assert.That(bus.Pulls.Count).IsEqualTo(0);
                pending.Join.TrySetResult();
                await clock.PausedAsync(token);
                await Assert.That(bus.PageRequests.Count).IsEqualTo(1);
                await Assert.That(bus.PageRequests.Single()).IsEqualTo(("gru-silent-0", ByteString.Empty));
                await Assert.That(bus.PageReplies.Count).IsEqualTo(0);
                await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                await Assert.That(bus.MaxActiveHeads).IsEqualTo(1);
                await Assert.That(bus.CancelledHeads).IsEqualTo(1);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                var file = Path.Combine(sfo.Server.DataDirectory, organization, project, saved.Id + ".db");
                await AssertSavedAsync(sfo, target, saved, documents[0], values[0], versions[0], file, clock, token);
                await AssertStaleGrowthAsync(sfo, clock, token);

                // A's earlier EOF did not witness the deleted candidate. B's real EOF must not bless A's malformed scan.
                bus.AdvertiseSilentMember = false;
                bus.CorruptFirstReply = reply =>
                {
                    reply.Heads.Clear();
                    reply.More = true;
                };
                await AdvanceSweepRoundAsync(bus, clock, token);
                advertisement = bus.LastAdvertisement!;
                await Assert.That(bus.CorruptFirstReply is null).IsTrue();
                await Assert.That(advertisement.Member).IsEqualTo("gru-a");
                await Assert.That(advertisement.Members.SequenceEqual(["gru-a"])).IsTrue();
                await Assert.That(advertisement.More).IsTrue();
                await Assert.That(advertisement.Heads.Count).IsEqualTo(0);
                await Assert.That(advertisement.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                await Assert.That(bus.PageRequests.Count).IsEqualTo(1);
                await Assert.That(bus.PageRequests.Single()).IsEqualTo(("gru-silent-0", ByteString.Empty));
                var eof = bus.PageReplies.Single();
                await Assert.That(eof.Member).IsEqualTo("gru-silent-0");
                await Assert.That(eof.Members.SequenceEqual(["gru-silent-0"])).IsTrue();
                await Assert.That(eof.More).IsFalse();
                await Assert.That(eof.Heads.Count).IsEqualTo(0);
                await Assert.That(eof.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                await Assert.That(bus.RequestedMembers.SetEquals(["gru-silent-0"])).IsTrue();
                await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                await Assert.That(bus.MaxActiveHeads).IsEqualTo(1);
                await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                await Assert.That(bus.Pulls.Count).IsEqualTo(0);
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                await AssertSavedAsync(sfo, target, saved, documents[0], values[0], versions[0], file, clock, token);
                await AssertReadAsync(target, documents[1], values[1], versions[1], token);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                await AssertStaleGrowthAsync(sfo, clock, token);

                await AdvanceSweepRoundAsync(bus, clock, token);
                advertisement = bus.LastAdvertisement!;
                await Assert.That(advertisement.Member).IsEqualTo("gru-a");
                await Assert.That(advertisement.Members.SequenceEqual(["gru-a"])).IsTrue();
                await Assert.That(advertisement.More).IsFalse();
                await Assert.That(advertisement.Heads.Single().DatabaseId).IsEqualTo(WanProtocol.Id(live.Id));
                await Assert.That(bus.PageRequests.Count).IsEqualTo(0);
                await Assert.That(bus.PageReplies.Count).IsEqualTo(0);
                await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                await Assert.That(bus.MaxActiveHeads).IsEqualTo(0);
                await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                await Assert.That(bus.Pulls.Count).IsEqualTo(0);
                _ = removed.Add(saved.Id);
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                using (var absent = await sfo.Client.GetAsync(ManagementApi.Uri(saved.Path), token))
                {
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                using (var absent = await target.GetAsync(documents[0], token))
                {
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                await AssertReadAsync(target, documents[1], values[1], versions[1], token);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Multiple_maximum_heads_saturate_lag_without_resetting_staleness_until_genuine_heads_arrive()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false, pageSize: 2);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-lag-overflow-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 2; index++)
                {
                    databases.Add(await CreatePlacedAsync(gru, organization, project, "lag-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var documents = databases.Select(database => ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "saved"))).ToArray();
                var values = databases.Select(_ => faker.Lorem.Sentence()).ToArray();
                var versions = new string[databases.Count];
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                for (var index = 0; index < databases.Count; index++)
                {
                    await ManagementApi.CreateCollectionAsync(source, databases[index], "items");
                    using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    versions[index] = written.Headers.ETag!.Tag;
                }

                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                HashSet<string> removed = [with(StringComparer.Ordinal)];
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                for (var index = 0; index < databases.Count; index++)
                {
                    await AssertReadAsync(target, documents[index], values[index], versions[index], token);
                }

                var initial = bus.LastAdvertisement!;
                await Assert.That(initial.More).IsFalse();
                await Assert.That(initial.Heads.Count).IsEqualTo(2);
                await Assert.That(initial.Heads.All(head => head.Sequence is > 0 and < long.MaxValue)).IsTrue();
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                bus.Slow = true;
                bus.CorruptFirstReply = reply =>
                {
                    foreach (var head in reply.Heads)
                    {
                        head.Sequence = long.MaxValue;
                    }
                };
                await AdvanceSweepRoundAsync(bus, clock, token);
                var advertisement = bus.LastAdvertisement!;
                await Assert.That(bus.CorruptFirstReply is null).IsTrue();
                await Assert.That(advertisement.Member).IsEqualTo("gru-a");
                await Assert.That(advertisement.Members.SequenceEqual(["gru-a"])).IsTrue();
                await Assert.That(advertisement.More).IsFalse();
                await Assert.That(advertisement.Heads.Count).IsEqualTo(2);
                await Assert.That(advertisement.Heads.Select(head => head.DatabaseId).SequenceEqual(databases.Select(database => WanProtocol.Id(database.Id)))).IsTrue();
                await Assert.That(advertisement.Heads.All(head => head.Sequence == long.MaxValue && head.Origin.Length == 16)).IsTrue();
                await Assert.That(advertisement.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                await Assert.That(bus.PageRequests.Count).IsEqualTo(0);
                await Assert.That(bus.PageReplies.Count).IsEqualTo(0);
                await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                await Assert.That(bus.MaxActiveHeads).IsEqualTo(0);
                await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                await Assert.That(bus.Pulls.Count).IsEqualTo(2);
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).SequenceEqual(initial.Heads.Select(head => head.DatabaseId))).IsTrue();
                await Assert.That(bus.Pulls.Select(pull => pull.Origin).SequenceEqual(initial.Heads.Select(head => head.Origin))).IsTrue();
                await Assert.That(bus.Pulls.Select(pull => pull.After).SequenceEqual(initial.Heads.Select(head => head.Sequence))).IsTrue();
                await Assert.That(bus.Pulls.All(pull => pull.Region == "sfo-1" && pull.Offset == 0)).IsTrue();
                await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                for (var index = 0; index < databases.Count; index++)
                {
                    await AssertReadAsync(target, documents[index], values[index], versions[index], token);
                }

                // Read the integer sample directly: the harness's double would round away precision near long.MaxValue.
                var metrics = await sfo.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);
                var lagSample = metrics.Split('\n').Single(line => line.StartsWith("terrabase_wan_lag{", StringComparison.Ordinal)
                    && line.Contains(GruLabel, StringComparison.Ordinal));
                var lag = lagSample[(lagSample.LastIndexOf('}') + 1)..].Trim().Split(' ')[0];
                await Assert.That(long.Parse(lag, CultureInfo.InvariantCulture)).IsEqualTo(long.MaxValue);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token))
                    .IsEqualTo((clock.GetUtcNow() - clock.CaughtUpAt).TotalSeconds);
                await AssertStaleGrowthAsync(sfo, clock, token);

                // Real source stores cannot supply the inflated heads, but their next genuine change remains pullable.
                for (var index = 0; index < databases.Count; index++)
                {
                    values[index] += " " + faker.Lorem.Sentence();
                    using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    versions[index] = written.Headers.ETag!.Tag;
                }

                await AdvanceSweepRoundAsync(bus, clock, token);
                advertisement = bus.LastAdvertisement!;
                await Assert.That(advertisement.Member).IsEqualTo("gru-a");
                await Assert.That(advertisement.Members.SequenceEqual(["gru-a"])).IsTrue();
                await Assert.That(advertisement.More).IsFalse();
                await Assert.That(advertisement.Heads.Count).IsEqualTo(2);
                await Assert.That(advertisement.Heads.Select(head => head.Sequence).SequenceEqual(initial.Heads.Select(head => head.Sequence + 1))).IsTrue();
                await Assert.That(bus.PageRequests.Count).IsEqualTo(0);
                await Assert.That(bus.PageReplies.Count).IsEqualTo(0);
                await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                await Assert.That(bus.MaxActiveHeads).IsEqualTo(0);
                await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                await Assert.That(bus.Pulls.Count).IsEqualTo(2);
                await Assert.That(bus.Pulls.Select(pull => pull.DatabaseId).SequenceEqual(initial.Heads.Select(head => head.DatabaseId))).IsTrue();
                await Assert.That(bus.Pulls.Select(pull => pull.After).SequenceEqual(initial.Heads.Select(head => head.Sequence))).IsTrue();
                await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                await AssertSweepInventoryAsync(sfo, databases, removed, token);
                for (var index = 0; index < databases.Count; index++)
                {
                    await AssertReadAsync(target, documents[index], values[index], versions[index], token);
                }

                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
            }
            finally
            {
                bus.Begin.TrySetResult();
                bus.ReleaseAll();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AdvanceRetainedMemberRoundAsync(
        ProgressBus bus, ProgressClock clock, ByteString firstAfter, bool interrupted, bool resumed, CancellationToken token)
    {
        bus.NextRound();
        clock.Advance(clock.NextInterval);
        clock.NextInterval = WanPuller.Interval;
        var initial = new[]
        {
            await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token),
            await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token),
        };
        var silent = initial.Single(call => call.Member == "gru-silent-0");
        var page = initial.Single(call => call.Member == "gru-a");
        await Assert.That(bus.PageRequests.Single(request => request.Member == "gru-a").After).IsEqualTo(firstAfter);
        if (!resumed)
        {
            page.Release.TrySetResult();
            await page.Replied.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            page = await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(page.Member).IsEqualTo("gru-a");
        }

        if (!interrupted)
        {
            page.Release.TrySetResult();
            await page.Replied.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }

        PageCall[] pending = interrupted ? [silent, page] : [silent];
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.WhenAll(pending.Select(call => call.Cancelled.Task)).WaitAsync(TimeSpan.FromSeconds(30), token);
        await Assert.That(bus.Pulls.Count).IsEqualTo(0);
        foreach (var call in pending)
        {
            call.Join.TrySetResult();
        }

        await clock.PausedAsync(token);
        await Assert.That(bus.ActiveHeads).IsEqualTo(0);
        await Assert.That(bus.MaxActiveHeads).IsEqualTo(2);
        await Assert.That(bus.CancelledHeads).IsEqualTo(pending.Length);
    }

    // Releases this many page requests, then holds the next one until the discovery deadline cuts it.
    private static async Task AdvanceCutRoundAsync(ProgressBus bus, ProgressClock clock, int released, CancellationToken token)
    {
        bus.NextRound();
        clock.Advance(clock.NextInterval);
        clock.NextInterval = WanPuller.Interval;
        for (var index = 0; index < released; index++)
        {
            var page = await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
            page.Release.TrySetResult();
            await page.Replied.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }

        var held = await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
        clock.Advance(TimeSpan.FromSeconds(10));
        await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        held.Join.TrySetResult();
        await clock.PausedAsync(token);
    }

    // About 1 MB: 15 strictly ordered heads after the cursor, with more always to come. Each member has its own project;
    // with advertise, every page also lists a member named after that page.
    private static HeadsReply LargePage(ByteString tenant, int which, string member, ByteString after, bool advertise = false)
    {
        var start = after.IsEmpty ? 0 : BinaryPrimitives.ReadInt64BigEndian(after.Span[^8..]) + 1;
        var projectId = new byte[16];
        projectId[0] = (byte)(which + 1);
        var reply = new HeadsReply { Member = member, More = true, Members = { member } };
        if (advertise)
        {
            reply.Members.Add($"gru-hidden-{which}-{start}");
        }

        for (var index = start; index < start + 15; index++)
        {
            var databaseId = new byte[16];
            BinaryPrimitives.WriteInt64BigEndian(databaseId.AsSpan(8), index);
            reply.Heads.Add(new Head
            {
                TenantId = tenant,
                ProjectId = ByteString.CopyFrom(projectId),
                DatabaseId = ByteString.CopyFrom(databaseId),
                Origin = tenant,
                Sequence = 1,
                DatabaseName = LargeName,
                HomeRegion = "gru-1",
                ReplicaRegions = { "sfo-1" },
                PlacementVersion = 1,
            });
        }

        return reply;
    }

    private static async Task AdvanceSweepRoundAsync(ProgressBus bus, ProgressClock clock, CancellationToken token, int maxPages = 15)
    {
        bus.NextRound();
        clock.Advance(clock.NextInterval);
        clock.NextInterval = WanPuller.Interval;
        var pause = clock.PausedAsync(token);
        var pages = 0;
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (true)
        {
            var next = bus.Pages.Reader.ReadAsync(reading.Token).AsTask();
            _ = await Task.WhenAny(next, pause).WaitAsync(TimeSpan.FromSeconds(30), token);
            if (pause.IsCompleted)
            {
                await reading.CancelAsync();
                try
                {
                    _ = await next;
                }
                catch (OperationCanceledException) when (reading.IsCancellationRequested)
                {
                    // Join the unused read before another round can publish its first page.
                }

                await pause;
                return;
            }

            var call = await next;
            await Assert.That(++pages).IsLessThanOrEqualTo(maxPages);
            call.Release.TrySetResult();
        }
    }

    private static async Task AssertSweepPagesAsync(ProgressBus bus, ManagedDatabaseRef[] live, bool resumed)
    {
        var advertisement = bus.LastAdvertisement!;
        await Assert.That(advertisement.Member).IsEqualTo("gru-a");
        await Assert.That(advertisement.Members.SequenceEqual(["gru-a"])).IsTrue();
        await Assert.That(advertisement.More).IsTrue();
        await Assert.That(advertisement.Heads.Select(head => head.DatabaseId).SequenceEqual(live.Take(6).Select(database => WanProtocol.Id(database.Id)))).IsTrue();
        await Assert.That(bus.PageRequests.Count).IsEqualTo(resumed ? 1 : 15);
        await Assert.That(bus.PageRequests.All(request => request.Member == "gru-a" && request.After.Length == 48)).IsTrue();
        ByteString[] expectedCursors = resumed ? [Cursor(live[95])] : [.. Enumerable.Range(1, 15).Select(page => Cursor(live[(page * 6) - 1]))];
        await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual(expectedCursors)).IsTrue();
        await Assert.That(bus.PageReplies.Count).IsEqualTo(resumed ? 1 : 15);
        await Assert.That(bus.PageReplies.All(reply => reply.Member == "gru-a" && reply.More == !resumed && reply.Heads.Count == (resumed ? 5 : 6))).IsTrue();
        await Assert.That(bus.PageReplies.SelectMany(reply => reply.Heads).Select(head => head.DatabaseId)
            .SequenceEqual(live.Skip(resumed ? 96 : 6).Take(resumed ? 5 : 90).Select(database => WanProtocol.Id(database.Id)))).IsTrue();
        await Assert.That(bus.PageReplies.Append(advertisement).All(reply => reply.CalculateSize() <= WanProtocol.MaxMessageBytes)).IsTrue();
        await Assert.That(bus.RequestedMembers.Count).IsEqualTo(1);
        await Assert.That(bus.ActiveHeads).IsEqualTo(0);
        await Assert.That(bus.MaxActiveHeads).IsEqualTo(1);
        await Assert.That(bus.CancelledHeads).IsEqualTo(0);
    }

    private static async Task AssertSweepInventoryAsync(
        ManagementApi api, List<ManagedDatabaseRef> databases, HashSet<string> removed, CancellationToken token)
    {
        HashSet<string> listed = [with(StringComparer.Ordinal)];
        string? after = null;
        do
        {
            using var response = await api.Client.GetAsync(ManagementApi.Uri(databases[0].ProjectPath + "/databases?limit=100"
                + (after is null ? "" : "&after=" + Uri.EscapeDataString(after))), token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            foreach (var record in body.GetProperty("items").EnumerateArray())
            {
                await Assert.That(listed.Add(record.GetProperty("id").GetString()!)).IsTrue();
            }

            after = body.GetProperty("next").GetString();
        }
        while (after is not null);

        await Assert.That(listed.Count).IsEqualTo(databases.Count - removed.Count);
        await Assert.That(listed.SetEquals(databases.Where(database => !removed.Contains(database.Id)).Select(database => database.Id))).IsTrue();
        foreach (var database in databases)
        {
            var file = Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db");
            await Assert.That(File.Exists(file)).IsEqualTo(!removed.Contains(database.Id));
        }
    }
}
