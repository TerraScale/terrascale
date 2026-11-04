using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Memory;
using TerraBase.Features.Sync;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal sealed class RevisionPush(IMemoryBudget memory, TenantContext database) : IRevisionPullSink, IDisposable
{
    internal const int MaxAnswerBytes = 768 * 1024;
    private const int NoticeBytes = 1024;

    private ArrayBufferWriter<byte>? _payload;
    private IMemoryReservation? _reservation;

    public bool HasPages => _payload is not null;

    public ReplicaRevisionNotice? Notice { get; private set; }

    public ReadOnlyMemory<byte> Payload { get; private set; }

    public RevisionPullResult? Result { get; private set; }

    public static async ValueTask<RevisionPush?> BuildAsync(
        IDatabaseRevisions revisions, IMemoryBudget memory, TenantContext database, string? from, long committedAt, CancellationToken cancellationToken)
    {
        var push = new RevisionPush(memory, database);
        try
        {
            if (await revisions.PullAsync(database, from, push, cancellationToken).ConfigureAwait(false) is not { } result)
            {
                push.Dispose();
                return null;
            }

            push.Result = result;
            push.Notice = new ReplicaRevisionNotice(database.OrganizationId, database.ProjectId!, database.DatabaseId!, committedAt, result.Revision.ToString(),
                push.HasPages ? from : null);
            push.Payload = push.Seal(push.Notice);
            return push;
        }
        catch
        {
            push.Dispose();
            throw;
        }
    }

    public static bool TryRead(ReadOnlyMemory<byte> payload, out ReplicaRevisionNotice? notice, out ReadOnlyMemory<byte> answer)
    {
        notice = null;
        answer = default;
        if (payload.Length < sizeof(uint))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(payload.Span[^sizeof(uint)..]);
        if (length > NoticeBytes || length > payload.Length - sizeof(uint))
        {
            return false;
        }

        var end = payload.Length - sizeof(uint);
        try
        {
            notice = JsonSerializer.Deserialize(payload.Span[(end - (int)length)..end], TerraBaseJsonContext.Default.ReplicaRevisionNotice);
        }
        catch (JsonException)
        {
            return false;
        }

        answer = payload[..(end - (int)length)];
        return notice is not null;
    }

    public ValueTask<bool> StartAsync(RevisionPullStart start, CancellationToken cancellationToken)
    {
        var bytes = (long)start.Pages * PullAnswer.PageBytes(start.PageSize) + 64;
        if (start.Full is not null || bytes > MaxAnswerBytes
            || memory.TryReserve(MemoryLane.Bulk, bytes + NoticeBytes, database) is not { } reservation)
        {
            return ValueTask.FromResult(false);
        }

        _reservation = reservation;
        _payload = new ArrayBufferWriter<byte>((int)bytes + NoticeBytes);
        PullAnswer.WriteHeader(_payload, start);
        return ValueTask.FromResult(true);
    }

    public ValueTask WriteAsync(RevisionPageBatch batch, CancellationToken cancellationToken)
    {
        PullAnswer.WritePages(_payload!, batch);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _reservation?.Dispose();
        _reservation = null;
        _payload = null;
        Payload = default;
    }

    private ReadOnlyMemory<byte> Seal(ReplicaRevisionNotice notice)
    {
        var payload = _payload ?? new ArrayBufferWriter<byte>(NoticeBytes);
        var before = payload.WrittenCount;
        using (var json = new Utf8JsonWriter(payload))
        {
            JsonSerializer.Serialize(json, notice, TerraBaseJsonContext.Default.ReplicaRevisionNotice);
        }

        BinaryPrimitives.WriteUInt32BigEndian(payload.GetSpan(sizeof(uint)), (uint)(payload.WrittenCount - before));
        payload.Advance(sizeof(uint));
        return payload.WrittenMemory;
    }
}
