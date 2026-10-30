using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TerraScale.TerraBase.Rpc
{
    // The response of an in-process REST request: headers, a bounded in-memory body, and the start and completion
    // callbacks that middleware registers (identity, metering), run once the pipeline finishes.
    internal sealed class RestResponse : IHttpResponseFeature, IHttpResponseBodyFeature, IFinishable, IDisposable
    {
        private readonly List<(Func<object, Task> Callback, object State)> starting = [];
        private readonly List<(Func<object, Task> Callback, object State)> completed = [];
        private readonly BoundedBuffer body;
        private PipeWriter? writer;

        internal RestResponse(int maxBytes)
        {
            body = new BoundedBuffer(maxBytes);
        }

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get => body; set => throw new NotSupportedException(); }
        public bool HasStarted { get; private set; }
        public Stream Stream => body;
        public PipeWriter Writer => writer ??= PipeWriter.Create(body, new StreamPipeWriterOptions(leaveOpen: true));

        internal ReadOnlyMemory<byte> Written => body.Written;

        public void OnStarting(Func<object, Task> callback, object state)
        {
            starting.Add((callback, state));
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
            completed.Add((callback, state));
        }

        public void Dispose()
        {
            body.Dispose();
        }

        public void DisableBuffering()
        {
            // The answer is buffered whole and bounded.
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (HasStarted)
            {
                return;
            }

            HasStarted = true;
            for (var index = starting.Count - 1; index >= 0; index--)
            {
                await starting[index].Callback(starting[index].State).ConfigureAwait(false);
            }
        }

        public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            await using var file = File.OpenRead(path);
            file.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[81920];
            var remaining = count ?? file.Length - offset;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }

        public async Task CompleteAsync()
        {
            await StartAsync().ConfigureAwait(false);
            if (writer is not null)
            {
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        public async Task FinishAsync()
        {
            await CompleteAsync().ConfigureAwait(false);
            for (var index = completed.Count - 1; index >= 0; index--)
            {
                await completed[index].Callback(completed[index].State).ConfigureAwait(false);
            }
        }

        private sealed class BoundedBuffer(int maxBytes) : Stream
        {
            private readonly MemoryStream inner = new();

            internal ReadOnlyMemory<byte> Written => inner.GetBuffer().AsMemory(0, (int)inner.Length);

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;
            public override long Position { get => inner.Length; set => throw new NotSupportedException(); }

            public override void Flush()
            {
                // Writes land in memory.
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Write(buffer.AsSpan(offset, count));
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (inner.Length + buffer.Length > maxBytes)
                {
                    throw new InvalidDataException("The response exceeds the RPC message limit");
                }

                inner.Write(buffer);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer.AsSpan(offset, count));
                return Task.CompletedTask;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
