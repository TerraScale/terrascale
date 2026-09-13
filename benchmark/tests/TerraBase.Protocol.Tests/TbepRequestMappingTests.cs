// <copyright file="TbepRequestMappingTests.cs" company="TerraScale">
// TerraBase (SE-14). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol.Tests;

using System.Net.Sockets;

/// <summary>The typed client request mappings that carry acknowledgement intent.</summary>
public sealed class TbepRequestMappingTests
{
    /// <summary>Document write and delete requests preserve their requested acknowledgement level.</summary>
    [Test]
    public async Task DocumentMutationsCarryAcknowledgementToWire()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tbep-request-mapping-{Guid.NewGuid():N}.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var serving = ServeAsync(listener);

        try
        {
            await using var client = TbepClient.Connect(path);
            var document = new DocumentPut { Id = "order-1", Collection = "orders", Body = TbepValue.Null };

            var write = await client.DocumentWriteAsync(
                document,
                ExpectedVersion.Absent(),
                "operation-write",
                "retry-write",
                ack: Acknowledgement.GlobalQuorum());
            var delete = await client.DocumentDeleteAsync(
                "orders",
                "order-1",
                ExpectedVersion.Exact(1),
                "operation-delete",
                "retry-delete",
                ack: Acknowledgement.BackupAck());

            await Assert.That(write.Outcome.Variant).IsEqualTo("Committed");
            await Assert.That(delete.Outcome.Variant).IsEqualTo("Committed");

            var requests = await serving;
            await Assert.That(requests.Write.Ack).IsEqualTo(Acknowledgement.GlobalQuorum());
            await Assert.That(requests.Delete.Ack).IsEqualTo(Acknowledgement.BackupAck());
        }
        finally
        {
            listener.Dispose();
            File.Delete(path);
        }
    }

    private static async Task<CapturedRequests> ServeAsync(Socket listener)
    {
        using var socket = await listener.AcceptAsync();
        using var stream = new NetworkStream(socket, ownsSocket: true);

        var writeFrame = await TbepWire.ReadFrame(stream)
            ?? throw new InvalidOperationException("the client closed before document.write");
        var write = MessagePackSerializer.Deserialize<DocumentWriteRequest>(writeFrame.Body, TbepOptions.Strict);
        await AnswerAsync(
            stream,
            "document.write",
            writeFrame,
            new DocumentWriteResponse
            {
                Outcome = WriteOutcome.Committed(new CommitReceipt
                {
                    Sequence = 1,
                    Version = 1,
                    OperationId = write.OperationId,
                    Acknowledgement = write.Ack,
                }),
            }).ConfigureAwait(false);

        var deleteFrame = await TbepWire.ReadFrame(stream)
            ?? throw new InvalidOperationException("the client closed before document.delete");
        var delete = MessagePackSerializer.Deserialize<DocumentDeleteRequest>(deleteFrame.Body, TbepOptions.Strict);
        await AnswerAsync(
            stream,
            "document.delete",
            deleteFrame,
            new DocumentDeleteResponse
            {
                Outcome = WriteOutcome.Committed(new CommitReceipt
                {
                    Sequence = 2,
                    Version = 0,
                    OperationId = delete.OperationId,
                    Acknowledgement = delete.Ack,
                }),
            }).ConfigureAwait(false);

        return new CapturedRequests(write, delete);
    }

    private static async Task AnswerAsync<T>(Stream stream, string operation, TbepWire.Frame request, T body)
    {
        var bytes = MessagePackSerializer.Serialize(body, TbepOptions.Strict);
        await TbepWire.WriteFrame(
            stream,
            TbepWire.Seal(
                TbepRegistry.ResponseKindOf(operation),
                correlation: 1,
                causation: request.Correlation,
                idempotency: null,
                bytes)).ConfigureAwait(false);
    }

    private sealed record CapturedRequests(DocumentWriteRequest Write, DocumentDeleteRequest Delete);
}
