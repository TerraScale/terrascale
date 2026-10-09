using System.Diagnostics;
using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Errors;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Features.CrossRegion;

/// <summary>
/// Answers other regions' members: the heads of the origins this member holds (every client database it committed to),
/// and an origin's change groups after a prefix, read from its journal as the in-region sender reads them. Requests only
/// read; a request this member cannot answer gets an empty reply, which the asking member counts as a failure.
/// </summary>
internal sealed partial class WanResponder(
    IRegionBus bus, IStorageEngine engine, IMembership membership, ITenantDirectory tenants, TerraBaseOptions options, WanMetrics metrics,
    ILogger<WanResponder> logger, IUsageMeter usage)
    : BackgroundService
{
    private const int MaxGroupsPerReply = 1024;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cell = options.Wan!.Cell;
        return Task.WhenAll(
            ServeAsync(WanProtocol.HeadsSubject(cell), stoppingToken),
            ServeAsync(WanProtocol.MemberSubject(cell, options.NodeName), stoppingToken));
    }

    private async Task ServeAsync(string subject, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await bus.ServeAsync(subject, AnswerAsync, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogServeFailed(logger, error);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<byte[]> AnswerAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            var request = data.Length <= WanProtocol.MaxMessageBytes ? WanRequest.Parser.ParseFrom(data.Span) : null;
            if (request?.Pull is { } incoming && WanProtocol.Database(incoming.TenantId, incoming.ProjectId, incoming.DatabaseId) is { } receiving)
            {
                usage.RecordReplicationBytes(receiving, incoming.Region, WanUsage.Kind(incoming.TrafficKind), false, data.Length);
            }

            var result = request?.Version != WanProtocol.Version ? new PullReply { Status = PullReply.Types.Status.Invalid }.ToByteArray()
                : request.RequestCase switch
                {
                    WanRequest.RequestOneofCase.Heads => await HeadsAsync(request.Heads, data.Length, cancellationToken).ConfigureAwait(false) switch
                    {
                        HeadsReply reply => WanProtocol.EncodeHeads(reply),
                        Unavailable => [],
                    },
                    WanRequest.RequestOneofCase.Pull => await PullAsync(request.Pull, cancellationToken).ConfigureAwait(false) switch
                    {
                        PullReply reply => reply.ToByteArray(),
                        Unavailable => [],
                    },
                    _ => new PullReply { Status = PullReply.Types.Status.Invalid }.ToByteArray(),
                };
            if (request?.Pull is { } pull && WanProtocol.Database(pull.TenantId, pull.ProjectId, pull.DatabaseId) is { } database)
            {
                usage.RecordReplicationBytes(database, pull.Region, WanUsage.Kind(pull.TrafficKind), true, result.Length);
            }

            return result;
        }
        catch (InvalidProtocolBufferException)
        {
            return new PullReply { Status = PullReply.Types.Status.Invalid }.ToByteArray();
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LogAnswerFailed(logger, error);
            return [];
        }
    }

    private readonly union WanHeadsResult(HeadsReply, Unavailable);
    private readonly union WanPullResult(PullReply, Unavailable);

    private async ValueTask<WanHeadsResult> HeadsAsync(HeadsRequest request, int requestBytes, CancellationToken cancellationToken)
    {
        var catalog = await tenants.CheckCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (catalog is not Done)
        {
            return catalog switch
            {
                Unavailable error => error,
                Done => throw new UnreachableException(),
            };
        }

        // ponytail: lists and reads every database per request; track heads from commits once members hold thousands.
        var databases = new List<(byte[] Key, TenantContext Database)>();
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = WanProtocol.Key(database);
            if (key.AsSpan().SequenceCompareTo(request.After.Span) > 0)
            {
                databases.Add((key, database));
            }
        }

        databases.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        var reply = new HeadsReply { Member = options.NodeName };
        reply.Members.AddRange((await membership.RefreshAsync(cancellationToken).ConfigureAwait(false)).Members.Select(member => member.Name));
        // Reserve More's tag and value; the existing 64 KiB framing allowance also covers LZ4 expansion and its header.
        var bytes = reply.CalculateSize() + 2;
        if (bytes > WanProtocol.ReplyBudget)
        {
            throw new InvalidDataException("the heads membership exceeds its size bound");
        }

        foreach (var (_, database) in databases)
        {
            if (reply.Heads.Count == WanProtocol.MaxHeadsPerReply)
            {
                reply.More = true;
                break;
            }

            if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                continue;
            }

            ReadOnlyMemory<byte> origin;
            long head;
            var read = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
            await using (read.ConfigureAwait(false))
            {
                origin = await read.GetOriginAsync(cancellationToken).ConfigureAwait(false);
                head = await read.GetLastChangeSequenceAsync(cancellationToken).ConfigureAwait(false);
            }

            // Every database, written to or not, so other regions learn its placement as soon as it is created.
            var found = await tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false);
            if (found is Unavailable error)
            {
                // An unreadable catalog must not publish a complete inventory that implies databases were deleted.
                return error;
            }

            var record = found switch
            {
                TenantDatabaseRecord known => known,
                NotFound => null,
                Unavailable => throw new UnreachableException(),
            };
            var entry = new Head
            {
                TenantId = WanProtocol.Id(database.OrganizationId),
                ProjectId = WanProtocol.Id(database.ProjectId!),
                DatabaseId = WanProtocol.Id(database.DatabaseId!),
                Origin = ByteString.CopyFrom(origin.Span),
                Sequence = (ulong)head,
                DatabaseName = record?.Name ?? "",
                HomeRegion = record?.Placement.Home ?? "",
                ReplicaRegions = { record?.Placement.Replicas ?? [] },
                PlacementVersion = (ulong)(record?.Placement.Version ?? 0),
            };
            var entryBytes = entry.CalculateSize();
            var framedBytes = 1 + CodedOutputStream.ComputeLengthSize(entryBytes) + entryBytes;
            if (bytes + framedBytes > WanProtocol.ReplyBudget)
            {
                if (reply.Heads.Count == 0)
                {
                    throw new InvalidDataException("a head exceeds its reply size bound");
                }

                reply.More = true;
                break;
            }

            reply.Heads.Add(entry);
            bytes += framedBytes;
        }

        WanUsage.Heads(usage, request.Region, reply, requestBytes, reply.CalculateSize(), responding: true);
        return reply;
    }

    private async ValueTask<WanPullResult> PullAsync(PullRequest request, CancellationToken cancellationToken)
    {
        if (WanProtocol.Database(request.TenantId, request.ProjectId, request.DatabaseId) is not { } database
            || request.Origin.Length != 16 || request.After > long.MaxValue)
        {
            return new PullReply { Status = PullReply.Types.Status.Invalid };
        }

        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return new PullReply { Status = PullReply.Types.Status.UnknownOrigin };
        }

        // A database's data goes only to the regions its placement names (docs/cross-region.md).
        var found = await tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false);
        if (found is not TenantDatabaseRecord record)
        {
            return found switch
            {
                NotFound => new PullReply { Status = PullReply.Types.Status.NotPlaced },
                Unavailable error => error,
                TenantDatabaseRecord => throw new UnreachableException(),
            };
        }

        if (!record.Placement.Includes(request.Region))
        {
            return new PullReply { Status = PullReply.Types.Status.NotPlaced };
        }

        // One short read transaction, bounded by the reply: it ends before the reply is sent.
        var reply = new PullReply();
        var read = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            var origin = await read.GetOriginAsync(cancellationToken).ConfigureAwait(false);
            var head = await read.GetLastChangeSequenceAsync(cancellationToken).ConfigureAwait(false);
            if (!origin.Span.SequenceEqual(request.Origin.Span))
            {
                return new PullReply { Status = PullReply.Types.Status.UnknownOrigin };
            }

            var after = (long)request.After;
            if (after > head)
            {
                return new PullReply { Status = PullReply.Types.Status.Ahead };
            }

            var size = 0;
            for (var next = after; next < head && reply.Groups.Count < MaxGroupsPerReply; next++)
            {
                var preceding = await read.GetChangeDigestAsync(next, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("the local journal prefix proof is missing");
                var changes = await read.ReadChangesAsync(next + 1, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("the local journal has a gap before its committed head");
                var batch = ReplicationProtocol.CreateBatch(database, origin, next + 1, changes, preceding);
                var bytes = batch.CalculateSize();
                if (reply.Groups.Count == 0 && (request.Offset > 0 || bytes > WanProtocol.ReplyBudget))
                {
                    // A group larger than one reply travels as parts of its envelope, from the offset the receiver holds.
                    var envelope = batch.ApplicationEnvelope;
                    if (request.Offset >= envelope.Length)
                    {
                        return new PullReply { Status = PullReply.Types.Status.Invalid };
                    }

                    var room = WanProtocol.ReplyBudget - (bytes - envelope.Length);
                    batch.ApplicationEnvelope = ByteString.CopyFrom(envelope.Span.Slice((int)request.Offset, Math.Min(envelope.Length - (int)request.Offset, room)));
                    reply.Groups.Add(batch);
                    break;
                }

                if (size + bytes > WanProtocol.ReplyBudget)
                {
                    break;
                }

                reply.Groups.Add(batch);
                size += bytes;
            }
        }

        metrics.Served(reply.Groups.Count);
        return reply;
    }

    [LoggerMessage(LogLevel.Warning, "Serving cross-region replication requests failed; subscribing again")]
    private static partial void LogServeFailed(ILogger logger, Exception error);

    [LoggerMessage(LogLevel.Warning, "A cross-region replication request failed")]
    private static partial void LogAnswerFailed(ILogger logger, Exception error);
}
