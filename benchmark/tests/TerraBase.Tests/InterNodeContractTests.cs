using MessagePack;
using TerraBase.Contracts;

internal static class InterNodeContractTests
{
    internal static void Run()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
        var request = new InterNodeRequestEnvelope
        {
            Operation = InterNodeOperation.ReplicationPull,
            Metadata = new RpcRequestMetadata
            {
                RequestId = "01JRPCREQUEST00000000000000",
                SourceNodeId = "member-a",
                DeadlineUnixMilliseconds = deadline,
                CancellationId = "cancel-1",
                Idempotency = new IdempotencyMetadata
                {
                    TenantUlid = "tenant-1",
                    DatabaseUlid = "database-1",
                    ClientRequestUlid = "01JRPCIDEMPOTENCY000000000",
                    RequestDigest = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
                },
            },
            Payload = new ReplicationPullRequest
            {
                DatabaseUlid = "database-1",
                ResumeFrom = new ReplicationCheckpoint
                {
                    DatabaseUlid = "database-1",
                    SourceNodeId = "member-a",
                    SourceReplicaEpoch = 7,
                    Frontier = new Dictionary<string, ulong> { ["member-a"] = 41 },
                    Cursor = "origin-a:42",
                },
                MaxPublications = 10,
                MaxBytes = 4096,
            },
        };

        var bytes = MessagePackSerializer.Serialize(request);
        var decoded = MessagePackSerializer.Deserialize<InterNodeRequestEnvelope>(bytes);
        Assert(decoded.MajorVersion == InterNodeProtocol.MajorVersion, "request major version must round-trip");
        Assert(decoded.MinorVersion == InterNodeProtocol.MinorVersion, "request minor version must round-trip");
        Assert(decoded.Operation == InterNodeOperation.ReplicationPull, "request operation must round-trip");
        Assert(decoded.Metadata.RequestId == request.Metadata.RequestId, "request identity must round-trip");
        Assert(decoded.Metadata.CancellationId == "cancel-1", "cancellation metadata must round-trip");
        Assert(decoded.Metadata.Idempotency?.ClientRequestUlid == "01JRPCIDEMPOTENCY000000000", "idempotency identity must round-trip");
        Assert(decoded.Payload is ReplicationPullRequest, "union payload must retain its concrete request type");
        var pull = (ReplicationPullRequest)decoded.Payload;
        Assert(pull.ResumeFrom?.Frontier["member-a"] == 41, "replication frontier must round-trip");
        Assert(pull.ResumeFrom?.Cursor == "origin-a:42", "replication cursor must round-trip");

        Assert(!request.Metadata.IsExpired(DateTimeOffset.UtcNow), "a future deadline must remain live");
        Assert(new RpcRequestMetadata { DeadlineUnixMilliseconds = 1 }.IsExpired(DateTimeOffset.UnixEpoch.AddMilliseconds(2)), "an elapsed deadline must be observable");

        var response = new InterNodeResponseEnvelope
        {
            Operation = InterNodeOperation.ReplicationChunk,
            Metadata = new RpcResponseMetadata
            {
                RequestId = request.Metadata.RequestId,
                Status = InterNodeResponseStatus.Succeeded,
                RespondedAtUnixMilliseconds = deadline - 1,
            },
            Payload = new ReplicationChunkResponse
            {
                TransferId = "transfer-1",
                Offset = 4096,
                Payload = [1, 2, 3],
                Digest = Enumerable.Repeat((byte)9, 32).ToArray(),
                NextOffset = 4099,
                Completed = false,
                Checkpoint = ((ReplicationPullRequest)request.Payload).ResumeFrom,
            },
        };

        var responseBytes = MessagePackSerializer.Serialize(response);
        var decodedResponse = MessagePackSerializer.Deserialize<InterNodeResponseEnvelope>(responseBytes);
        Assert(decodedResponse.Payload is ReplicationChunkResponse, "union payload must retain its concrete response type");
        var chunk = (ReplicationChunkResponse)decodedResponse.Payload;
        Assert(chunk.Offset == 4096 && chunk.NextOffset == 4099, "chunk offsets must round-trip");
        Assert(chunk.Payload.SequenceEqual([1, 2, 3]), "chunk payload must round-trip");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
