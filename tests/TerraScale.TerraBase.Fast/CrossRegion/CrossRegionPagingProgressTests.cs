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

public sealed partial class CrossRegionPagingProgressTests
{
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";
    private const string GruLabel = "region=\"gru-1\"";
    private static readonly string[] InSfo = ["sfo-1"];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Incomplete_discovery_keeps_replicating_without_healing_or_faster_pages(bool fullMembership)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-progress-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var a = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var newcomer = await StartAsync(network, credentials, "gru-1", "gru", "gru-new");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = EntityIds.New().ToString();
                var project = EntityIds.New().ToString();
                var newcomerProject = EntityIds.New().ToString();
                foreach (var api in new[] { a, newcomer, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = EntityIds.New().ToString(),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = faker.Company.CompanyName() },
                            new { op = "create_project", organization, id = api == newcomer ? newcomerProject : project, name = faker.Random.AlphaNumeric(12) },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                using var sfoService = sfo.Service(sfo.User);
                using (var registered = await sfoService.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                {
                    id = EntityIds.New().ToString(),
                    ops = new[] { new { op = "create_project", organization, id = newcomerProject, name = faker.Random.AlphaNumeric(12) } },
                }, token))
                {
                    registered.EnsureSuccessStatusCode();
                }

                var databases = new List<ManagedDatabaseRef>();
                for (var index = 0; index < 3; index++)
                {
                    databases.Add(await CreatePlacedAsync(a, organization, project, "page-" + index.ToString(CultureInfo.InvariantCulture), token));
                }

                // Protocol cursors use big-endian ULIDs, which order like their text within one project.
                databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                var prefix = databases[0];
                var tail = fullMembership ? await CreatePlacedAsync(newcomer, organization, newcomerProject, "new-member", token) : databases[^1];
                using var source = await a.CreateClientAsync(organization, "documents:read", "documents:write");
                using var newSource = await newcomer.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                foreach (var database in databases)
                {
                    await ManagementApi.CreateCollectionAsync(source, database, "items");
                }

                if (fullMembership)
                {
                    await ManagementApi.CreateCollectionAsync(newSource, tail, "items");
                }

