using System.Diagnostics.Metrics;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

public sealed class SenderRestoreTests
{
    [Test]
    [Arguments(100)]
    [Arguments(101)]
    public async Task A_restored_sender_cannot_reuse_an_acknowledged_prefix_even_after_replacement_writes_catch_up(int replacementHead)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
        });
        var data = Directory.CreateTempSubdirectory("terrabase-sender-restore-").FullName;
        TerraBaseTestServer? sender = null;
        var replicate = true;
        MeterListener? observer = null;
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long sent = 0;
        void ConfigureSender(Dictionary<string, string> settings)
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["data-directory"] = data;
            if (replicate)
            {
                settings["peer-addresses"] = receiver.PeerAddress!.ToString();
            }
        }

        try
        {
            sender = await TerraBaseTestServer.StartAsync(ConfigureSender);
            var tenant = await ReplicationTenant.CreateAsync(sender, "restored", "recovered");
            await tenant.BindAsync(receiver);
            var database = tenant.Database("restored");
            var databaseFile = tenant.File(sender, "restored");
            var path = tenant.Path("restored");
            var id = EntityIds.New().ToString();
            var snapshot = Path.Combine(data, "sequence-90.snapshot");
            using (var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write")))
            {
                using var collection = await writer.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = "items" });
                collection.EnsureSuccessStatusCode();
                // Group 1 created the collection; each write is the group of its sequence.
                for (var sequence = 2; sequence <= 90; sequence++)
                {
                    await PutAsync(writer, path, id, $"original-{sequence}");
                }

                await TerraBaseTestServer.ReplicatedAsync(sender);
                await SnapshotAsync(databaseFile, snapshot);
                for (var sequence = 91; sequence <= 100; sequence++)
                {
                    await PutAsync(writer, path, id, $"original-{sequence}");
                }

                await TerraBaseTestServer.ReplicatedAsync(sender);
            }

            ReadOnlyMemory<byte> origin;
            ReadOnlyMemory<byte> acknowledgedDigest;
            Ulid incarnation;
            var source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await using var local = await source.BeginTransactionAsync(write: false, deadline.Token);
                await using var remote = await target.BeginTransactionAsync(write: false, deadline.Token);
                origin = await local.GetOriginAsync(deadline.Token);
                incarnation = source.Incarnation;
                await Assert.That(await local.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(100);
                var (applied, digest) = await remote.GetAppliedFrontierAsync(origin, deadline.Token);
                await Assert.That(applied).IsEqualTo(100);
                acknowledgedDigest = digest;
                var localDigest = (await local.GetChangeDigestAsync(100, deadline.Token))!.Value;
                await Assert.That(digest.Span.SequenceEqual(localDigest.Span)).IsTrue();
            }

            await sender.DisposeAsync();
            sender = null;
            File.Copy(snapshot, databaseFile, overwrite: true);

            // Replacement groups are already durable before any reconnect. Their head equals or exceeds the peer's
            // old prefix, so an ahead-of-head check cannot detect the rollback.
            replicate = false;
            sender = await TerraBaseTestServer.StartAsync(ConfigureSender);
            source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            await Assert.That(source.Incarnation).IsEqualTo(incarnation);
            using (var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write")))
            {
                for (var sequence = 91; sequence <= replacementHead; sequence++)
                {
                    await PutAsync(writer, path, id, $"replacement-{sequence}");
                }
            }

            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await using var read = await source.BeginTransactionAsync(write: false, deadline.Token);
                await Assert.That(await read.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(replacementHead);
                await Assert.That((await read.GetOriginAsync(deadline.Token)).Span.SequenceEqual(origin.Span)).IsTrue();
                await Assert.That((await read.GetChangeDigestAsync(100, deadline.Token))!.Value.Span.SequenceEqual(acknowledgedDigest.Span)).IsFalse();
            }

            await sender.DisposeAsync();
            sender = null;
            replicate = true;
            sender = await TerraBaseTestServer.StartAsync(ConfigureSender, services => services.AddSingleton(provider =>
            {
                // Subscribe before constructing sender instruments, so even its first attempt is observed.
                var meters = provider.GetRequiredService<IMeterFactory>();
                observer = new MeterListener
                {
                    InstrumentPublished = (instrument, listener) =>
                    {
                        if (instrument.Meter.Scope == meters && instrument.Name is "terrabase.replication.sender.prefix.mismatches" or "terrabase.replication.sent.groups")
                        {
                            listener.EnableMeasurementEvents(instrument);
                        }
                    },
                };
                observer.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                {
                    if (instrument.Name == "terrabase.replication.sent.groups")
                    {
                        Interlocked.Add(ref sent, value);
                    }
                    else if (value > 0)
                    {
                        rejected.TrySetResult();
                    }
                });
                observer.Start();
                return new ReplicationMetrics(meters);
            }));
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_initialized")).IsEqualTo(1);
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(replacementHead);
            await Assert.That(Volatile.Read(ref sent)).IsEqualTo(0);
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                // Complete repair RPCs too: anti-entropy must not bypass the journal's rollback fence.
                source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
                await using var local = await source.BeginTransactionAsync(write: false, deadline.Token);
                await using var read = await target.BeginTransactionAsync(write: false, deadline.Token);
                var (applied, digest) = await read.GetAppliedFrontierAsync(origin, deadline.Token);
                await Assert.That(applied).IsEqualTo(100);
                await Assert.That(digest.Span.SequenceEqual(acknowledgedDigest.Span)).IsTrue();
                using var channel = peerTls.CreateChannel(receiver.PeerAddress!);
                var repair = new AntiEntropy.AntiEntropyClient(channel);
                var identity = new ShardIdentity
                {
                    Organization = tenant.OrganizationId,
                    Project = tenant.ProjectId,
                    Database = database.DatabaseId!,
                    Origin = ByteString.CopyFrom(origin.Span),
                };
                using var digestCall = repair.DigestAsync(identity, cancellationToken: deadline.Token);
                var remoteDigest = await digestCall.ResponseAsync;
                await Assert.That(remoteDigest.AppliedSource.Sequence).IsEqualTo(100UL);
                identity.Incarnation = remoteDigest.Incarnation;
                identity.SourceProof = new JournalProof
                {
                    Origin = identity.Origin,
                    Sequence = 100,
                    Digest = ByteString.CopyFrom((await local.GetChangeDigestAsync(100, deadline.Token))!.Value.Span),
                };
                var (targetSequence, targetDigest) = await local.GetAppliedFrontierAsync(remoteDigest.Origin.Memory, deadline.Token);
                identity.TargetProof = new JournalProof
                {
                    Origin = remoteDigest.Origin,
                    Sequence = checked((ulong)targetSequence),
                    Digest = ByteString.CopyFrom(targetDigest.Span),
                };
                using var rangeCall = repair.ReadRangeAsync(new RangeRequest { Identity = identity, Ranges = ushort.MaxValue }, cancellationToken: deadline.Token);
                var rangeRejected = await Assert.That(async () => await rangeCall.ResponseAsync).Throws<RpcException>();
                await Assert.That(rangeRejected!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
                var replacement = (await local.GetAsync("items", id, deadline.Token))!;
                var envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(ChangeGroup.Encode(
                    [new DocumentChange("items", id, replacement.Version, replacement.Json)])).Span);
                using var mergeCall = repair.MergeAsync(new StateRequest { Identity = identity, Envelope = envelope }, cancellationToken: deadline.Token);
                var mergeRejected = await Assert.That(async () => await mergeCall.ResponseAsync).Throws<RpcException>();
                await Assert.That(mergeRejected!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
                using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
                await AssertBodyAsync(reader, path, id, "original-100", deadline.Token);
            }

            // Explicit recovery creates a fresh database/copy identity and baseline, then retires the ambiguous
            // source. Reopen alone never renews the incarnation or pretends the old peer received the replacements.
            var engine = sender.Services.GetRequiredService<IStorageEngine>();
            var recovered = tenant.Database("recovered");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await Assert.That(await engine.DeleteDatabaseAsync(recovered, deadline.Token)).IsTrue();
                await Assert.That(await engine.CopyDatabaseAsync(database, recovered, deadline.Token)).IsTrue();
                var copy = (await engine.OpenDatabaseAsync(recovered, deadline.Token))!;
                await Assert.That(copy.Incarnation).IsNotEqualTo(incarnation);
                await Assert.That(await engine.DeleteDatabaseAsync(database, deadline.Token)).IsTrue();
            }

            await TerraBaseTestServer.ReplicatedAsync(sender);
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var copy = (await engine.OpenDatabaseAsync(recovered, deadline.Token))!;
                var received = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(recovered, deadline.Token))!;
                await using var local = await copy.BeginTransactionAsync(write: false, deadline.Token);
                await using var remote = await received.BeginTransactionAsync(write: false, deadline.Token);
                var copiedOrigin = await local.GetOriginAsync(deadline.Token);
                var head = await local.GetLastChangeSequenceAsync(deadline.Token);
                var (applied, digest) = await remote.GetAppliedFrontierAsync(copiedOrigin, deadline.Token);
                await Assert.That(head).IsEqualTo(1);
                await Assert.That(applied).IsEqualTo(head);
                var localDigest = (await local.GetChangeDigestAsync(head, deadline.Token))!.Value;
                await Assert.That(digest.Span.SequenceEqual(localDigest.Span)).IsTrue();
                using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
                await AssertBodyAsync(reader, tenant.Path("recovered"), id, $"replacement-{replacementHead}", deadline.Token);
                await AssertBodyAsync(reader, path, id, "original-100", deadline.Token);
            }
        }
        finally
        {
            observer?.Dispose();
            if (sender is not null)
            {
                await sender.DisposeAsync();
            }

            Directory.Delete(data, recursive: true);
        }
    }

    private static async Task PutAsync(HttpClient writer, string path, string id, string value)
    {
        using var response = await writer.PutAsJsonAsync(new Uri($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative), new { value });
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssertBodyAsync(HttpClient reader, string path, string id, string expected, CancellationToken token)
    {
        using var response = await reader.GetAsync(new Uri($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative), token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        await Assert.That(body.RootElement.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(expected);
    }

    private static async Task SnapshotAsync(string source, string destination)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path;";
        command.Parameters.AddWithValue("$path", destination);
        await command.ExecuteNonQueryAsync();
    }
}
