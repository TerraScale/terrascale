using System.Buffers;
using Google.Protobuf;
using TerraBase.Engine.Revisions;

namespace TerraBase.Features.Sync;

internal static class PullAnswer
{
    public const string MediaType = "application/protobuf";

    public static string? Refusal(PullUpdatesRequest request) => request switch
    {
        { Encoding: not PageEncoding.Raw } => "only raw page encoding is served",
        { StreamKind: not StreamKind.Pages } => "only page pulls are served",
        { ServerRevision.Length: > 0 } or { ServerQuerySelector.Length: > 0 } or { ServerPagesSelector.IsEmpty: false } or { ClientPages.IsEmpty: false }
            => "partial pulls are not served: send only client_revision",
        _ => null,
    };

    public static bool TryClientRevision(ReadOnlySpan<byte> body, out string clientRevision)
    {
        clientRevision = string.Empty;
        try
        {
            var request = PullUpdatesRequest.Parser.ParseFrom(body);
            if (Refusal(request) is not null)
            {
                return false;
            }

            clientRevision = request.ClientRevision;
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }

    public static void WriteHeader(IBufferWriter<byte> writer, RevisionPullStart start) => Write(writer, new PullUpdatesHeader
    {
        ServerRevision = start.Revision.ToString(),
        DbSize = start.DatabaseSize,
        RawEncoding = new RawPageEncoding(),
        StreamKind = StreamKind.Pages,
        Protocol = PullProtocol.Pages,
    });

    public static void WritePages(IBufferWriter<byte> writer, RevisionPageBatch batch)
    {
        for (var index = 0; index < batch.Numbers.Length; index++)
        {
            Write(writer, new PageData
            {
                PageId = batch.Numbers.Span[index] - 1UL,
                EncodedPage = UnsafeByteOperations.UnsafeWrap(batch.Pages.Slice(index * batch.PageSize, batch.PageSize)),
            });
        }
    }

    public static int PageBytes(int pageSize) => pageSize + 16;

    private static void Write(IBufferWriter<byte> writer, IMessage message)
    {
        var length = (uint)message.CalculateSize();
        var prefix = writer.GetSpan(5);
        var used = 0;
        for (; length >= 0x80; length >>= 7)
        {
            prefix[used++] = (byte)(length | 0x80);
        }

        prefix[used++] = (byte)length;
        writer.Advance(used);
        message.WriteTo(writer);
    }
}
