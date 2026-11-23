using System.Buffers;

namespace TerraScale.QuicRpc.Messages;

public enum SqlConsistencyHint : byte
{
    DatabaseDefault = 0,
    Fast = 1,
    Strong = 2,
}

public sealed record InternalSqlRequest(Ulid DatabaseId, string Bookmark, SqlConsistencyHint Consistency, ReadOnlyMemory<byte> Statements)
    : IBinaryMessage<InternalSqlRequest>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUlid(DatabaseId);
        writer.WriteString(Bookmark);
        writer.WriteUInt8((byte)Consistency);
        writer.WriteBytes(Statements.Span);
    }

    public static InternalSqlRequest ReadFrom(ref WireReader reader) =>
        new(reader.ReadUlid(), reader.ReadString(), (SqlConsistencyHint)reader.ReadUInt8(), reader.ReadBytes());
}

public sealed record InternalSqlReply(ushort Status, string Bookmark, ReadOnlyMemory<byte> Body) : IBinaryMessage<InternalSqlReply>
{
    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt16(Status);
        writer.WriteString(Bookmark);
        writer.WriteBytes(Body.Span);
    }

    public static InternalSqlReply ReadFrom(ref WireReader reader) => new(reader.ReadUInt16(), reader.ReadString(), reader.ReadBytes());
}
