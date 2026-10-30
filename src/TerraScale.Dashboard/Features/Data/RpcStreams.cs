using System.Runtime.CompilerServices;
using Grpc.Core;
using MagicOnion;
using MessagePack;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Data
{
    // Streaming faults arrive while advancing the reader, after the unary client interceptor has returned.
    internal static class RpcStreams
    {
        internal static async IAsyncEnumerable<T> Read<T>(Task<ServerStreamingResult<T>> opened,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ServerStreamingResult<T> stream;
            try
            {
                stream = await opened;
            }
            catch (RpcException error)
            {
                throw Failure(error, cancellationToken);
            }

            using var owned = stream;
            while (true)
            {
                bool next;
                try
                {
                    next = await stream.ResponseStream.MoveNext(cancellationToken);
                }
                catch (RpcException error)
                {
                    throw Failure(error, cancellationToken);
                }

                if (!next)
                {
                    yield break;
                }

                yield return stream.ResponseStream.Current;
            }
        }

        private static Exception Failure(RpcException error, CancellationToken cancellationToken)
        {
            if (error.StatusCode == StatusCode.Cancelled)
            {
                return new OperationCanceledException(error.Message, error, cancellationToken);
            }

            var fault = error.Trailers.GetValueBytes(TerraBaseRpc.FaultTrailer) is { } bytes
                ? MessagePackSerializer.Deserialize<TerraBaseFault>(bytes, TerraBaseRpc.SerializerOptions)
                : error.StatusCode switch
                {
                    StatusCode.Unauthenticated => new TerraBaseFault(401, "unauthenticated", "TerraBase refused the peer credential."),
                    StatusCode.DeadlineExceeded => new TerraBaseFault(504, "deadline_exceeded", "The TerraBase stream timed out."),
                    _ => new TerraBaseFault(503, "unavailable", "The TerraBase stream is unavailable."),
                };
            return TerraBaseException.From(fault, error);
        }
    }
}
