using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Features.CrossRegion;

/// <summary>
/// Answers other regions' members: the heads of the origins this member holds (every client database it committed to),
/// and an origin's change groups after a prefix, read from its journal as the in-region sender reads them. Requests only
/// read; a request this member cannot answer gets an empty reply, which the asking member counts as a failure.
/// </summary>
internal sealed partial class WanResponder(IRegionBus bus, IStorageEngine engine, TerraBaseOptions options, WanMetrics metrics, ILogger<WanResponder> logger)
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
            return request?.Version != WanProtocol.Version ? new PullReply { Status = PullReply.Types.Status.Invalid }.ToByteArray()
                : request.RequestCase switch
                {
                    WanRequest.RequestOneofCase.Heads => (await HeadsAsync(request.Heads, cancellationToken).ConfigureAwait(false)).ToByteArray(),
                    WanRequest.RequestOneofCase.Pull => (await PullAsync(request.Pull, cancellationToken).ConfigureAwait(false)).ToByteArray(),
                    _ => new PullReply { Status = PullReply.Types.Status.Invalid }.ToByteArray(),
                };
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

    private async ValueTask<HeadsReply> HeadsAsync(HeadsRequest request, CancellationToken cancellationToken)
    {
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

            if (head > 0)
            {
                reply.Heads.Add(new Head
                {
                    TenantId = WanProtocol.Id(database.OrganizationId),
                    ProjectId = WanProtocol.Id(database.ProjectId!),
                    DatabaseId = WanProtocol.Id(database.DatabaseId!),
                    Origin = ByteString.CopyFrom(origin.Span),
                    Sequence = (ulong)head,
                });
            }
        }

        return reply;
    }

    private async ValueTask<PullReply> PullAsync(PullRequest request, CancellationToken cancellationToken)
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