                var prefixDocument = ManagementApi.Uri(ManagementApi.DocumentPath(prefix, "items", "saved"));
                var prefixValue = faker.Lorem.Sentence();
                using var prefixWrite = await source.PutAsJsonAsync(prefixDocument, new { value = prefixValue }, token);
                await Assert.That(prefixWrite.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var prefixVersion = prefixWrite.Headers.ETag!.Tag;
                var middle = databases[1];
                var middleDocument = ManagementApi.Uri(ManagementApi.DocumentPath(middle, "items", "saved"));
                var middleValue = faker.Lorem.Sentence();
                using var middleWrite = await source.PutAsJsonAsync(middleDocument, new { value = middleValue }, token);
                await Assert.That(middleWrite.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var middleVersion = middleWrite.Headers.ETag!.Tag;
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                await AssertReadAsync(target, prefixDocument, prefixValue, prefixVersion, token);
                await AssertReadAsync(target, middleDocument, middleValue, middleVersion, token);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                var prefixFile = Path.Combine(sfo.Server.DataDirectory, organization, project, prefix.Id + ".db");
                var middleFile = Path.Combine(sfo.Server.DataDirectory, organization, project, middle.Id + ".db");
                await Assert.That(File.Exists(prefixFile)).IsTrue();
                await Assert.That(File.Exists(middleFile)).IsTrue();

                bus.Slow = true;
                if (fullMembership)
                {
                    // A's partial page plus 1023 silent peers leave all 1024 admitted identities unresolved.
                    await ExpireMembershipRoundAsync(bus, clock, token);
                    var advertisement = bus.SaturatedAdvertisement!;
                    await Assert.That(advertisement.Member).IsEqualTo("gru-a");
                    await Assert.That(advertisement.More).IsTrue();
                    await Assert.That(advertisement.Members.Count).IsEqualTo(1024);
                    await Assert.That(advertisement.Members.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1024);
                    await Assert.That(advertisement.Members.All(WanProtocol.IsMember)).IsTrue();
                    await Assert.That(advertisement.Heads.Single().DatabaseId).IsEqualTo(WanProtocol.Id(prefix.Id));
                    await Assert.That(advertisement.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(0);
                    await AssertSavedAsync(sfo, target, prefix, prefixDocument, prefixValue, prefixVersion, prefixFile, clock, token);
                    await AssertSavedAsync(sfo, target, middle, middleDocument, middleValue, middleVersion, middleFile, clock, token);
                    await AssertStaleGrowthAsync(sfo, clock, token);
                    bus.AdvertiseNewcomer = true;
                }

                var tailDocument = ManagementApi.Uri(ManagementApi.DocumentPath(tail, "items", "tail"));
                using (var absent = await target.GetAsync(tailDocument, token))
                {
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                var tailSource = fullMembership ? newSource : source;
                var tailValue = faker.Lorem.Sentence();
                string? previousTailVersion = null;
                for (var update = 0; update < 3; update++)
                {
                    var previousTailValue = tailValue;
                    tailValue += " " + faker.Lorem.Sentence();
                    using var written = await tailSource.PutAsJsonAsync(tailDocument, new { value = tailValue }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(update == 0 ? HttpStatusCode.Created : HttpStatusCode.OK);
                    var version = written.Headers.ETag!.Tag;
                    // A full scan always needs two six-second continuations. Its tail must arrive across budgets.
                    for (var round = 0; round < (fullMembership ? 1 : 2); round++)
                    {
                        if (fullMembership)
                        {
                            await ExpireMembershipRoundAsync(bus, clock, token);
                            var advertisement = bus.LastAdvertisement!;
                            await Assert.That(advertisement.Member).IsEqualTo("gru-new");
                            await Assert.That(advertisement.Members.SequenceEqual(["gru-new"])).IsTrue();
                            await Assert.That(advertisement.More).IsFalse();
                            await Assert.That(advertisement.Heads.Single().DatabaseId).IsEqualTo(WanProtocol.Id(tail.Id));
                            await Assert.That(advertisement.CalculateSize()).IsLessThanOrEqualTo(WanProtocol.MaxMessageBytes);
                            await Assert.That(bus.Pulls.Any(pull => pull.DatabaseId == WanProtocol.Id(tail.Id))).IsTrue();
                            await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                            await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, token)).IsEqualTo(1);
                        }
                        else
                        {
                            await AdvancePagingRoundAsync(bus, clock, token);
                            await Assert.That(bus.PageRequests.Count).IsGreaterThan(0);
                            await Assert.That(bus.PageRequests.Count).IsLessThanOrEqualTo(2);
                            await Assert.That(bus.PageRequests.All(request => request.After.Length == 48)).IsTrue();
                            await Assert.That(bus.RequestedMembers.Count).IsEqualTo(1);
                            await Assert.That(bus.ActiveHeads).IsEqualTo(0);
                            await Assert.That(bus.MaxActiveHeads).IsEqualTo(1);
                            if (round == 0)
                            {
                                await Assert.That(bus.CancelledHeads).IsEqualTo(1);
                                await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual(
                                    [Cursor(databases[0]), Cursor(databases[1])])).IsTrue();
                                if (previousTailVersion is null)
                                {
                                    using var pending = await target.GetAsync(tailDocument, token);
                                    await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                                }
                                else
                                {
                                    await AssertReadAsync(target, tailDocument, previousTailValue, previousTailVersion, token);
                                }
                            }
                            else
                            {
                                await Assert.That(bus.CancelledHeads).IsEqualTo(0);
                                await Assert.That(bus.PageRequests.Select(request => request.After).SequenceEqual([Cursor(databases[1])])).IsTrue();
                                await Assert.That(bus.Pulls.Any(pull => pull.DatabaseId == WanProtocol.Id(tail.Id))).IsTrue();
                                await Assert.That(bus.ActiveAtFirstPull).IsEqualTo(0);
                                // EOF certifies this cycle only because its earlier prefix was witnessed and caught up.
                                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
                                clock.CaughtUpAt = clock.GetUtcNow();
                            }
                        }

                        await AssertSavedAsync(sfo, target, prefix, prefixDocument, prefixValue, prefixVersion, prefixFile, clock, token);
                        await AssertSavedAsync(sfo, target, middle, middleDocument, middleValue, middleVersion, middleFile, clock, token);
                        await AssertStaleGrowthAsync(sfo, clock, token);
                    }

                    await AssertReadAsync(target, tailDocument, tailValue, version, token);
                    previousTailVersion = version;
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

    private static async Task ExpireMembershipRoundAsync(ProgressBus bus, ProgressClock clock, CancellationToken token)
    {
        bus.NextRound();
        clock.Advance(clock.NextInterval);
        clock.NextInterval = WanPuller.Interval;
        var pending = new List<PageCall>();
        for (var index = 0; index < 8; index++)
        {
            pending.Add(await bus.Pages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token));
        }

        await Assert.That(bus.ActiveHeads).IsEqualTo(8);
        await Assert.That(bus.MaxActiveHeads).IsEqualTo(8);
        await Assert.That(bus.PageRequests.Count).IsEqualTo(8);
        await Assert.That(bus.PageRequests.Select(request => request.Member).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(8);
        await Assert.That(bus.PageRequests.All(request => request.Member == "gru-a" || request.Member.StartsWith("gru-silent-", StringComparison.Ordinal))).IsTrue();
        await Assert.That(bus.RequestedMembers.Count).IsLessThanOrEqualTo(1024);
        await Assert.That(bus.CancelledHeads).IsEqualTo(0);
        await Assert.That(bus.Pulls.Count).IsEqualTo(0);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.WhenAll(pending.Select(call => call.Cancelled.Task)).WaitAsync(TimeSpan.FromSeconds(30), token);
        await Assert.That(bus.ActiveHeads).IsEqualTo(8);
        await Assert.That(bus.CancelledHeads).IsEqualTo(8);
        await Assert.That(bus.Pulls.Count).IsEqualTo(0);
        foreach (var call in pending)
        {
            call.Join.TrySetResult();
        }

        await clock.PausedAsync(token);
        await Assert.That(bus.ActiveHeads).IsEqualTo(0);
        await Assert.That(bus.PageRequests.Count).IsEqualTo(8);
    }

    private static async Task AdvancePagingRoundAsync(ProgressBus bus, ProgressClock clock, CancellationToken token)
    {
        bus.NextRound();
        clock.Advance(clock.NextInterval);
        clock.NextInterval = WanPuller.Interval;
        var started = clock.GetUtcNow();
        var pause = clock.PausedAsync(token);
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
                    // Join the unused channel read so it cannot consume the following round's page.
                }

                await pause;
                return;
            }

            var call = await next;
            var remaining = TimeSpan.FromSeconds(10) - (clock.GetUtcNow() - started);
            if (remaining < TimeSpan.FromSeconds(6))
            {
                clock.Advance(remaining);
                await call.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                await Assert.That(bus.ActiveHeads).IsEqualTo(1);
                await Assert.That(bus.Pulls.Count).IsEqualTo(0);
                call.Join.TrySetResult();
                await pause.WaitAsync(TimeSpan.FromSeconds(30), token);
                return;
            }

            clock.Advance(TimeSpan.FromSeconds(6));
            call.Release.TrySetResult();
        }
    }

