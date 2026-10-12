using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Durability and recovery with regions as separate operating-system processes, killed with SIGKILL at fixed points of
/// the replication path (docs/terrabase/multi-region-replication.md sections 7 to 10 and 18):
/// <list type="bullet">
/// <item>after a local commit, before it ships: every outbound link of the writer is cut while its peers keep writing to it;</item>
/// <item>after a batch shipped, before its ack: the replies of one link are held, so the receiver applies and acks but the sender never hears it;</item>
/// <item>on the receiver between the two transactions of one batch, and with every durable sync slowed past the sender's
/// progress timeout: <c>strace</c> fault injection on the receiver's write-ahead log;</item>
/// <item>a region down while thousands of writes accumulate in the others;</item>
/// <item>a power loss: the writer's write-ahead log cut back to a size taken before writes its peers already hold, and
/// before writes nobody else holds.</item>
/// </list>
/// Each region has its own view of the machine list with its own TCP hop to every peer (the shared harness has one list
/// for all; see the hook request in the report). Every scenario asserts the exact listing (count and digest of canonical
/// documents) and the exact counter in every region, so a lost, partial or duplicated group fails it. Local only.
/// </summary>
public sealed class DurabilityFaultTests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];
    private static readonly string[] GruPeers = [Cluster.Sjc, Cluster.Fra];
    private static readonly string[] Writers = [Cluster.Gru, Cluster.Sjc];
    private const string SecondCounter = "second-counter";

    // strace: SIGKILL at the entry of the first sync of the traced file; every sync of it delayed by 20 s.
    private static readonly string[] KillAtFirstSync = ["-e", "trace=pwrite64,pwritev,fsync,fdatasync", "-e", "inject=fsync,fdatasync:signal=SIGKILL:when=1"];
    private static readonly string[] SlowSyncs = ["-e", "trace=fsync,fdatasync", "-e", "inject=fsync,fdatasync:delay_enter=20000000"];

    // A power loss and the anti-entropy that repairs it: two differing rounds 60 s apart, plus a round's own time
    // (design section 10).
    private static readonly TimeSpan RepairDeadline = TimeSpan.FromSeconds(300);

    // gru's outbound links are cut, so its writes commit and are acknowledged but cannot ship; sjc and fra keep writing,
    // and gru keeps receiving their groups, so its turso_cdc interleaves received rows with its unsent ones. gru is killed
    // with SIGKILL, its links come back and it starts again: every acknowledged write of every region is in every region
    // exactly once, no stream stays behind (send and ack lag 0 everywhere) and gru's tail lag goes back to 0.
    [Test]
    public async Task Writes_committed_but_not_shipped_when_their_region_is_SIGKILLed_reach_every_region_after_its_restart()
    {
        Skip.When(Cluster.FlyRequested, "kills local region processes");
        await using var regions = await Regions.StartAsync(All);
        var expected = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.SeedCounterAsync(expected);
        regions.Link(Cluster.Gru, Cluster.Sjc).Cut = true;
        regions.Link(Cluster.Gru, Cluster.Fra).Cut = true;

        var written = await Task.WhenAll(
            regions.WriteAsync(Cluster.Gru, 200, 20, expected),
            regions.WriteAsync(Cluster.Sjc, 100, 20, expected),
            regions.WriteAsync(Cluster.Fra, 100, 20, expected));
        var (gruWrites, peerWrites) = (written[0], written[1].Concat(written[2]).ToHashSet(StringComparer.Ordinal));
        Regions.Add(expected, 60);

        // The fault point: gru holds everything its peers wrote (received groups between its own unsent ones), and no
        // peer holds anything gru wrote.
        _ = await Converge.UntilAsync(() => regions.ListAsync(Cluster.Gru), listing => peerWrites.IsSubsetOf(listing.Keys), "every peer write on gru");
        foreach (var peer in GruPeers)
        {
            var listing = await regions.ListAsync(peer);
            await Assert.That(gruWrites.Overlaps(listing.Keys)).IsFalse().Because($"gru's writes cannot have shipped to {peer}");
        }

        await regions.KillAsync(Cluster.Gru);
        regions.Link(Cluster.Gru, Cluster.Sjc).Cut = false;
        regions.Link(Cluster.Gru, Cluster.Fra).Cut = false;
        await regions.StartAgainAsync(Cluster.Gru);

        await regions.ConvergedAsync(expected, "every acknowledged write after gru's SIGKILL with unsent writes");
        await regions.StreamsCaughtUpAsync(All);
        _ = await regions.TailLagZeroAsync(Cluster.Gru);
        Console.WriteLine($"unshipped SIGKILL: {gruWrites.Count} unsent gru writes, {peerWrites.Count} peer writes, counter 60: exact in every region");
    }

    // gru's link to sjc holds sjc's replies, so sjc applies gru's batch and acknowledges it, but gru never hears the ack:
    // its tail lag grows. gru is killed with SIGKILL and started again; it sends the batch again from the ack it persisted,
    // sjc joins it a second time, and every region still holds each write once: the batch's inc of 5 counts 5. No stream
    // stays behind and gru's tail lag goes back to 0.
    [Test]
    public async Task A_batch_applied_by_its_receiver_but_not_acknowledged_before_its_sender_is_SIGKILLed_is_sent_again_and_counted_once()
    {
        Skip.When(Cluster.FlyRequested, "kills local region processes");
        await using var regions = await Regions.StartAsync(All);
        var expected = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.SeedCounterAsync(expected);
        regions.Link(Cluster.Gru, Cluster.Sjc).HoldReplies = true;

        await regions.TransactAsync(Cluster.Gru, "held", 200, 5, expected);
        Regions.Add(expected, 5);
        foreach (var region in GruPeers)
        {
            _ = await regions.UntilListedAsync(region, expected, $"gru's batch applied on {region}");
        }

        // The fault point: sjc applied the batch, and its ack cannot have reached gru.
        _ = await regions.UntilMetricAsync(Cluster.Gru, "terrabase_replication_tail_lag_seconds", null, value => value >= 1, "gru's tail lag while sjc's ack is held");
        var merged = await regions.MetricAsync(Cluster.Sjc, "terrabase_replication_merged_groups", "peer_region=\"gru\"") ?? 0;

        await regions.KillAsync(Cluster.Gru);
        regions.Link(Cluster.Gru, Cluster.Sjc).HoldReplies = false;
        await regions.StartAgainAsync(Cluster.Gru);

        _ = await regions.UntilMetricAsync(Cluster.Sjc, "terrabase_replication_merged_groups", "peer_region=\"gru\"", value => value > merged,
            "sjc joining gru's batch again after gru's restart");
        await regions.ConvergedAsync(expected, "the re-sent batch joined once");
        await regions.StreamsCaughtUpAsync(All);
        _ = await regions.TailLagZeroAsync(Cluster.Gru);
    }

    // sjc is down while gru commits two transactions of 600 documents and one inc each, so gru sends them to sjc as one
    // batch of two groups, which sjc applies in two durable transactions (at most 1024 deltas each). sjc runs under strace,
    // which kills it with SIGKILL at the entry of its first sync of the database's write-ahead log: after the first
    // transaction's frames are written, before the second one and before any ack. Started again with no peer reaching it,
    // sjc holds exactly the first group; once reached, it holds both, each inc (one counter per transaction) counts once,
    // and no stream stays behind.
    [Test]
    public async Task A_receiver_SIGKILLed_between_the_transactions_of_one_batch_keeps_the_first_whole_and_joins_the_resent_batch_once()
    {
        Skip.When(Cluster.FlyRequested, "kills local region processes");
        Skip.When(!Strace.Available, "needs strace for its fault point (see the hook request for an in-process barrier)");
        await using var regions = await Regions.StartAsync(All);
        var expected = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.SeedCounterAsync(expected);
        await regions.SeedCounterAsync(expected, SecondCounter);
        await regions.KillAsync(Cluster.Sjc);

        // Each transaction counts on a counter of its own, so the first group joined twice would show on its counter.
        var first = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.TransactAsync(Cluster.Gru, "first", 600, 1, first);
        await regions.TransactAsync(Cluster.Gru, "second", 600, 1, expected, SecondCounter);
        foreach (var (id, body) in first)
        {
            expected[id] = body;
        }

        Regions.Add(expected, 1);
        Regions.Add(expected, 1, SecondCounter);
        _ = await regions.UntilListedAsync(Cluster.Fra, expected, "both transactions on fra");

        // strace shows the injected kill as a sync that never returns ("= ?"), after the frames written before it.
        var trace = (await regions.StartUntilInjectedKillAsync(Cluster.Sjc, KillAtFirstSync)).Split('\n');
        var killed = Array.FindIndex(trace, line => Regex.IsMatch(line, @"\b(fsync|fdatasync)\(\d+\)\s+= \?$"));
        await Assert.That(killed).IsGreaterThan(0).Because("strace killed sjc at a sync of the database's write-ahead log");
        await Assert.That(trace.Any(line => line.EndsWith("+++ killed by SIGKILL +++", StringComparison.Ordinal))).IsTrue();
        var frames = trace[..killed].Select(line => Regex.Match(line, @"^\d+ pwritev?(64)?\(.*\) = (\d+)$")).Where(match => match.Success)
            .Sum(match => long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) / 4120;
        Console.WriteLine($"fault: sjc killed at its first write-ahead log sync, after {frames} frames written since its start");

        regions.Link(Cluster.Gru, Cluster.Sjc).Cut = true;
        regions.Link(Cluster.Fra, Cluster.Sjc).Cut = true;
        await regions.StartAgainAsync(Cluster.Sjc);
        var halfway = new Dictionary<string, string>(regions.Seeded(), StringComparer.Ordinal);
        foreach (var (id, body) in first)
        {
            halfway[id] = body;
        }

        halfway[Regions.Counter] = """{"n":1}""";
        halfway[SecondCounter] = """{"n":0}""";
        var listing = await regions.ListAsync(Cluster.Sjc);
        await Assert.That(Docs.Digest(listing.Select(pair => (pair.Key, pair.Value)))).IsEqualTo(Docs.Digest(halfway.Select(pair => (pair.Key, pair.Value))))
            .Because($"sjc holds exactly the first group after the kill; it lists {listing.Count} documents, {listing.Keys.Count(id => id.StartsWith("second", StringComparison.Ordinal))} of the second");

        regions.Link(Cluster.Gru, Cluster.Sjc).Cut = false;
        regions.Link(Cluster.Fra, Cluster.Sjc).Cut = false;
        await regions.ConvergedAsync(expected, "the batch joined again after sjc's restart");
        await regions.StreamsCaughtUpAsync(All);
    }

    // sjc's every sync of the database's write-ahead log takes 20 s (strace delay injection), longer than the sender's 15 s
    // progress timeout: gru gives up on a batch, reconnects and sends it again while sjc still applies it. gru writes ten
    // transactions with an inc each; every region ends with each write and the counter at exactly 10. Once sjc runs without
    // the delay, no stream stays behind.
    [Test]
    public async Task Writes_replicated_through_a_receiver_whose_syncs_outlast_the_progress_timeout_converge_exactly_once()
    {
        Skip.When(Cluster.FlyRequested, "slows a local region's disk");
        Skip.When(!Strace.Available, "needs strace to slow the receiver's syncs (see the hook request for a storage delay hook)");
        await using var regions = await Regions.StartAsync(All);
        var expected = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.SeedCounterAsync(expected);
        await regions.KillAsync(Cluster.Sjc);
        await regions.StartAgainAsync(Cluster.Sjc, SlowSyncs);

        for (var transaction = 0; transaction < 10; transaction++)
        {
            await regions.TransactAsync(Cluster.Gru, $"slow{transaction}", 20, 1, expected);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        Regions.Add(expected, 10);
        await regions.ConvergedAsync(expected, "writes through 20 s syncs on sjc", RepairDeadline);
        await Assert.That(regions.Trace(Cluster.Sjc)).Contains("(DELAYED)");
        var stuck = await regions.MetricAsync(Cluster.Gru, "terrabase_replication_tail_lag_seconds", null);

        // Its disk healthy again (a restart without the delay), sjc acknowledges and every lag returns to 0.
        await regions.KillAsync(Cluster.Sjc);
        await regions.StartAgainAsync(Cluster.Sjc);
        await regions.ConvergedAsync(expected, "every write after sjc's disk is fast again");
        await regions.StreamsCaughtUpAsync(All);
        Console.WriteLine($"slow syncs: converged with gru's tail lag at {stuck} s while every sync of sjc took 20 s");
    }

    // fra is killed with SIGKILL while gru and sjc each acknowledge 3000 documents and 50 incs. While it is away, gru's and
    // sjc's tail lag grows (the metric sees the backlog). Once fra is back it converges to every write, no stream stays
    // behind, gru's and sjc's tail lag return to 0 and health is ok in every region.
    [Test]
    public async Task A_region_SIGKILLed_while_6000_writes_accumulate_rejoins_exactly_and_every_lag_returns_to_zero()
    {
        Skip.When(Cluster.FlyRequested, "kills local region processes");
        await using var regions = await Regions.StartAsync(All);
        var expected = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.SeedCounterAsync(expected);
        await regions.KillAsync(Cluster.Fra);

        var clock = Stopwatch.StartNew();
        _ = await Task.WhenAll(regions.WriteAsync(Cluster.Gru, 3000, 50, expected), regions.WriteAsync(Cluster.Sjc, 3000, 50, expected));
        Regions.Add(expected, 100);
        var writing = clock.Elapsed;
        foreach (var region in Writers)
        {
            _ = await regions.UntilMetricAsync(region, "terrabase_replication_tail_lag_seconds", null, value => value >= 1, $"{region}'s tail lag while fra is away");
        }

        _ = await regions.UntilListedAsync(Cluster.Gru, expected, "gru and sjc agree before fra returns");
        clock.Restart();
        await regions.StartAgainAsync(Cluster.Fra);
        await regions.ConvergedAsync(expected, "fra after a 6000-write backlog", TimeSpan.FromSeconds(180));
        var draining = clock.Elapsed;
        Console.WriteLine($"backlog: {expected.Count} documents, written in {writing.TotalSeconds:F1} s, fra converged in {draining.TotalSeconds:F1} s");
        await regions.StreamsCaughtUpAsync(All);
        foreach (var region in Writers)
        {
            _ = await regions.TailLagZeroAsync(region);
        }
        foreach (var region in All)
        {
            _ = await Converge.UntilAsync(() => Docs.GetJsonAsync(regions[region], new Uri("/health/replication", UriKind.Relative)),
                body => body?["status"]?.GetValue<string>() == "ok", $"replication health of {region}");
        }

    }

    // sjc is away from gru while gru writes, so gru's tail lag grows; sjc comes back and acknowledges. gru's tail lag (the
    // oldest local commit a peer heard from in the last 24 h has not acknowledged) must return to 0. Away behind a cut link
    // (the control), sjc keeps its replica id and it does. Killed with SIGKILL, sjc restarts under its next epoch, a new
    // replica id, and the old id's cursor in gru's file, heard from moments ago, never acknowledges again.
    [Test]
    [Arguments("partition")]
    [Arguments("SIGKILL")]
    public async Task A_peer_back_from_being_away_releases_its_senders_tail_lag_once_it_acknowledged(string away)
    {
        Skip.When(Cluster.FlyRequested, "cuts links and kills local region processes");
        await using var regions = await Regions.StartAsync(Cluster.Gru, Cluster.Sjc);
        var expected = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.SeedCounterAsync(expected);
        _ = await regions.TailLagZeroAsync(Cluster.Gru);
        if (away == "SIGKILL")
        {
            await regions.KillAsync(Cluster.Sjc);
        }
        else
        {
            regions.Link(Cluster.Gru, Cluster.Sjc).Cut = true;
            regions.Link(Cluster.Sjc, Cluster.Gru).Cut = true;
        }

        _ = await regions.WriteAsync(Cluster.Gru, 1, 0, expected);
        _ = await regions.UntilMetricAsync(Cluster.Gru, "terrabase_replication_tail_lag_seconds", null, value => value >= 1, "gru's tail lag while sjc is away");
        if (away == "SIGKILL")
        {
            await regions.StartAgainAsync(Cluster.Sjc);
        }
        else
        {
            regions.Link(Cluster.Gru, Cluster.Sjc).Cut = false;
            regions.Link(Cluster.Sjc, Cluster.Gru).Cut = false;
        }

        await regions.ConvergedAsync(expected, $"gru's write after sjc's {away}");
        await regions.StreamsCaughtUpAsync(Cluster.Gru, Cluster.Sjc);
        _ = await regions.TailLagZeroAsync(Cluster.Gru);
    }

    // A power loss on gru under synchronous = NORMAL (design section 18): its write-ahead log goes back to a size taken
    // (with gru stopped by SIGSTOP) before two transactions. The first one shipped and every peer holds it; the second one
    // was acknowledged while gru's links were cut, so only gru ever held it. After gru starts again: the first comes back to
    // gru through anti-entropy, the second is gone everywhere (it was never anywhere else, and nothing of it comes back
    // partly), and a write gru makes after the restart reaches every region.
    [Test]
    public async Task A_power_loss_on_a_region_loses_only_what_it_never_shipped_and_anti_entropy_restores_what_it_did()
    {
        Skip.When(Cluster.FlyRequested, "edits a local region's files");
        await using var regions = await Regions.StartAsync(All);
        var expected = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.SeedCounterAsync(expected);
        var wal = regions.Wal(Cluster.Gru);
        var (size, header) = regions.Paused(Cluster.Gru, () => (new FileInfo(wal).Length, Header(wal)));

        await regions.TransactAsync(Cluster.Gru, "shipped", 100, 3, expected);
        Regions.Add(expected, 3);
        foreach (var region in GruPeers)
        {
            _ = await regions.UntilListedAsync(region, expected, $"the shipped transaction on {region}");
        }

        regions.Link(Cluster.Gru, Cluster.Sjc).Cut = true;
        regions.Link(Cluster.Gru, Cluster.Fra).Cut = true;
        var lost = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await regions.TransactAsync(Cluster.Gru, "unshipped", 50, 4, lost);
        await regions.KillAsync(Cluster.Gru);

        // Without a checkpoint in between, the log only grew from the size taken: cutting it there is the power loss.
        await Assert.That(Header(wal)).IsEqualTo(header).Because("no checkpoint restarted gru's write-ahead log since its size was taken");
        await Assert.That(new FileInfo(wal).Length).IsGreaterThan(size);
        await using (var file = new FileStream(wal, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(size);
        }

        regions.Link(Cluster.Gru, Cluster.Sjc).Cut = false;
        regions.Link(Cluster.Gru, Cluster.Fra).Cut = false;
        await regions.StartAgainAsync(Cluster.Gru);

        // The fault took effect: gru starts without the shipped transaction. Only anti-entropy brings it back, and its first
        // repair needs a second differing round 60 s after the first.
        var restarted = await regions.ListAsync(Cluster.Gru);
        await Assert.That(restarted.Keys.Count(id => id.StartsWith("shipped", StringComparison.Ordinal))).IsEqualTo(0);
        await Assert.That(restarted[Regions.Counter]).IsEqualTo("""{"n":0}""");
        var after = $"after{EntityIds.New()}";
        await Docs.PutAsync(regions[Cluster.Gru], regions.Document(after), new JsonObject { ["written"] = "after the power loss" });
        expected[after] = Docs.Canonical(new JsonObject { ["written"] = "after the power loss" });

        await regions.ConvergedAsync(expected, "every region after gru's power loss", RepairDeadline);
        foreach (var region in All)
        {
            var listing = await regions.ListAsync(region);
            await Assert.That(lost.Keys.Any(listing.ContainsKey)).IsFalse().Because($"{region} holds nothing of the transaction only gru held");
        }
    }

    // The write-ahead log's header: its salts change when a checkpoint restarts the log.
    private static string Header(string wal)
    {
        using var file = new FileStream(wal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[32];
        file.ReadExactly(header);
        return Convert.ToHexString(header);
    }

    /// <summary>strace on this host, for fault injection into a region process's system calls.</summary>
    private static class Strace
    {
        public static bool Available { get; } = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(directory => File.Exists(Path.Combine(directory, "strace")));
    }

    /// <summary>
    /// Regions as separate processes of <see cref="RegionProcessHost"/>, as <see cref="RegionProcesses"/> runs them, except
    /// that each region reads its own machine list, which names every peer through a <see cref="Hop"/> of its own. A hop can
    /// cut the link or hold the peer's replies.
    /// </summary>
    private sealed class Regions : IAsyncDisposable
    {
        public const string Counter = "counter";
        private const string Collection = "e2e";
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);
        private readonly string _root = Directory.CreateTempSubdirectory("terrabase-durability-").FullName;
        private readonly Dictionary<string, Node> _nodes = [with(StringComparer.Ordinal)];
        private readonly Dictionary<(string From, string To), Hop> _hops = [];
        private readonly string _organization = EntityIds.New().ToString();
        private readonly string _project = EntityIds.New().ToString();
        private readonly string _database = EntityIds.New().ToString();
        private readonly ConcurrentDictionary<string, long> _lastWrite = new(StringComparer.Ordinal);
        private Dictionary<string, string> _seeded = [];

        public string DatabasePath => $"/v1/projects/{_project}/databases/canary";

        public HttpClient this[string region] => _nodes[region].Client ?? throw new InvalidOperationException($"{region} is down");

        public Uri Document(string id) => new($"{DatabasePath}/collections/{Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

        /// <summary>The way <paramref name="from"/> reaches <paramref name="to"/>.</summary>
        public Hop Link(string from, string to) => _hops[(from, to)];

        /// <summary>The database's write-ahead log on <paramref name="region"/>.</summary>
        public string Wal(string region) => Path.Combine(_nodes[region].DataDirectory, _organization, _project, _database + ".db-wal");

        /// <summary>The strace output of the region's last start under strace.</summary>
        public string Trace(string region) => File.Exists(_nodes[region].TraceFile) ? File.ReadAllText(_nodes[region].TraceFile) : "";

        public static async Task<Regions> StartAsync(params string[] names)
        {
            var regions = new Regions();
            try
            {
                foreach (var name in names)
                {
                    var directory = Path.Combine(regions._root, name);
                    var node = new Node(name, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14], Path.Combine(directory, "data"), Path.Combine(directory, "members"));
                    Directory.CreateDirectory(node.DataDirectory);
                    Directory.CreateDirectory(node.Members);
                    regions._nodes[name] = node;
                }

                foreach (var from in names)
                {
                    foreach (var to in names.Where(to => to != from))
                    {
                        regions._hops[(from, to)] = new Hop();
                    }
                }

                foreach (var node in regions._nodes.Values)
                {
                    await regions.LaunchAsync(node, strace: null);
                }

                var owner = EntityIds.New().ToString();
                foreach (var node in regions._nodes.Values)
                {
                    await regions.RegisterAsync(node, owner, [.. names.Where(other => other != node.Name)], alreadyHeld: node.Name != names[0]);
                }

                foreach (var node in regions._nodes.Values)
                {
                    _ = await Converge.UntilAsync(() => Docs.StatusAsync(regions[node.Name], new Uri($"{regions.DatabasePath}/collections", UriKind.Relative)),
                        status => status == HttpStatusCode.OK, $"the database on {node.Name}");
                }

                await Cluster.CreateCollectionAsync(regions._nodes.ToDictionary(pair => pair.Key, pair => regions[pair.Key], StringComparer.Ordinal), regions.DatabasePath, Collection);
                return regions;
            }
            catch
            {
                await regions.DisposeAsync();
                throw;
            }
        }

        /// <summary>SIGKILL: the region's process (and strace, when it runs under it) ends at once, and leaves every machine list.</summary>
        public async Task KillAsync(string region)
        {
            var node = _nodes[region];
            if (node.Process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Gone(node);
        }

        /// <summary>Starts the region again on its data, under strace with <paramref name="strace"/> when given.</summary>
        public Task StartAgainAsync(string region, string[]? strace = null) =>
            _nodes[region].Process is { HasExited: false } ? throw new InvalidOperationException($"{region} is still running") : LaunchAsync(_nodes[region], strace);

        /// <summary>
        /// Starts the region under strace limited to the database's write-ahead log, lists it for its peers, waits until the
        /// injected SIGKILL ends it, and answers strace's output.
        /// </summary>
        public async Task<string> StartUntilInjectedKillAsync(string region, string[] strace)
        {
            var node = _nodes[region];
            await LaunchAsync(node, strace);
            try
            {
                await node.Process!.WaitForExitAsync().WaitAsync(StartTimeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"{region} was not killed at its fault point within {StartTimeout.TotalSeconds:F0} s:\n{node.Output}\n{Trace(region)}");
            }

            Gone(node);
            return Trace(region);
        }

        /// <summary>Runs <paramref name="call"/> while the region's process is stopped (SIGSTOP, then SIGCONT): no write is in flight.</summary>
        public T Paused<T>(string region, Func<T> call)
        {
            var pid = _nodes[region].Process!.Id;
            Signal("STOP", pid);
            try
            {
                return call();
            }
            finally
            {
                Signal("CONT", pid);
            }
        }

        /// <summary>A counter document (<see cref="Counter"/> unless named) at 0 everywhere; the seed every expectation starts from.</summary>
        public async Task SeedCounterAsync(ConcurrentDictionary<string, string> expected, string counter = Counter)
        {
            await Docs.PutAsync(this[Cluster.Gru], Document(counter), new JsonObject { ["n"] = 0 });
            _lastWrite[Cluster.Gru] = Stopwatch.GetTimestamp();
            expected[counter] = """{"n":0}""";
            _seeded = new Dictionary<string, string>(expected, StringComparer.Ordinal);
            foreach (var region in _nodes.Keys)
            {
                _ = await UntilListedAsync(region, expected, $"the seed on {region}");
            }
        }

        public Dictionary<string, string> Seeded() => _seeded;

        /// <summary>Raises the expected counter (<see cref="Counter"/> unless named) by <paramref name="by"/>.</summary>
        public static void Add(ConcurrentDictionary<string, string> expected, int by, string counter = Counter)
        {
            var current = JsonNode.Parse(expected[counter])!["n"]!.GetValue<long>();
            expected[counter] = Docs.Canonical(new JsonObject { ["n"] = current + by });
        }

        /// <summary>
        /// <paramref name="documents"/> synthetic documents and <paramref name="incs"/> incs of the counter by 1 from
        /// <paramref name="region"/>, eight at a time; each acknowledged document joins <paramref name="expected"/>. Answers
        /// the documents' ids. Any failure fails the test: no fault is active while they are written.
        /// </summary>
        public async Task<HashSet<string>> WriteAsync(string region, int documents, int incs, ConcurrentDictionary<string, string> expected)
        {
            var faker = new Faker();
            var ids = new ConcurrentBag<string>();
            await Parallel.ForEachAsync(Enumerable.Range(0, documents + incs), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (index, token) =>
            {
                if (index >= documents)
                {
                    _ = await Docs.OpsAsync(this[region], Document(Counter), new JsonObject { ["op"] = "inc", ["path"] = "/n", ["by"] = 1 });
                    return;
                }

                var (id, body) = ($"{region}{EntityIds.New()}", ThreeRegionE2ETests.Synthetic(faker));
                await Docs.PutAsync(this[region], Document(id), body, token);
                expected[id] = Docs.Canonical(body);
                ids.Add(id);
            });
            _lastWrite[region] = Stopwatch.GetTimestamp();
            return [.. ids];
        }

        /// <summary>One transaction on <paramref name="region"/>: <paramref name="documents"/> puts with ids starting with <paramref name="prefix"/> and an inc of the counter (<see cref="Counter"/> unless named) by <paramref name="by"/>.</summary>
        public async Task TransactAsync(string region, string prefix, int documents, int by, ConcurrentDictionary<string, string> expected, string counter = Counter)
        {
            var faker = new Faker();
            var written = Enumerable.Range(0, documents).Select(_ => (Id: $"{prefix}{EntityIds.New()}", Body: ThreeRegionE2ETests.Synthetic(faker))).ToList();
            JsonArray ops =
            [
                .. written.Select(document => new JsonObject
                {
                    ["op"] = "put",
                    ["collection"] = Collection,
                    ["id"] = document.Id,
                    ["document"] = document.Body.DeepClone(),
                }),
                new JsonObject
                {
                    ["op"] = "patch",
                    ["collection"] = Collection,
                    ["id"] = counter,
                    ["ops"] = new JsonArray(new JsonObject { ["op"] = "inc", ["path"] = "/n", ["by"] = by }),
                },
            ];
            _ = await Docs.PostAsync(this[region], new Uri($"{DatabasePath}/transactions", UriKind.Relative), new JsonObject { ["ops"] = ops });
            _lastWrite[region] = Stopwatch.GetTimestamp();
            foreach (var (id, body) in written)
            {
                expected[id] = Docs.Canonical(body);
            }
        }

        /// <summary>Every document of the collection on <paramref name="region"/>, by id, in canonical form.</summary>
        public async Task<SortedDictionary<string, string>> ListAsync(string region)
        {
            var documents = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var query = cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}";
                var page = await Docs.GetJsonAsync(this[region], new Uri($"{DatabasePath}/collections/{Collection}/documents?limit=1000{query}", UriKind.Relative))
                    ?? throw new InvalidOperationException($"the collection is missing on {region}");
                foreach (var item in page["items"]!.AsArray())
                {
                    documents.Add(item!["id"]!.GetValue<string>(), Docs.Canonical(item["document"]));
                }

                cursor = page["next_cursor"]?.GetValueKind() == JsonValueKind.String ? page["next_cursor"]!.GetValue<string>() : null;
            }
            while (cursor is not null);

            return documents;
        }

        /// <summary>Until the region lists exactly <paramref name="expected"/> (count and digest).</summary>
        public Task<SortedDictionary<string, string>> UntilListedAsync(string region, IReadOnlyDictionary<string, string> expected, string what) =>
            UntilListedAsync(region, expected, what, Converge.Deadline);

        /// <summary>Until every region lists exactly <paramref name="expected"/>.</summary>
        public async Task ConvergedAsync(IReadOnlyDictionary<string, string> expected, string what, TimeSpan? deadline = null)
        {
            foreach (var region in _nodes.Keys)
            {
                _ = await UntilListedAsync(region, expected, $"{what}: {region}", deadline ?? Converge.Deadline);
            }
        }

        /// <summary>Until each region's send and ack lag towards every other region are 0: no stream is stuck.</summary>
        public async Task StreamsCaughtUpAsync(params string[] regions)
        {
            foreach (var region in regions)
            {
                foreach (var peer in regions.Where(peer => peer != region))
                {
                    _ = await UntilMetricAsync(region, "terrabase_replication_send_lag_seconds", $"peer_region=\"{peer}\"", value => value == 0, $"{region}'s send lag to {peer}");
                    _ = await UntilMetricAsync(region, "terrabase_replication_ack_lag_seconds", $"peer_region=\"{peer}\"", value => value == 0, $"{region}'s ack lag to {peer}");
                }
            }
        }

        /// <summary>
        /// Until the region's tail lag is 0: every peer acknowledged every local commit. A reading counts only 7 s after the
        /// region's last write: the gauge is whole seconds since the oldest unacknowledged commit (0 in the commit's own
        /// second) and is looked at again at most once a second after acks and every 5 s otherwise.
        /// </summary>
        public async Task<double?> TailLagZeroAsync(string region)
        {
            var (value, _) = await Converge.UntilAsync(
                async () => (await MetricAsync(region, "terrabase_replication_tail_lag_seconds", null), Stopwatch.GetElapsedTime(_lastWrite.GetValueOrDefault(region))),
                reading => reading.Item1 == 0 && reading.Item2 >= TimeSpan.FromSeconds(7), $"{region}'s tail lag");
            return value;
        }

        /// <summary>The sum of the scraped series of <paramref name="name"/> (with <paramref name="label"/> when given), or null without one.</summary>
        public async Task<double?> MetricAsync(string region, string name, string? label)
        {
            var text = await this[region].GetStringAsync(new Uri("/metrics", UriKind.Relative));
            double? sum = null;
            foreach (var line in text.Split('\n').Where(line => line.StartsWith(name, StringComparison.Ordinal) && (label is null || line.Contains(label, StringComparison.Ordinal))))
            {
                // name{labels} value [timestamp]
                var sample = line[(Math.Max(line.LastIndexOf('}'), line.IndexOf(' ', StringComparison.Ordinal) - 1) + 1)..].Trim().Split(' ')[0];
                if (double.TryParse(sample, CultureInfo.InvariantCulture, out var value))
                {
                    sum = (sum ?? 0) + value;
                }
            }

            return sum;
        }

        public Task<double?> UntilMetricAsync(string region, string name, string? label, Func<double, bool> holds, string what) =>
            Converge.UntilAsync(() => MetricAsync(region, name, label), value => value is { } sample && holds(sample), what);

        public async ValueTask DisposeAsync()
        {
            foreach (var region in _nodes.Keys)
            {
                await KillAsync(region);
                _nodes[region].Process?.Dispose();
            }

            foreach (var hop in _hops.Values)
            {
                await hop.DisposeAsync();
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A file a killed process still held: the temporary directory is left for the OS to clean.
            }
        }

        private async Task<SortedDictionary<string, string>> UntilListedAsync(string region, IReadOnlyDictionary<string, string> expected, string what, TimeSpan deadline)
        {
            var digest = Docs.Digest(expected.Select(pair => (pair.Key, pair.Value)));
            var count = expected.Count;
            var listed = await Converge.UntilAsync(async () => new Listed(await ListAsync(region)), listing => listing.Count == count && listing.Digest == digest,
                $"{what} ({count} documents, counter {expected[Counter]})", deadline);
            return listed.Documents;
        }

        private Process Start(Node node, string[]? strace)
        {
            var info = Path.Combine(_root, $"{node.Name}.info");
            File.Delete(info);
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var start = new ProcessStartInfo(strace is null ? dotnet : "strace")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (strace is not null)
            {
                // Only the database's write-ahead log is traced; -f follows every thread of the node.
                foreach (var argument in (string[])["-f", "-qq", "-o", node.TraceFile, "-P", Wal(node.Name), .. strace, dotnet])
                {
                    start.ArgumentList.Add(argument);
                }
            }

            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerraBase.E2E.dll"));
            start.ArgumentList.Add("--treenode-filter");
            start.ArgumentList.Add($"/*/*/{nameof(RegionProcessHost)}/*");
            start.ArgumentList.Add("--results-directory");
            start.ArgumentList.Add(Path.Combine(_root, $"{node.Name}.results"));
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name.StartsWith("FLY_", StringComparison.Ordinal)).ToList())
            {
                _ = start.Environment.Remove(name);
            }

            start.Environment[RegionProcessHost.InfoVariable] = info;
            start.Environment[RegionProcessHost.RegionVariable] = node.Name;
            start.Environment[RegionProcessHost.MachineVariable] = node.MachineId;
            start.Environment[RegionProcessHost.DataVariable] = node.DataDirectory;
            start.Environment[RegionProcessHost.MembersVariable] = node.Members;
            var process = Process.Start(start) ?? throw new InvalidOperationException($"the {node.Name} process did not start");
            process.OutputDataReceived += (_, line) => node.Append(line.Data);
            process.ErrorDataReceived += (_, line) => node.Append(line.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            node.Process = process;
            return process;
        }

        private async Task LaunchAsync(Node node, string[]? strace)
        {
            var process = Start(node, strace);
            var info = Path.Combine(_root, $"{node.Name}.info");
            var clock = Stopwatch.StartNew();
            while (!File.Exists(info))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"the {node.Name} process exited with {process.ExitCode}:\n{node.Output}");
                }

                if (clock.Elapsed > StartTimeout)
                {
                    throw new TimeoutException($"the {node.Name} process did not start within {StartTimeout.TotalSeconds:F0} s:\n{node.Output}");
                }

                await Task.Delay(100);
            }

            node.Client = new HttpClient(new SocketsHttpHandler { UseCookies = false })
            {
                BaseAddress = new Uri(JsonNode.Parse(await File.ReadAllTextAsync(info))!["internal"]!.GetValue<string>()),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(30),
            };

            // The node listed itself in its own list; every peer's hop to it now leads there, and each running region lists
            // it through that hop. It lists every running peer through its own hops.
            var self = (await File.ReadAllTextAsync(Path.Combine(node.Members, node.MachineId))).Split('\n')[1];
            var listener = new Uri(self);
            foreach (var other in _nodes.Values.Where(other => other != node))
            {
                Link(other.Name, node.Name).Target = new IPEndPoint(IPAddress.Parse(listener.Host), listener.Port);
                if (other.Client is not null)
                {
                    List(other, node, Link(other.Name, node.Name));
                    List(node, other, Link(node.Name, other.Name));
                }
            }
        }

        // Writes `listed` into `reader`'s machine list, reached through `hop`; written aside and moved, as the host does.
        private static void List(Node reader, Node listed, Hop hop)
        {
            var aside = Path.Combine(reader.Members, "." + listed.MachineId);
            File.WriteAllText(aside, $"{listed.Name}\n{hop.Address}");
            File.Move(aside, Path.Combine(reader.Members, listed.MachineId), overwrite: true);
        }

        // A stopped machine leaves every list, as Fly's DNS drops it; its hops' open connections go with it.
        private void Gone(Node node)
        {
            foreach (var other in _nodes.Values)
            {
                File.Delete(Path.Combine(other.Members, node.MachineId));
            }

            foreach (var hop in _hops.Where(pair => pair.Key.From == node.Name || pair.Key.To == node.Name).Select(pair => pair.Value))
            {
                hop.Reset();
            }

            node.Client?.Dispose();
            node.Client = null;
        }

        private static void Signal(string signal, int pid)
        {
            using var kill = Process.Start("kill", [$"-{signal}", pid.ToString(CultureInfo.InvariantCulture)]);
            kill.WaitForExit();
            if (kill.ExitCode != 0)
            {
                throw new InvalidOperationException($"kill -{signal} {pid} failed with {kill.ExitCode}");
            }
        }

        private async Task RegisterAsync(Node node, string owner, string[] replicas, bool alreadyHeld)
        {
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = _organization, name = $"durability-{_organization}" },
                    new { op = "set_plan", organization = _organization, plan = "enterprise" },
                    new { op = "create_project", organization = _organization, id = _project, name = "durability" },
                    new { op = "create_database", organization = _organization, project = _project, name = "canary", id = _database, replica_regions = replicas },
                },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/management/commits", UriKind.Relative))
            {
                Content = new StringContent(commit, Encoding.UTF8, "application/json"),
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.Add("X-TerraBase-User-Id", owner);
            using var response = await this[node.Name].SendAsync(request);
            if (!alreadyHeld || !await Cluster.TakenAsync(response))
            {
                await Docs.EnsureSuccessAsync(response);
            }
        }

        // A region's listing as a failure names it: its count, counter and digest.
        private sealed record Listed(SortedDictionary<string, string> Documents)
        {
            public int Count => Documents.Count;

            public string Digest { get; } = Docs.Digest(Documents.Select(pair => (pair.Key, pair.Value)));

            public override string ToString() => $"{Count} documents, counter {Documents.GetValueOrDefault(Counter, "absent")}, digest {Digest[..12]}";
        }

        private sealed class Node(string name, string machineId, string dataDirectory, string members)
        {
            private readonly StringBuilder _output = new();

            public string Name { get; } = name;

            public string MachineId { get; } = machineId;

            public string DataDirectory { get; } = dataDirectory;

            // This region's own machine list.
            public string Members { get; } = members;

            public string TraceFile => Path.Combine(Path.GetDirectoryName(DataDirectory)!, "strace.log");

            public Process? Process { get; set; }

            public HttpClient? Client { get; set; }

            public string Output
            {
                get
                {
                    lock (_output)
                    {
                        return _output.ToString();
                    }
                }
            }

            public void Append(string? line)
            {
                if (line is null)
                {
                    return;
                }

                lock (_output)
                {
                    if (_output.Length > 64 * 1024)
                    {
                        _ = _output.Remove(0, _output.Length - (32 * 1024));
                    }

                    _ = _output.AppendLine(line);
                }
            }
        }
    }

    /// <summary>
    /// One region's TCP way to one peer. <see cref="Cut"/> resets its connections and refuses new ones; <see cref="HoldReplies"/>
    /// stops forwarding what the peer sends back (the bytes wait in the socket) until it is cleared. The target follows the
    /// peer across restarts.
    /// </summary>
    private sealed class Hop : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Lock _gate = new();
        private readonly HashSet<TcpClient> _open = [];
        private readonly Task _accepting;
        private TaskCompletionSource _released = Released();
        private bool _cut;

        public Hop()
        {
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public IPEndPoint? Target { get; set; }

        public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public bool Cut
        {
            get
            {
                lock (_gate)
                {
                    return _cut;
                }
            }

            set
            {
                lock (_gate)
                {
                    _cut = value;
                }

                if (value)
                {
                    Reset();
                }
            }
        }

        public bool HoldReplies
        {
            set
            {
                lock (_gate)
                {
                    if (value && _released.Task.IsCompleted)
                    {
                        _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    else if (!value)
                    {
                        _ = _released.TrySetResult();
                    }
                }
            }
        }

        public void Reset()
        {
            List<TcpClient> open;
            lock (_gate)
            {
                open = [.. _open];
                _open.Clear();
            }

            open.ForEach(PeerLink.Abort);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            HoldReplies = false;
            Reset();
            await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _stop.Dispose();
        }

        private static TaskCompletionSource Released()
        {
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            released.SetResult();
            return released;
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (Cut || Target is null)
                {
                    PeerLink.Abort(client);
                    continue;
                }

                _ = PumpAsync(client, Target);
            }
        }

        private async Task PumpAsync(TcpClient client, IPEndPoint target)
        {
            using var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(target, _stop.Token);
                lock (_gate)
                {
                    if (_cut)
                    {
                        PeerLink.Abort(client);
                        return;
                    }

                    _open.Add(client);
                    _open.Add(upstream);
                }

                var (down, up) = (client.GetStream(), upstream.GetStream());
                await Task.WhenAny(down.CopyToAsync(up, _stop.Token), RepliesAsync(up, down));
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Cut, reset or one side gone: the machine sees a reset and reconnects.
            }
            finally
            {
                lock (_gate)
                {
                    _open.Remove(client);
                    _open.Remove(upstream);
                }

                PeerLink.Abort(client);
            }
        }

        // The peer's bytes back to the machine, each read forwarded only while replies are not held.
        private async Task RepliesAsync(NetworkStream from, NetworkStream to)
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await from.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                Task released;
                lock (_gate)
                {
                    released = _released.Task;
                }

                await released.WaitAsync(_stop.Token);
                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
        }
    }
}
