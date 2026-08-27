// <copyright file="DroppingHandler.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

/// <summary>
/// The dropped-connection fixture of the <c>changes --follow</c> row: a
/// delegating handler that serves the real HTTP traffic and can be told
/// — exactly once, on demand — to fault the live SSE response stream of
/// the moment, so the connection drops under the CLI's feet at a
/// deterministic point (after the operator has seen the events the test
/// waited for, before the commit that must be recovered by the
/// checkpoint resume). The CLI's production path is unchanged: it sees
/// an <see cref="IOException"/> on its response stream, exactly as a
/// real transport drop surfaces; later requests — including the
/// reconnected stream — are served untouched.
/// </summary>
public sealed class DroppingHandler : DelegatingHandler
{
    private readonly object gate = new();
    private readonly TaskCompletionSource dropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FaultingStream? live;
    private bool fired;

    /// <summary>Initializes the handler over the real transport.</summary>
    public DroppingHandler()
        : base(new SocketsHttpHandler())
    {
    }

    /// <summary>A task that completes when the faulted read surfaced.</summary>
    public Task Dropped => this.dropped.Task;

    /// <summary>Drops the live SSE connection: its pending or next read faults.</summary>
    public void Drop()
    {
        lock (this.gate)
        {
            if (this.fired || this.live is null)
            {
                return;
            }

            this.fired = true;
            this.live.Arm();
            this.live = null;
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var answer = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Method == HttpMethod.Get
            && request.RequestUri?.AbsolutePath == "/v1/streams"
            && answer.Content is { } content)
        {
            var inner = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var faulting = new FaultingStream(inner, this.dropped);
            lock (this.gate)
            {
                if (!this.fired)
                {
                    this.live = faulting;
                }
            }

            answer.Content = new StreamContent(faulting);
            foreach (var header in content.Headers)
            {
                answer.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return answer;
    }

    private sealed class FaultingStream(Stream inner, TaskCompletionSource dropped) : Stream
    {
        private readonly TaskCompletionSource arm = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => this.arm.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this.arm.Task.IsCompleted)
            {
                // The connection dropped under the consumer: the read
                // faults exactly as a transport loss surfaces.
                dropped.TrySetResult();
                throw new IOException("the connection dropped (test fault)");
            }

            // A read already in flight when the drop lands must fault
            // too — the transport loss interrupts the pending read, it
            // does not wait for the next one.
            var read = inner.ReadAsync(buffer, cancellationToken).AsTask();
            var completed = await Task.WhenAny(read, this.arm.Task).ConfigureAwait(false);
            if (completed != read)
            {
                // The abandoned inner read's outcome is observed, never
                // left as unobserved noise.
                _ = read.ContinueWith(
                    abandoned => _ = abandoned.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                dropped.TrySetResult();
                throw new IOException("the connection dropped (test fault)");
            }

            return await read.ConfigureAwait(false);
        }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("asynchronous reads only");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