    private static ByteString Cursor(ManagedDatabaseRef database) =>
        ByteString.CopyFrom([.. WanProtocol.Id(database.Organization).Span, .. WanProtocol.Id(database.Project).Span, .. WanProtocol.Id(database.Id).Span]);

    private static async Task AssertSavedAsync(
        ManagementApi api, HttpClient target, ManagedDatabaseRef database, Uri document, string value, string version, string file,
        ProgressClock clock, CancellationToken token)
    {
        await AssertReadAsync(target, document, value, version, token);
        using var saved = await api.Client.GetAsync(ManagementApi.Uri(database.Path), token);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(File.Exists(file)).IsTrue();
        // Incomplete rounds keep the preceding complete cycle's exact staleness baseline.
        await Assert.That(await api.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token))
            .IsEqualTo((clock.GetUtcNow() - clock.CaughtUpAt).TotalSeconds);
    }

    private static async Task AssertStaleGrowthAsync(ManagementApi api, ProgressClock clock, CancellationToken token)
    {
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(await api.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token))
            .IsEqualTo((clock.GetUtcNow() - clock.CaughtUpAt).TotalSeconds);
        // Leave the remaining half-interval to the next driver.
        clock.NextInterval = TimeSpan.FromMilliseconds(500);
    }

    private static Task<ManagementApi> StartAsync(
        FakeRegionNetwork network, string credentials, string region, string cell, string node, IRegionBus? bus = null, ProgressClock? clock = null, Action<Task>? failedStartCleanup = null) =>
        ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = node;
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            TerraBaseTestServer.EnableConnectionDiagnostics(services);
            services.AddSingleton(bus ?? network.Join(cell, node));
            services.AddSingleton<IMemberStore>(new FakeMemberStore(TimeProvider.System));
            if (clock is not null)
            {
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

    // Many databases with few catalog commits: at most 16 database operations each, the commit limit.
    private static async Task<List<ManagedDatabaseRef>> CreatePlacedAsync(
        ManagementApi api, string organization, string project, IEnumerable<string> names, CancellationToken token,
        SuiteDiagnostics.StorageObservation? observation = null)
    {
        var output = TUnit.Core.TestContext.Current!.OutputWriter;
        var databases = new List<ManagedDatabaseRef>();
        foreach (var batch in names.Select(name => new ManagedDatabaseRef(organization, project, EntityIds.New().ToString(), name)).Chunk(16))
        {
            observation?.Phase("home.commit." + (databases.Count + batch.Length).ToString(CultureInfo.InvariantCulture));
            using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
            {
                id = EntityIds.New().ToString(),
                ops = batch.Select(database => new { op = "create_database", organization, project, name = database.Name, id = database.Id, replica_regions = InSfo }).ToArray(),
            }, token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
            databases.AddRange(batch);
            output.WriteLine($"paging inventory: seed batch completed, {databases.Count} databases created");
        }

        return databases;
    }

    private static async Task AssertReadAsync(HttpClient client, Uri document, string value, string version, CancellationToken token)
    {
        using var response = await client.GetAsync(document, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.ETag?.Tag).IsEqualTo(version);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(body.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
    }

    private sealed class ProgressClock(DateTimeOffset started) : FakeTimeProvider(started)
    {
        private readonly Channel<bool> _pauses = Channel.CreateBounded<bool>(1);

        public DateTimeOffset CaughtUpAt { get; set; } = started;

        public TimeSpan NextInterval { get; set; } = WanPuller.Interval;

        public async Task PausedAsync(CancellationToken token) =>
            _ = await _pauses.Reader.ReadAsync(token);

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

    private sealed class PageCall(string member)
    {
        public string Member { get; } = member;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Replied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Join { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Heads and pulls come from real source stores; this wrapper constrains discovery and injects test faults.
    private sealed class ProgressBus(IRegionBus inner, bool fullMembership, int pageSize = 1) : IRegionBus
    {
        private readonly List<PageCall> _calls = [];
        private int _activeHeads;
        private int _maxActiveHeads;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HeadsReply> _snapshots = new(StringComparer.Ordinal);
        private int _cancelledHeads;
        private bool _releasing;

        public TaskCompletionSource Begin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<PageCall> Pages { get; } = Channel.CreateBounded<PageCall>(8);

        public List<(string Member, ByteString After)> PageRequests { get; } = [];

        public List<HeadsReply> PageReplies { get; } = [];

        public HashSet<string> RequestedMembers { get; } = [with(StringComparer.Ordinal)];

        public List<PullRequest> Pulls { get; } = [];

        public HeadsReply? SaturatedAdvertisement { get; private set; }

        public HeadsReply? LastAdvertisement { get; private set; }

        public int ActiveHeads => Volatile.Read(ref _activeHeads);

        public int MaxActiveHeads => Volatile.Read(ref _maxActiveHeads);

        public int CancelledHeads => Volatile.Read(ref _cancelledHeads);

        public int ActiveAtFirstPull { get; private set; } = -1;

        public bool Slow
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool AdvertiseNewcomer
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool AdvertiseSilentMember
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool SilentMemberAnswers
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool MalformedFirstReply
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public Action<Head>? CorruptFirstHead
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public Action<HeadsReply>? CorruptFirstReply
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // Serves a round's heads pages from one listing of the real member, cut at each cursor, instead of asking it again
        // for every page: the responder reads every remaining database per request. Only for rounds that change nothing.
        public bool SnapshotHeads
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // While set, no member answers a heads request; pulls still reach their members.
        public bool Unanswered
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // More scattered first pages, sent after the real member's.
        public Func<IEnumerable<HeadsReply>>? Scatter
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // Answers a member's heads request itself when it returns a reply: the page is sent as is.
        public Func<string, ByteString, HeadsReply?>? Answer
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // Answers every pull of this database as UnknownOrigin while set, as a member that recreated it would.
        public ByteString? UnknownOriginOf
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // Answers every pull of this database as Ahead while set, as an origin that rolled back its journal would.
        public ByteString? AheadOf
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // Sees each pull reply before the puller does, as another stream that commits its groups first.
        public Func<PullRequest, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? BeforePullReply
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // Fails every pull of this database while set.
        public ByteString? FailPullsOf
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        // Rewrites the next continuation page once, before it is cut to the page size: it sees the responder's whole tail.
        public Action<HeadsReply>? CorruptNextPage
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public void NextRound()
        {
            _snapshots.Clear();
            PageRequests.Clear();
            PageReplies.Clear();
            Pulls.Clear();
            _calls.Clear();
            Volatile.Write(ref _maxActiveHeads, 0);
            _cancelledHeads = 0;
            ActiveAtFirstPull = -1;
        }

        public void ReleaseAll()
        {
            lock (_calls)
            {
                _releasing = true;
                foreach (var call in _calls)
                {
                    call.Release.TrySetResult();
                    call.Join.TrySetResult();
                }
            }
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(
            string subject, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Begin.Task.WaitAsync(cancellationToken);
            if (Slow && Unanswered)
            {
                yield break;
            }

            var member = AdvertiseNewcomer ? "gru-new" : "gru-a";
            var reply = await HeadsAsync(WanProtocol.MemberSubject("gru", member), request, ByteString.Empty, cancellationToken);
            reply.Members.Clear();
            reply.Members.Add(member);
            if (Slow && !AdvertiseNewcomer)
            {
                LimitPage(reply);
                if (fullMembership)
                {
                    reply.Members.AddRange(Enumerable.Range(0, 1023).Select(index => "gru-silent-" + index.ToString(CultureInfo.InvariantCulture)));
                    SaturatedAdvertisement = reply.Clone();
                }
            }

            if (Slow && AdvertiseSilentMember)
            {
                reply.Members.Add("gru-silent-0");
            }

            if (Slow && CorruptFirstHead is { } corrupt)
            {
                corrupt(reply.Heads[0]);
                CorruptFirstHead = null;
            }

            if (Slow && CorruptFirstReply is { } corruptReply)
            {
                corruptReply(reply);
                CorruptFirstReply = null;
            }

            LastAdvertisement = reply.Clone();
            if (Slow && MalformedFirstReply)
            {
                MalformedFirstReply = false;
                yield return ApplicationEnvelope.Encode((byte[])[0x0a, 0x01]); // Truncated length-delimited protobuf field, followed by real usable heads.
            }

            yield return WanProtocol.EncodeHeads(reply);
            if (Slow && Scatter is { } scatter)
            {
                foreach (var extra in scatter())
                {
                    yield return WanProtocol.EncodeHeads(extra);
                }
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            var parsed = WanRequest.Parser.ParseFrom(request.Span);
            if (parsed.Pull is { } pull)
            {
                if (Slow)
                {
                    if (Pulls.Count == 0)
                    {
                        ActiveAtFirstPull = ActiveHeads;
                    }

                    Pulls.Add(pull);
                }

                if (FailPullsOf is { } failing && pull.DatabaseId == failing)
                {
                    throw new IOException("injected pull failure");
                }

                if (AheadOf is { } ahead && pull.DatabaseId == ahead)
                {
                    return new PullReply { Status = PullReply.Types.Status.Ahead }.ToByteArray();
                }

                if (UnknownOriginOf is { } unknown && pull.DatabaseId == unknown)
                {
                    return new PullReply { Status = PullReply.Types.Status.UnknownOrigin }.ToByteArray();
                }

                var reply = await inner.RequestAsync(subject, request, cancellationToken);
                if (BeforePullReply is { } before)
                {
                    await before(pull, reply, cancellationToken);
                }

                return reply;
            }

            if (!Slow)
            {
                return await inner.RequestAsync(subject, request, cancellationToken);
            }

            if (Unanswered)
            {
                throw new IOException("injected unanswered heads request");
            }

            var member = subject[(subject.LastIndexOf('.') + 1)..];
            // Concurrent discovery requests record here; the test reads the lists only between rounds.
            lock (PageRequests)
            {
                PageRequests.Add((member, parsed.Heads.After));
                _ = RequestedMembers.Add(member);
            }

            var call = new PageCall(member);
            lock (_calls)
            {
                if (_releasing)
                {
                    throw new IOException("the test is stopping WAN discovery");
                }

                _calls.Add(call);
            }

            var active = Interlocked.Increment(ref _activeHeads);
            // Concurrent workers race to record the peak: only a larger count may replace it.
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
                if (!Pages.Writer.TryWrite(call))
                {
                    throw new InvalidOperationException("WAN discovery exceeded eight queued requests");
                }

                try
                {
                    await call.Release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _ = Interlocked.Increment(ref _cancelledHeads);
                    call.Cancelled.TrySetResult();
                    await call.Join.Task;
                    throw;
                }

                if (Answer?.Invoke(member, parsed.Heads.After) is { } fabricated)
                {
                    lock (PageRequests)
                    {
                        PageReplies.Add(fabricated);
                    }

                    call.Replied.TrySetResult();
                    return WanProtocol.EncodeHeads(fabricated);
                }

                // The silent member holds no databases: once it answers, its whole scan is one empty final page.
                var reply = SilentMemberAnswers && member == "gru-silent-0"
                    ? new HeadsReply { Member = member }
                    : await HeadsAsync(subject, request, parsed.Heads.After, cancellationToken);
                reply.Members.Clear();
                reply.Members.Add(member);
                if (parsed.Heads.After.Length > 0 && CorruptNextPage is { } corruptPage)
                {
                    CorruptNextPage = null;
                    corruptPage(reply);
                }

                LimitPage(reply);
                lock (PageRequests)
                {
                    PageReplies.Add(reply);
                }

                call.Replied.TrySetResult();
                return WanProtocol.EncodeHeads(reply);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeHeads);
            }
        }

        public Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken) =>
            inner.ServeAsync(subject, answer, cancellationToken);

        private async Task<HeadsReply> HeadsAsync(string subject, ReadOnlyMemory<byte> request, ByteString after, CancellationToken cancellationToken)
        {
            if (!SnapshotHeads)
            {
                return WanProtocol.DecodeHeads(await inner.RequestAsync(subject, request, cancellationToken));
            }

            if (!_snapshots.TryGetValue(subject, out var snapshot))
            {
                var everything = new WanRequest { Version = WanProtocol.Version, Heads = new HeadsRequest() }.ToByteArray();
                snapshot = _snapshots.GetOrAdd(subject, WanProtocol.DecodeHeads(await inner.RequestAsync(subject, everything, cancellationToken)));
            }

            // The responder's own cut: every head whose 48-byte key follows the cursor, in key order.
            var reply = snapshot.Clone();
            reply.Heads.Clear();
            reply.Heads.AddRange(snapshot.Heads.Where(head =>
                ((byte[])[.. head.TenantId.Span, .. head.ProjectId.Span, .. head.DatabaseId.Span]).AsSpan().SequenceCompareTo(after.Span) > 0));
            return reply;
        }

        private void LimitPage(HeadsReply reply)
        {
            reply.More |= reply.Heads.Count > pageSize;
            while (reply.Heads.Count > pageSize)
            {
                reply.Heads.RemoveAt(reply.Heads.Count - 1);
            }
        }
    }
}
