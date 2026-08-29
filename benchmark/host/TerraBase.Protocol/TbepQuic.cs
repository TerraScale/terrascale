// <copyright file="TbepQuic.cs" company="TerraScale">
// TerraBaseV2 (SE-14). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol;

using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Net.Security;
using System.Security.Authentication;

/// <summary>
/// The TBEP client's QUIC+TLS binding (P4): the same client discipline
/// over an authenticated TLS 1.3 transport — one bidirectional QUIC
/// stream carries one TBEP channel, exactly as the local
/// Unix-domain-socket binding does.
/// </summary>
[SupportedOSPlatform("linux")]
public static class TbepQuic
{
    /// <summary>The binding's ALPN identity, identical to the engine's.</summary>
    public static readonly SslApplicationProtocol Protocol = new("terrabase.engine.protocol/1");

    /// <summary>Connects one TBEP channel over QUIC+TLS to a serving engine process.</summary>
    /// <param name="host">The engine's QUIC host name (the certificate's name).</param>
    /// <param name="port">The engine's QUIC port.</param>
    /// <param name="trustServerCertificate">Whether the row trusts the presented certificate unverified — test rows with self-signed identities only; production verifies.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The connected client, unbound.</returns>
    /// <exception cref="System.PlatformNotSupportedException">The platform carries no QUIC support.</exception>
    /// <exception cref="System.Security.Authentication.AuthenticationException">The TLS handshake failed.</exception>
    public static async Task<TbepClient> ConnectAsync(string host, int port, bool trustServerCertificate, CancellationToken cancellationToken = default)
    {
        var options = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [Protocol],
            TargetHost = host,
        };
        if (trustServerCertificate)
        {
            options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var connection = await QuicConnection.ConnectAsync(
            new QuicClientConnectionOptions
            {
                RemoteEndPoint = new DnsEndPoint(host, port),
                ClientAuthenticationOptions = options,
            },
            cancellationToken).ConfigureAwait(false);
        var stream = await connection.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional,
            cancellationToken).ConfigureAwait(false);
        return TbepClient.OverStream(new QuicChannelStream(connection, stream));
    }

    /// <summary>One bidirectional QUIC stream as a <see cref="Stream"/>: disposing it closes the connection it belongs to.</summary>
    private sealed class QuicChannelStream : Stream
    {
        private readonly QuicConnection connection;
        private readonly QuicStream stream;

        internal QuicChannelStream(QuicConnection connection, QuicStream stream)
        {
            this.connection = connection;
            this.stream = stream;
        }

        public override bool CanRead => this.stream.CanRead;

        public override bool CanWrite => this.stream.CanWrite;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await this.stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        public override int Read(byte[] buffer, int offset, int count) =>
            this.stream.Read(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await this.stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

        public override void Write(byte[] buffer, int offset, int count) =>
            this.stream.Write(buffer, offset, count);

        public override void Flush() => this.stream.Flush();

        public override async Task FlushAsync(CancellationToken cancellationToken = default) =>
            await this.stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.stream.Dispose();
                this.connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await this.stream.DisposeAsync().ConfigureAwait(false);
            await this.connection.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
