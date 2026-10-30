using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.TerraBase.Rpc.Changes
{
    internal sealed class LineResponse : Stream, IHttpResponseFeature, IHttpResponseBodyFeature, IFinishable
    {
        private readonly string mediaType;
        private readonly Func<ReadOnlyMemory<byte>, string?, Task> onLine;
        private readonly MemoryStream pending = new();
        private readonly List<(Func<object, Task> Callback, object State)> starting = [];
        private readonly List<(Func<object, Task> Callback, object State)> completed = [];
        private PipeWriter? writer;
        private bool finished;

        internal LineResponse(string mediaType, Func<ReadOnlyMemory<byte>, string?, Task> onLine)
        {
            this.mediaType = mediaType;
            this.onLine = onLine;
        }

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get => this; set => throw new NotSupportedException(); }
        public bool HasStarted { get; private set; }
        public Stream Stream => this;
        public PipeWriter Writer => writer ??= PipeWriter.Create(this, new StreamPipeWriterOptions(leaveOpen: true));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        internal ReadOnlyMemory<byte> ErrorBody => pending.ToArray();

        public void OnStarting(Func<object, Task> callback, object state)
        {
            starting.Add((callback, state));
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
            completed.Add((callback, state));
        }

        public void DisableBuffering()
        {
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            if (finished)
            {
                return;
            }

            finished = true;
            try
            {
                await CompleteAsync().ConfigureAwait(false);
                if (StatusCode is >= 200 and <= 299 && pending.Length != 0)
                {
                    throw new InvalidDataException("The REST stream ended with an incomplete line.");
                }
            }
            finally
            {
                if (writer is not null)
                {
                    await writer.CompleteAsync().ConfigureAwait(false);
                }

                for (var index = completed.Count - 1; index >= 0; index--)
                {
                    await completed[index].Callback(completed[index].State).ConfigureAwait(false);
                }
            }
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            if (StatusCode is < 200 or > 299)
            {
                Append(buffer.Span);
                return;
            }

            if (!Headers.ContentType.ToString().StartsWith(mediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The REST stream has an unexpected media type.");
            }

            while (!buffer.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newline = buffer.Span.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    Append(buffer.Span);
                    return;
                }

                Append(buffer.Span[..newline]);
                var line = pending.ToArray();
                pending.SetLength(0);
                await onLine(line, Headers["X-TerraBase-Region"].FirstOrDefault()).ConfigureAwait(false);
                buffer = buffer[(newline + 1)..];
            }
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            if (pending.Length + bytes.Length > TerraBaseRpc.MaxMessageBytes)
            {
                throw new InvalidDataException("The REST stream line exceeds the RPC message limit.");
            }

            pending.Write(bytes);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return StartAsync(cancellationToken);
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pending.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
