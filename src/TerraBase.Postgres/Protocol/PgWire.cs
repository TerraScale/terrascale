using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;

namespace TerraBase.Postgres.Protocol;

internal sealed class PgWire(IDuplexPipe transport, TimeProvider timeProvider) : IAsyncDisposable
{
    private SslStream? _tls;

    public PgFrameReader Reader { get; private set; } = new(transport.Input, timeProvider);

    public PgBackendWriter Writer { get; private set; } = new(transport.Output, timeProvider);

    public async ValueTask<bool> UpgradeAsync(SslServerAuthenticationOptions options, CancellationToken cancellationToken)
    {
        var tls = new SslStream(new DuplexPipeStream(transport), leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsServerAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AuthenticationException or IOException)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        _tls = tls;
        Writer.Dispose();
        Reader = new PgFrameReader(PipeReader.Create(tls, new StreamPipeReaderOptions(leaveOpen: true)), timeProvider);
        Writer = new PgBackendWriter(PipeWriter.Create(tls, new StreamPipeWriterOptions(leaveOpen: true)), timeProvider);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        Writer.Dispose();
        if (_tls is { } tls)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class DuplexPipeStream(IDuplexPipe pipe) : Stream
    {
        private readonly Stream _input = pipe.Input.AsStream(leaveOpen: true);
        private readonly Stream _output = pipe.Output.AsStream(leaveOpen: true);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _input.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _output.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _output.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => _output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
                _output.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
