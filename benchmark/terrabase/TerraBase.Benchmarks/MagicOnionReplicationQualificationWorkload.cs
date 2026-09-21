using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TerraBase.Api;
using TerraBase.Contracts;
using TerraBase.Security;
using TerraBase.Transport;
using ContractReplicationApplyOutcome = TerraBase.Contracts.ReplicationApplyOutcome;

namespace TerraBase.Benchmarks;

/// <summary>
/// Runs a retained two-process authenticated MagicOnion replication
/// qualification. The driver and both child processes use the production
/// host, durable storage engine, TLS admission, stream pull, and unary push
/// paths; no in-memory replica or mocked transport is involved.
/// </summary>
public static class MagicOnionReplicationQualificationWorkload
{
    private const string WorkloadName = "magic-onion-replication-qualification";
    private const string Service = "TerraBase.InterNode";
    private const string PushMethod = "InvokeAsync";
    private const string StreamMethod = "StreamAsync";
    private const string SourceNode = "member-a";
    private const string DestinationNode = "member-b";
    private const ulong MembershipEpoch = 7;
    private const ulong ReplicaEpoch = 9;
    private const string Origin = SourceNode;
    private const string ReadyPrefix = "READY|";
    private const string NextDiagnosticStep = "Repeat this exact receipt under the project VM or Lightcracker network-fault harness, preserving the same seed and frontier assertions, before any OCI run.";
    private static readonly EntityId Database = EntityId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", CultureInfo.InvariantCulture);
    private static readonly EntityId Tenant = EntityId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FB0", CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions RawJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns whether the qualification workload was selected.</summary>
    public static bool IsRequested(string[] args) =>
        ReadOption(args, "workload")?.Equals(WorkloadName, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Returns whether this process is being used as a child host.</summary>
    public static bool IsReplicaHostRequested(string[] args) =>
        args.Any(argument => argument.Equals("--replica-host", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the driver qualification and writes a retained receipt.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        QualificationOptions options;
        try
        {
            options = QualificationOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid MagicOnion qualification configuration: {exception.Message}");
            QualificationOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            QualificationOptions.PrintHelp();
            return 0;
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var startedAt = DateTimeOffset.UtcNow;
        var eventsPath = Path.Combine(outputDirectory, "raw-events.jsonl");
        var events = new EventLog(eventsPath);
        QualificationResult? result = null;
        string? error = null;
        try
        {
            result = await RunScenarioAsync(options, outputDirectory, events).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.ToString();
            await events.WriteAsync(new QualificationEvent(
                await events.NextAsync().ConfigureAwait(false),
                "failed",
                null,
                null,
                null,
                null,
                [],
                exception.GetType().Name,
                exception.Message)).ConfigureAwait(false);
            Console.Error.WriteLine($"MagicOnion replication qualification failed: {exception.Message}");
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }

        var receipt = new QualificationReceipt(
            error is null ? "complete" : "failed",
            startedAt,
            DateTimeOffset.UtcNow,
            options,
            result,
            eventsPath,
            Environment.CommandLine,
            outputDirectory,
            BuildEnvironmentIdentity(),
            NextDiagnosticStep,
            error);
        var receiptPath = Path.Combine(outputDirectory, "receipt.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, JsonOptions)).ConfigureAwait(false);
        var summaryPath = Path.Combine(outputDirectory, "summary.md");
        await File.WriteAllTextAsync(summaryPath, CreateSummary(receipt, receiptPath)).ConfigureAwait(false);
        Console.WriteLine($"MagicOnion qualification receipt: {receiptPath}");
        Console.WriteLine($"MagicOnion qualification summary: {summaryPath}");
        return error is null ? 0 : 1;
    }

    /// <summary>Runs one isolated TerraBase host child process.</summary>
    public static async Task<int> RunReplicaHostAsync(string[] args)
    {
        ReplicaHostOptions options;
        try
        {
            options = ReplicaHostOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid replica host configuration: {exception.Message}");
            return 2;
        }

        using var authority = X509CertificateLoader.LoadPkcs12FromFile(options.AuthorityPath, password: null);
        using var server = X509CertificateLoader.LoadPkcs12FromFile(options.ServerPath, password: null);
        using var peerClient = X509CertificateLoader.LoadPkcs12FromFile(options.PeerClientPath, password: null);
        var peerOptions = new PeerServerOptions(
            server,
            authority,
            options.NodeId,
            new Dictionary<string, MagicOnionPeerPrincipal>(StringComparer.Ordinal)
            {
                [Fingerprint(peerClient)] = new MagicOnionPeerPrincipal
                {
                    MemberId = options.PeerNodeId,
                    MembershipEpoch = MembershipEpoch,
                    AllowedDatabases = new HashSet<EntityId> { Database },
                    AllowedTenants = new HashSet<EntityId> { Tenant },
                },
            })
        {
            ReplicaEpoch = ReplicaEpoch,
            MaximumBatchRecords = 256,
            MaximumBatchBytes = 8 * 1024 * 1024,
        };
        await using var app = TerraBaseHost.Create(
            $"https://127.0.0.1:{options.Port}",
            builder =>
            {
                builder.Configuration["TERRABASE_ALLOW_SCHEMALESS"] = "true";
                builder.Configuration["TERRABASE_REGION"] = options.NodeId == SourceNode ? "GRU" : "SFO";
                builder.Configuration["TERRABASE_DATABASE_PATH"] = options.StoragePath;
                builder.Configuration["TERRABASE_DATABASE_ROUTE"] = Database.ToString();
                builder.Configuration["TERRABASE_DATABASE_TENANT"] = Tenant.ToString();
                builder.Configuration["TERRABASE_DATABASE_ULID"] = Database.ToString();
                builder.Configuration["TERRABASE_DATABASE_BACKEND"] = "storage-engine";
                builder.Configuration["TERRABASE_DATABASE_MODE"] = "explicit";
                builder.Configuration["TERRABASE_DATABASE_HOME_REGION"] = "GRU";
                builder.Configuration["TERRABASE_DATABASE_SELECTED_REGIONS"] = "GRU,SFO";
                builder.Configuration["TERRABASE_INTERNAL_DATABASE_ID"] = Database.ToString();
            },
            peerOptions);

        await app.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"{ReadyPrefix}node={options.NodeId}|port={options.Port}|storage={options.StoragePath}");
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Equals("stop", StringComparison.Ordinal))
                break;
        }

        await app.StopAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<QualificationResult> RunScenarioAsync(
        QualificationOptions options,
        string outputDirectory,
        EventLog events)
    {
        var topologyDirectory = Path.Combine(outputDirectory, "topology");
        var sourceStorage = Path.Combine(topologyDirectory, SourceNode, "data");
        var destinationStorage = Path.Combine(topologyDirectory, DestinationNode, "data");
        Directory.CreateDirectory(sourceStorage);
        Directory.CreateDirectory(destinationStorage);
        var identity = TestIdentity.Create(topologyDirectory);
        var sourcePort = AllocatePort();
        var destinationPort = AllocatePort();
        var sourceSpec = new ReplicaSpec(
            SourceNode,
            DestinationNode,
            sourcePort,
            sourceStorage,
            identity.AuthorityPath,
            identity.SourceServerPath,
            identity.DestinationClientPath,
            identity.AuthorityFingerprint,
            identity.SourceServerFingerprint,
            identity.DestinationClientFingerprint);
        var destinationSpec = new ReplicaSpec(
            DestinationNode,
            SourceNode,
            destinationPort,
            destinationStorage,
            identity.AuthorityPath,
            identity.DestinationServerPath,
            identity.SourceClientPath,
            identity.AuthorityFingerprint,
            identity.DestinationServerFingerprint,
            identity.SourceClientFingerprint);

        var sourceHost = await ReplicaProcess.StartAsync(sourceSpec, outputDirectory, generation: 1).ConfigureAwait(false);
        ReplicaProcess? destinationHost = null;
        try
        {
            destinationHost = await ReplicaProcess.StartAsync(destinationSpec, outputDirectory, generation: 1).ConfigureAwait(false);
            await events.WriteAsync(new QualificationEvent(
                await events.NextAsync().ConfigureAwait(false),
                "topology-ready",
                null,
                null,
                null,
                null,
                [],
                null,
                $"source={sourcePort};destination={destinationPort};storage={topologyDirectory}")).ConfigureAwait(false);

            await using var sourceClient = CreateClient(
                sourceHost.Endpoint,
                identity.DestinationClientPath,
                identity.AuthorityPath,
                identity.SourceServerPath,
                DestinationNode);
            await using var destinationClientBeforePartition = CreateClient(
                destinationHost.Endpoint,
                identity.SourceClientPath,
                identity.AuthorityPath,
                identity.DestinationServerPath,
                SourceNode);
            var publications = CreatePublications(options.Operations);
            var seedStarted = Stopwatch.GetTimestamp();
            var seed = await PushBatchAsync(
                sourceClient,
                publications,
                "seed",
                DateTimeOffset.UtcNow.AddSeconds(10)).ConfigureAwait(false);
            var seedMilliseconds = Stopwatch.GetElapsedTime(seedStarted).TotalMilliseconds;
            EnsureDurable(seed, "source seed");
            await events.WriteAsync(new QualificationEvent(
                await events.NextAsync().ConfigureAwait(false),
                "seed",
                SourceNode,
                seed.DurableFrontier.GetValueOrDefault(Origin),
                seed.AppliedFrontier.GetValueOrDefault(Origin),
                seed.DurableAcknowledged,
                seed.Results.Select(result => result.Outcome.ToString()).ToArray(),
                null,
                $"operations={publications.Length};elapsedMs={seedMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}")).ConfigureAwait(false);

            var pulled = await PullAsync(sourceClient, options.Operations, "seed-pull").ConfigureAwait(false);
            if (pulled.Count != publications.Length)
                throw new InvalidOperationException($"Source pull returned {pulled.Count} rows; expected {publications.Length}.");
            await sourceHost.AssertAliveAsync().ConfigureAwait(false);

            await destinationHost.StopAsync().ConfigureAwait(false);
            var partitionException = await ExpectPartitionAsync(
                destinationClientBeforePartition,
                publications[0],
                "partition-probe").ConfigureAwait(false);
            await events.WriteAsync(new QualificationEvent(
                await events.NextAsync().ConfigureAwait(false),
                "partition",
                DestinationNode,
                null,
                null,
                false,
                [],
                partitionException.GetType().Name,
                partitionException.Message)).ConfigureAwait(false);

            destinationHost = await ReplicaProcess.StartAsync(destinationSpec, outputDirectory, generation: 2).ConfigureAwait(false);
            await using var destinationClient = CreateClient(
                destinationHost.Endpoint,
                identity.SourceClientPath,
                identity.AuthorityPath,
                identity.DestinationServerPath,
                SourceNode);
            var heartbeat = await HeartbeatAsync(destinationClient, "restart-heartbeat").ConfigureAwait(false);
            if (!heartbeat.Accepted || heartbeat.AppliedFrontier.Count != 0)
                throw new InvalidOperationException("Destination restart did not restore an empty applied frontier before repair.");
            await events.WriteAsync(new QualificationEvent(
                await events.NextAsync().ConfigureAwait(false),
                "restart",
                DestinationNode,
                null,
                heartbeat.AppliedFrontier.GetValueOrDefault(Origin),
                true,
                [],
                null,
                $"generation=2;heartbeatAccepted={heartbeat.Accepted}")).ConfigureAwait(false);

            var sendLatencies = new List<double>();
            var durableAckCount = 0;
            var deferredCount = 0;
            var duplicateCount = 0;
            var firstDeferredMilliseconds = (double?)null;
            var deliveryStarted = Stopwatch.GetTimestamp();
            var durableFrontier = 0UL;
            var appliedFrontier = 0UL;
            ulong? deferredSequence = null;
            for (var index = pulled.Count - 1; index >= 1; index--)
            {
                for (var duplicate = 0; duplicate < 2; duplicate++)
                {
                    var publication = pulled[index];
                    var requestId = $"reordered-{publication.Sequence}-{duplicate}";
                    var responseStarted = Stopwatch.GetTimestamp();
                    var response = await PushAsync(destinationClient, publication, requestId, DateTimeOffset.UtcNow.AddSeconds(10)).ConfigureAwait(false);
                    sendLatencies.Add(Stopwatch.GetElapsedTime(responseStarted).TotalMilliseconds * 1_000);
                    durableAckCount += response.DurableAcknowledged ? 1 : 0;
                    deferredCount += response.Results.Count(result => result.Outcome == ContractReplicationApplyOutcome.Deferred);
                    duplicateCount += response.Results.Count(result => result.Outcome == ContractReplicationApplyOutcome.Duplicate);
                    if (response.Results.Any(result => result.Outcome == ContractReplicationApplyOutcome.Deferred))
                        firstDeferredMilliseconds ??= Stopwatch.GetElapsedTime(deliveryStarted).TotalMilliseconds;
                    if (publication.Sequence == 2 && duplicate == 0)
                        deferredSequence = response.DurableLocalSequence;
                    durableFrontier = response.DurableFrontier.GetValueOrDefault(Origin);
                    appliedFrontier = response.AppliedFrontier.GetValueOrDefault(Origin);
                    await events.WriteAsync(new QualificationEvent(
                        await events.NextAsync().ConfigureAwait(false),
                        "reordered-delivery",
                        DestinationNode,
                        durableFrontier,
                        appliedFrontier,
                        response.DurableAcknowledged,
                        response.Results.Select(result => result.Outcome.ToString()).ToArray(),
                        null,
                        $"sequence={publication.Sequence};duplicate={duplicate}")).ConfigureAwait(false);
                }
            }

            await destinationClient.DisposeAsync().ConfigureAwait(false);
            await destinationHost.DisposeAsync().ConfigureAwait(false);
            destinationHost = await ReplicaProcess.StartAsync(destinationSpec, outputDirectory, generation: 3).ConfigureAwait(false);
            await using var restartedDestinationClient = CreateClient(
                destinationHost.Endpoint,
                identity.SourceClientPath,
                identity.AuthorityPath,
                identity.DestinationServerPath,
                SourceNode);
            var restartedHeartbeat = await HeartbeatAsync(restartedDestinationClient, "restart-heartbeat-after-reorder").ConfigureAwait(false);
            if (!restartedHeartbeat.Accepted || restartedHeartbeat.AppliedFrontier.Count != 0)
                throw new InvalidOperationException("The restarted destination applied deferred rows before anti-entropy repair.");
            await events.WriteAsync(new QualificationEvent(
                await events.NextAsync().ConfigureAwait(false),
                "restart-after-reorder",
                DestinationNode,
                null,
                restartedHeartbeat.AppliedFrontier.GetValueOrDefault(Origin),
                true,
                [],
                null,
                "generation=3;deferredRowsRehydrated=true")).ConfigureAwait(false);

            var restartedDuplicate = await PushAsync(
                restartedDestinationClient,
                pulled.Single(publication => publication.Sequence == 2),
                "restart-duplicate",
                DateTimeOffset.UtcNow.AddSeconds(10)).ConfigureAwait(false);
            if (restartedDuplicate.DurableLocalSequence == 0
                || restartedDuplicate.DurableLocalSequence != deferredSequence
                || restartedDuplicate.Results.Single().Outcome is not (ContractReplicationApplyOutcome.Deferred or ContractReplicationApplyOutcome.Duplicate))
            {
                throw new InvalidOperationException("The restarted destination did not retain the deferred publication at the same durable sequence.");
            }
            durableFrontier = restartedDuplicate.DurableFrontier.GetValueOrDefault(Origin);
            appliedFrontier = restartedDuplicate.AppliedFrontier.GetValueOrDefault(Origin);
            duplicateCount += restartedDuplicate.Results.Count(result => result.Outcome == ContractReplicationApplyOutcome.Duplicate);
            await events.WriteAsync(new QualificationEvent(
                await events.NextAsync().ConfigureAwait(false),
                "restart-duplicate",
                DestinationNode,
                durableFrontier,
                appliedFrontier,
                restartedDuplicate.DurableAcknowledged,
                restartedDuplicate.Results.Select(result => result.Outcome.ToString()).ToArray(),
                null,
                $"durableLocalSequence={restartedDuplicate.DurableLocalSequence}")).ConfigureAwait(false);

            var repairPulled = await PullAsync(sourceClient, options.Operations, "anti-entropy-pull").ConfigureAwait(false);
            var repairAppliedFrontier = appliedFrontier;
            foreach (var publication in repairPulled.Where(publication => publication.Sequence > repairAppliedFrontier))
            {
                var response = await PushAsync(
                    restartedDestinationClient,
                    publication,
                    $"anti-entropy-{publication.Sequence}",
                    DateTimeOffset.UtcNow.AddSeconds(10)).ConfigureAwait(false);
                durableAckCount += response.DurableAcknowledged ? 1 : 0;
                deferredCount += response.Results.Count(result => result.Outcome == ContractReplicationApplyOutcome.Deferred);
                duplicateCount += response.Results.Count(result => result.Outcome == ContractReplicationApplyOutcome.Duplicate);
                durableFrontier = response.DurableFrontier.GetValueOrDefault(Origin);
                appliedFrontier = response.AppliedFrontier.GetValueOrDefault(Origin);
                if (response.AppliedFrontier.GetValueOrDefault(Origin) >= (ulong)options.Operations)
                    firstDeferredMilliseconds ??= Stopwatch.GetElapsedTime(deliveryStarted).TotalMilliseconds;
                await events.WriteAsync(new QualificationEvent(
                    await events.NextAsync().ConfigureAwait(false),
                    "anti-entropy-repair",
                    DestinationNode,
                    durableFrontier,
                    appliedFrontier,
                    response.DurableAcknowledged,
                    response.Results.Select(result => result.Outcome.ToString()).ToArray(),
                    null,
                    $"sequence={publication.Sequence}")).ConfigureAwait(false);
            }

            var finalHeartbeat = await HeartbeatAsync(restartedDestinationClient, "final-heartbeat").ConfigureAwait(false);
            var repairLag = firstDeferredMilliseconds is { } firstDeferred
                ? Stopwatch.GetElapsedTime(deliveryStarted).TotalMilliseconds - firstDeferred
                : (double?)null;
            if (finalHeartbeat.AppliedFrontier.GetValueOrDefault(Origin) != (ulong)options.Operations
                || durableFrontier != (ulong)options.Operations
                || appliedFrontier != (ulong)options.Operations)
            {
                throw new InvalidOperationException(
                    $"Destination did not converge: durable={durableFrontier}, applied={appliedFrontier}, heartbeat={finalHeartbeat.AppliedFrontier.GetValueOrDefault(Origin)}, expected={options.Operations}.");
            }

            await events.WriteAsync(new QualificationEvent(
                await events.NextAsync().ConfigureAwait(false),
                "complete",
                DestinationNode,
                durableFrontier,
                appliedFrontier,
                true,
                [],
                null,
                $"repairLagMs={Format(repairLag)};deferred={deferredCount};duplicates={duplicateCount}")).ConfigureAwait(false);

            return new QualificationResult(
                Database.ToString(),
                Tenant.ToString(),
                new QualificationSeedData(
                    Origin,
                    "magic-onion-qualification",
                    "document-{sequence:D4}",
                    publications.Length,
                    "sequence n requires member-a:n-1; sequence 1 has an empty context",
                    Convert.ToHexString(SHA256.HashData(publications.SelectMany(publication => publication.CanonicalBody ?? Array.Empty<byte>()).ToArray())).ToLowerInvariant()),
                new QualificationTopology(
                    SourceNode,
                    DestinationNode,
                    sourcePort,
                    destinationPort,
                    sourceStorage,
                    destinationStorage,
                    identity.AuthorityPath,
                    identity.SourceServerPath,
                    identity.DestinationServerPath,
                    identity.SourceClientPath,
                    identity.DestinationClientPath,
                    identity.AuthorityFingerprint,
                    identity.SourceServerFingerprint,
                    identity.DestinationServerFingerprint,
                    identity.SourceClientFingerprint,
                    identity.DestinationClientFingerprint,
                    sourceHost.Command,
                    destinationHost.Command),
                publications.Length,
                seed.DurableAcknowledged,
                seed.DurableFrontier.GetValueOrDefault(Origin),
                seed.AppliedFrontier.GetValueOrDefault(Origin),
                partitionException.GetType().Name,
                true,
                durableFrontier,
                appliedFrontier,
                finalHeartbeat.AppliedFrontier.GetValueOrDefault(Origin),
                deferredCount,
                duplicateCount,
                durableAckCount,
                repairPulled.Count,
                Percentile(sendLatencies, 0.50),
                Percentile(sendLatencies, 0.95),
                repairLag,
                Directory.EnumerateFiles(sourceStorage, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Where(static name => name is not null).Cast<string>().Order().ToArray(),
                Directory.EnumerateFiles(destinationStorage, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Where(static name => name is not null).Cast<string>().Order().ToArray());
        }
        finally
        {
            if (destinationHost is not null)
                await destinationHost.DisposeAsync().ConfigureAwait(false);
            await sourceHost.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Exception> ExpectPartitionAsync(
        MagicOnionInterNodeClient client,
        ReplicationPublication publication,
        string requestId)
    {
        try
        {
            await PushAsync(client, publication, requestId, DateTimeOffset.UtcNow.AddSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InterNodeTransportException or HttpRequestException or IOException)
        {
            return exception;
        }

        throw new InvalidOperationException("A push to the stopped destination unexpectedly succeeded.");
    }

    private static async ValueTask<ReplicationPushResponse> PushBatchAsync(
        MagicOnionInterNodeClient client,
        IReadOnlyList<ReplicationPublication> publications,
        string requestId,
        DateTimeOffset deadline) =>
        (await client.InvokeAsync<ReplicationPushRequest, ReplicationPushResponse>(
            InterNodeRequestContext.Mutation(Service, PushMethod, requestId, requestId + "-digest", deadline),
            new ReplicationPushRequest
            {
                DatabaseUlid = Database,
                Publications = publications.ToArray(),
                EndOfBatch = true,
            }).ConfigureAwait(false)).Value;

    private static async ValueTask<ReplicationPushResponse> PushAsync(
        MagicOnionInterNodeClient client,
        ReplicationPublication publication,
        string requestId,
        DateTimeOffset deadline) =>
        await PushBatchAsync(client, [publication], requestId, deadline).ConfigureAwait(false);

    private static async Task<List<ReplicationPublication>> PullAsync(
        MagicOnionInterNodeClient client,
        int expectedCount,
        string requestId)
    {
        var publications = new List<ReplicationPublication>(expectedCount);
        await foreach (var item in client.StreamAsync<ReplicationPullRequest, ReplicationPullResponse>(
                           InterNodeRequestContext.Read(Service, StreamMethod, DateTimeOffset.UtcNow.AddSeconds(10)),
                           new ReplicationPullRequest
                           {
                               DatabaseUlid = Database,
                               MaxPublications = expectedCount,
                               MaxBytes = 8 * 1024 * 1024,
                               IncludeTombstones = true,
                           }).ConfigureAwait(false))
        {
            publications.AddRange(item.Value.Publications);
        }

        if (publications.Count == 0)
            throw new InvalidOperationException($"MagicOnion stream {requestId} returned no publications.");
        return publications;
    }

    private static async Task<ControlHeartbeatResponse> HeartbeatAsync(
        MagicOnionInterNodeClient client,
        string requestId)
    {
        var response = await client.InvokeAsync<ControlHeartbeatRequest, ControlHeartbeatResponse>(
            InterNodeRequestContext.Read(Service, PushMethod, DateTimeOffset.UtcNow.AddSeconds(10)),
            new ControlHeartbeatRequest
            {
                NodeId = SourceNode,
                ReplicaEpoch = MembershipEpoch,
                SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ConfigureAwait(false);
        return response.Value;
    }

    private static ReplicationPublication[] CreatePublications(int operations)
    {
        var publications = new ReplicationPublication[operations];
        for (var index = 0; index < operations; index++)
        {
            var sequence = checked((ulong)index + 1);
            publications[index] = new ReplicationPublication
            {
                TenantUlid = Tenant,
                Collection = "magic-onion-qualification",
                DocumentId = $"document-{sequence:D4}",
                Origin = Origin,
                Sequence = sequence,
                Context = sequence == 1
                    ? new Dictionary<string, ulong>(StringComparer.Ordinal)
                    : new Dictionary<string, ulong>(StringComparer.Ordinal) { [Origin] = sequence - 1 },
                Hlc = checked((long)sequence),
                CanonicalBody = Encoding.UTF8.GetBytes($"{{\"sequence\":{sequence},\"seed\":\"qualification\"}}"),
            };
        }

        return publications;
    }

    private static MagicOnionInterNodeClient CreateClient(
        Uri endpoint,
        string clientPath,
        string authorityPath,
        string serverPath,
        string sourceNodeId)
    {
        var clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(clientPath, password: null);
        var authority = X509CertificateLoader.LoadPkcs12FromFile(authorityPath, password: null);
        var serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(serverPath, password: null);
        return new MagicOnionInterNodeClient(
            new MagicOnionInterNodeClientOptions(
                new AuthenticatedTlsOptions(endpoint, clientCertificate, requireClientCertificate: true)
                {
                    CheckCertificateRevocationList = false,
                },
                sourceNodeId)
            {
                TrustedServerCertificates = [authority],
                ExpectedRemoteMemberId = sourceNodeId == SourceNode ? DestinationNode : SourceNode,
                ExpectedRemoteCertificateFingerprints = [Fingerprint(serverCertificate)],
                DefaultDatabaseUlid = Database,
            });
    }

    private static void EnsureDurable(ReplicationPushResponse response, string operation)
    {
        if (!response.DurableAcknowledged)
            throw new InvalidOperationException($"The {operation} was not durably acknowledged.");
    }

    private static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateSummary(QualificationReceipt receipt, string receiptPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# TerraBase MagicOnion replication qualification");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Status: **{receipt.Status}**  ");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Receipt: `{receiptPath}`  ");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Command: `{receipt.Command}`");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Source commit: `{receipt.Environment.GitCommit ?? "unavailable"}`; dirty `{receipt.Environment.GitDirty}`; benchmark artifact SHA-256 `{receipt.Environment.BenchmarkArtifactSha256 ?? "unavailable"}`.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {receipt.Environment.FrameworkDescription}; build `{receipt.Environment.BuildConfiguration}`; {receipt.Environment.OperatingSystem}; {receipt.Environment.Architecture}; CPU `{receipt.Environment.CpuModel}`; {receipt.Environment.ProcessorCount} logical processors; GC server `{receipt.Environment.ServerGc}`, latency `{receipt.Environment.GcLatencyMode}`.");
        builder.AppendLine();
        builder.AppendLine("Topology: the driver launched two independent TerraBase benchmark child processes. `member-a` and `member-b` each ran a real HTTPS/HTTP2 MagicOnion peer host with mTLS admission and separate storage-engine WAL directories.");
        if (receipt.Result is { } seedResult)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Seed: database `{seedResult.Database}`, tenant `{seedResult.Tenant}`, origin `{seedResult.SeedData.Origin}`, collection `{seedResult.SeedData.Collection}`, sequences `{seedResult.SeedData.SequenceCount}`, body SHA-256 `{seedResult.SeedData.BodySha256}`.");
        }
        builder.AppendLine();
        builder.AppendLine("| Seed durable | Seed applied | Reordered deferred | Duplicate outcomes | Final durable | Final applied | Heartbeat applied | Repair lag (ms) |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|");
        if (receipt.Result is { } result)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {result.SeedDurableAcknowledged} | {result.SeedAppliedFrontier} | {result.DeferredResponses} | {result.DuplicateOutcomes} | {result.FinalDurableFrontier} | {result.FinalAppliedFrontier} | {result.FinalHeartbeatFrontier} | {Format(result.RepairLagMilliseconds)} |");
        }

        builder.AppendLine();
        builder.AppendLine("The partition is represented by a stopped destination process and a failed authenticated push, followed by a restart over the same destination directory. Reordering and duplicates are driver-controlled unary RPC deliveries. Anti-entropy uses a real source `StreamAsync` journal pull and replays rows against the destination frontier; the retained raw event log records every outcome.");
        builder.AppendLine();
        builder.AppendLine("Retained artifacts include both node storage directories, child stdout/stderr logs, certificates and fingerprints, `raw-events.jsonl`, and this receipt. The run does not claim WAN packet loss, cross-region latency, or production deployment behavior.");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Next diagnostic step: {NextDiagnosticStep}");
        return builder.ToString();
    }

    private static string Format(double? value) =>
        value is { } number ? number.ToString("F3", CultureInfo.InvariantCulture) : "unsupported";

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var ordered = values.OrderBy(static value => value).ToArray();
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))
                return args[index][(name.Length + 3)..];
            if (args[index].Equals($"--{name}", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1];
        }

        return null;
    }

    private static string Fingerprint(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static EnvironmentIdentity BuildEnvironmentIdentity()
    {
        var benchmarkPath = Assembly.GetExecutingAssembly().Location;
        var terraBasePath = typeof(TerraBaseHost).Assembly.Location;
        var gitRoot = FindGitRoot();
        return new EnvironmentIdentity(
            Environment.Version.ToString(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ReadCpuModel(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            benchmarkPath,
            HashFile(benchmarkPath),
            terraBasePath,
            HashFile(terraBasePath),
            gitRoot is null ? null : RunGit(gitRoot, "rev-parse", "HEAD"),
            gitRoot is not null && !string.IsNullOrWhiteSpace(RunGit(gitRoot, "status", "--porcelain")));
    }

    private static string ReadCpuModel()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                var line = File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(static value => value.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                if (line is not null)
                {
                    var separator = line.IndexOf(':');
                    return separator >= 0 ? line[(separator + 1)..].Trim() : line.Trim();
                }
            }
        }
        catch (IOException)
        {
            // The host model is optional metadata.
        }

        return "unknown";
    }

    private static string? FindGitRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }

        return null;
    }

    private static string? RunGit(string root, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            process.WaitForExit();
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? HashFile(string path) =>
        File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : null;

    private sealed class EventLog : IAsyncDisposable
    {
        private readonly StreamWriter writer;
        private long sequence;

        public EventLog(string path)
        {
            writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        }

        public ValueTask<long> NextAsync() => ValueTask.FromResult(Interlocked.Increment(ref sequence));

        public async ValueTask WriteAsync(QualificationEvent value)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(value, RawJsonOptions)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => writer.DisposeAsync();
    }

    private sealed class ReplicaProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly StreamWriter input;
        private readonly StreamWriter outputLog;
        private readonly StreamWriter errorLog;
        private bool disposed;

        private ReplicaProcess(Process process, StreamWriter input, StreamWriter outputLog, StreamWriter errorLog, Uri endpoint, string command)
        {
            this.process = process;
            this.input = input;
            this.outputLog = outputLog;
            this.errorLog = errorLog;
            Endpoint = endpoint;
            Command = command;
        }

        public Uri Endpoint { get; }
        public string Command { get; }

        public static async Task<ReplicaProcess> StartAsync(ReplicaSpec spec, string outputDirectory, int generation)
        {
            var command = BuildChildCommand(spec);
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (var argument in ChildArguments(spec))
                startInfo.ArgumentList.Add(argument);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start a replica host process.");
            var nodeLogDirectory = Path.Combine(outputDirectory, "logs");
            Directory.CreateDirectory(nodeLogDirectory);
            var outputLog = new StreamWriter(
                Path.Combine(nodeLogDirectory, $"{spec.NodeId}-generation-{generation}-stdout.log"),
                append: false,
                new UTF8Encoding(false));
            var errorLog = new StreamWriter(
                Path.Combine(nodeLogDirectory, $"{spec.NodeId}-generation-{generation}-stderr.log"),
                append: false,
                new UTF8Encoding(false));
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null)
                    return;
                outputLog.WriteLine(eventArgs.Data);
                outputLog.Flush();
                if (eventArgs.Data.StartsWith(ReadyPrefix, StringComparison.Ordinal))
                    ready.TrySetResult(true);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    errorLog.WriteLine(eventArgs.Data);
                    errorLog.Flush();
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Task? completed;
            try
            {
                completed = await Task.WhenAny(ready.Task, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                completed = null;
            }

            if (completed != ready.Task)
            {
                await StopProcessAsync(process, input: null).ConfigureAwait(false);
                await outputLog.DisposeAsync().ConfigureAwait(false);
                await errorLog.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"Replica host {spec.NodeId} did not become ready. Command: {command}");
            }

            return new ReplicaProcess(process, process.StandardInput, outputLog, errorLog, new Uri($"https://127.0.0.1:{spec.Port}"), command);
        }

        public async Task AssertAliveAsync()
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Replica host exited with code {process.ExitCode}: {Command}");
            await Task.Yield();
        }

        public async Task StopAsync()
        {
            if (process.HasExited)
                return;
            await input.WriteLineAsync("stop").ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
            await StopProcessAsync(process, input).ConfigureAwait(false);
            await outputLog.FlushAsync().ConfigureAwait(false);
            await errorLog.FlushAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                await StopAsync().ConfigureAwait(false);
                await outputLog.DisposeAsync().ConfigureAwait(false);
                await errorLog.DisposeAsync().ConfigureAwait(false);
                process.Dispose();
                input.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        private static async Task StopProcessAsync(Process process, StreamWriter? input)
        {
            if (!process.HasExited)
            {
                if (input is not null)
                {
                    try
                    {
                        await input.WriteLineAsync("stop").ConfigureAwait(false);
                        await input.FlushAsync().ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                    }
                }

                var exit = process.WaitForExitAsync();
                try
                {
                    await exit.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }

                process.WaitForExit();
            }
        }
    }

    private static string BuildChildCommand(ReplicaSpec spec) =>
        string.Join(' ', new[] { Environment.ProcessPath ?? "dotnet" }.Concat(ChildArguments(spec).Select(Quote)));

    private static IEnumerable<string> ChildArguments(ReplicaSpec spec)
    {
        yield return Assembly.GetExecutingAssembly().Location;
        yield return "--replica-host";
        yield return "--node-id";
        yield return spec.NodeId;
        yield return "--peer-node-id";
        yield return spec.PeerNodeId;
        yield return "--port";
        yield return spec.Port.ToString(CultureInfo.InvariantCulture);
        yield return "--storage";
        yield return spec.StoragePath;
        yield return "--authority";
        yield return spec.AuthorityPath;
        yield return "--server";
        yield return spec.ServerPath;
        yield return "--peer-client";
        yield return spec.PeerClientPath;
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

    private sealed class QualificationOptions
    {
        public int Operations { get; private init; } = 8;
        public string OutputDirectory { get; private init; } = Path.Combine("bench", "receipts", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-magic-onion-qualification");
        public bool ShowHelp { get; private init; }

        public static QualificationOptions Parse(string[] args)
        {
            if (args.Any(argument => argument is "--help" or "-h"))
                return new QualificationOptions { ShowHelp = true };
            return new QualificationOptions
            {
                Operations = ParseInteger(args, "operations", 3, 128),
                OutputDirectory = ReadOption(args, "output") ?? Path.Combine("bench", "receipts", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-magic-onion-qualification"),
            };
        }

        public static void PrintHelp()
        {
            Console.WriteLine($"--workload {WorkloadName}");
            Console.WriteLine("  --operations N    causal publications in the deterministic seed (default 8)");
            Console.WriteLine("  --output PATH     retained receipt/artifact directory");
        }

        private static int ParseInteger(string[] args, string name, int minimum, int maximum)
        {
            var value = ReadOption(args, name);
            if (value is null)
                return 8;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < minimum
                || parsed > maximum)
            {
                throw new ArgumentException($"--{name} must be between {minimum} and {maximum}.");
            }

            return parsed;
        }
    }

    private sealed class ReplicaHostOptions
    {
        public required string NodeId { get; init; }
        public required string PeerNodeId { get; init; }
        public required int Port { get; init; }
        public required string StoragePath { get; init; }
        public required string AuthorityPath { get; init; }
        public required string ServerPath { get; init; }
        public required string PeerClientPath { get; init; }

        public static ReplicaHostOptions Parse(string[] args)
        {
            var node = ReadRequired(args, "node-id");
            var peer = ReadRequired(args, "peer-node-id");
            if (!int.TryParse(ReadRequired(args, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw new ArgumentException("--port must be a valid TCP port.");
            return new ReplicaHostOptions
            {
                NodeId = node,
                PeerNodeId = peer,
                Port = port,
                StoragePath = Path.GetFullPath(ReadRequired(args, "storage")),
                AuthorityPath = Path.GetFullPath(ReadRequired(args, "authority")),
                ServerPath = Path.GetFullPath(ReadRequired(args, "server")),
                PeerClientPath = Path.GetFullPath(ReadRequired(args, "peer-client")),
            };
        }

        private static string ReadRequired(string[] args, string name) =>
            ReadOption(args, name) ?? throw new ArgumentException($"--{name} is required for a replica host.");
    }

    private sealed record ReplicaSpec(
        string NodeId,
        string PeerNodeId,
        int Port,
        string StoragePath,
        string AuthorityPath,
        string ServerPath,
        string PeerClientPath,
        string AuthorityFingerprint,
        string ServerFingerprint,
        string PeerClientFingerprint);

    private sealed record TestIdentity(
        string AuthorityPath,
        string SourceServerPath,
        string DestinationServerPath,
        string SourceClientPath,
        string DestinationClientPath,
        string AuthorityFingerprint,
        string SourceServerFingerprint,
        string DestinationServerFingerprint,
        string SourceClientFingerprint,
        string DestinationClientFingerprint)
    {
        public static TestIdentity Create(string directory)
        {
            var identityDirectory = Path.Combine(directory, "identity");
            Directory.CreateDirectory(identityDirectory);
            using var authority = CreateCertificate("terrabase-qualification-ca", CertificateKind.CertificateAuthority);
            using var sourceServer = CreateCertificate("terrabase-qualification-source", CertificateKind.Server, authority);
            using var destinationServer = CreateCertificate("terrabase-qualification-destination", CertificateKind.Server, authority);
            using var sourceClient = CreateCertificate("terrabase-qualification-source-client", CertificateKind.Client, authority);
            using var destinationClient = CreateCertificate("terrabase-qualification-destination-client", CertificateKind.Client, authority);
            var authorityPath = SaveCertificate(authority, Path.Combine(identityDirectory, "ca.pfx"));
            var sourceServerPath = SaveCertificate(sourceServer, Path.Combine(identityDirectory, "member-a-server.pfx"));
            var destinationServerPath = SaveCertificate(destinationServer, Path.Combine(identityDirectory, "member-b-server.pfx"));
            var sourceClientPath = SaveCertificate(sourceClient, Path.Combine(identityDirectory, "member-a-client.pfx"));
            var destinationClientPath = SaveCertificate(destinationClient, Path.Combine(identityDirectory, "member-b-client.pfx"));
            return new TestIdentity(
                authorityPath,
                sourceServerPath,
                destinationServerPath,
                sourceClientPath,
                destinationClientPath,
                Fingerprint(authority),
                Fingerprint(sourceServer),
                Fingerprint(destinationServer),
                Fingerprint(sourceClient),
                Fingerprint(destinationClient));
        }

        private static string SaveCertificate(X509Certificate2 certificate, string path)
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
            return path;
        }
    }

    private static X509Certificate2 CreateCertificate(string name, CertificateKind kind, X509Certificate2? issuer = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={name}"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(kind == CertificateKind.CertificateAuthority, kind == CertificateKind.CertificateAuthority, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            kind == CertificateKind.CertificateAuthority ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        var usages = new OidCollection();
        if (kind == CertificateKind.Server)
            usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        else if (kind == CertificateKind.Client)
            usages.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        if (usages.Count > 0)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        if (kind == CertificateKind.Server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = issuer?.NotAfter.AddMinutes(-1) ?? DateTimeOffset.UtcNow.AddHours(1);
        if (issuer is null)
            return request.CreateSelfSigned(notBefore, notAfter);
        using var issued = request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    private enum CertificateKind
    {
        CertificateAuthority,
        Server,
        Client,
    }

    private sealed record QualificationEvent(
        long Sequence,
        string Phase,
        string? Node,
        ulong? DurableFrontier,
        ulong? AppliedFrontier,
        bool? DurableAcknowledged,
        IReadOnlyList<string> Outcomes,
        string? ErrorType,
        string? Detail);

    private sealed record QualificationReceipt(
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        QualificationOptions Configuration,
        QualificationResult? Result,
        string RawEventsPath,
        string Command,
        string ReceiptDirectory,
        EnvironmentIdentity Environment,
        string NextDiagnosticStep,
        string? Error);

    private sealed record QualificationResult(
        string Database,
        string Tenant,
        QualificationSeedData SeedData,
        QualificationTopology Topology,
        int Operations,
        bool SeedDurableAcknowledged,
        ulong SeedDurableFrontier,
        ulong SeedAppliedFrontier,
        string PartitionFailureType,
        bool RestartVerified,
        ulong FinalDurableFrontier,
        ulong FinalAppliedFrontier,
        ulong FinalHeartbeatFrontier,
        int DeferredResponses,
        int DuplicateOutcomes,
        int DurableAcknowledgementCount,
        int AntiEntropyPulled,
        double SendP50Microseconds,
        double SendP95Microseconds,
        double? RepairLagMilliseconds,
        IReadOnlyList<string> SourceStorageFiles,
        IReadOnlyList<string> DestinationStorageFiles);

    private sealed record QualificationSeedData(
        string Origin,
        string Collection,
        string DocumentIdPattern,
        int SequenceCount,
        string CausalContext,
        string BodySha256);

    private sealed record QualificationTopology(
        string SourceNode,
        string DestinationNode,
        int SourcePort,
        int DestinationPort,
        string SourceStoragePath,
        string DestinationStoragePath,
        string AuthorityPath,
        string SourceServerPath,
        string DestinationServerPath,
        string SourceClientPath,
        string DestinationClientPath,
        string AuthorityFingerprint,
        string SourceServerFingerprint,
        string DestinationServerFingerprint,
        string SourceClientFingerprint,
        string DestinationClientFingerprint,
        string SourceCommand,
        string DestinationCommand);

    private sealed record EnvironmentIdentity(
        string Runtime,
        string FrameworkDescription,
        string OperatingSystem,
        string Architecture,
        string CpuModel,
        int ProcessorCount,
        long AvailableMemoryBytes,
        bool ServerGc,
        string GcLatencyMode,
        string BuildConfiguration,
        string? BenchmarkArtifactPath,
        string? BenchmarkArtifactSha256,
        string? TerraBaseArtifactPath,
        string? TerraBaseArtifactSha256,
        string? GitCommit,
        bool GitDirty);
}
