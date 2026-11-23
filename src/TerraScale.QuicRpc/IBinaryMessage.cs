using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace TerraScale.QuicRpc;

public interface IBinaryMessage<TSelf> where TSelf : IBinaryMessage<TSelf>
{
    void WriteTo(IBufferWriter<byte> writer);

    static abstract TSelf ReadFrom(ref WireReader reader);
}

public static class BinaryMessage
{
    public static bool TryDecode<T>(ReadOnlySpan<byte> payload, [MaybeNullWhen(false)] out T message)
        where T : IBinaryMessage<T>
    {
        var reader = new WireReader(payload);
        var decoded = T.ReadFrom(ref reader);
        if (reader.IsComplete)
        {
            message = decoded;
            return true;
        }

        message = default;
        return false;
    }
}
