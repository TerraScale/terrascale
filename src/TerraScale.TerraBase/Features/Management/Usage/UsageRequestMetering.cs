using System.Diagnostics;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Accounts for completed client requests and actual HTTP body I/O within their authorized database.</summary>
public static class UsageRequestMetering
{
    /// <summary>Wraps body streams before authentication; records only requests that resolve an authorized database.</summary>
    public static WebApplication UseTerraBaseUsage(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var meter = app.Services.GetRequiredService<IUsageMeter>();
        app.Use(async (context, next) =>
        {
            if (Listeners.ArrivedOn(context) != TerraBaseListener.Client)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var input = context.Request.Body;
            var output = context.Response.Body;
            using var received = new CountingStream(input);
            using var sent = new CountingStream(output);
            context.Request.Body = received;
            context.Response.Body = sent;
            var started = Stopwatch.GetTimestamp();
            var failed = false;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                context.Request.Body = input;
                context.Response.Body = output;
                var database = context.Features.Get<TenantContext>();
                if (database is null && context.Features.Get<ProjectAccess>() is { } access
                    && context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null
                    && context.Request.RouteValues["database"] is string id)
                {
                    database = new(access.OrganizationId, PlanTier.Hobby, access.ProjectId, id);
                }

                if (database?.DatabaseId is not null)
                {
                    meter.RecordRequest(database, context.Request.Method, failed ? 500 : context.Response.StatusCode,
                        Stopwatch.GetElapsedTime(started), received.ReadBytes, sent.WrittenBytes);
                }
            }
        });
        return app;
    }

    // The wrapper owns no buffer and never closes the server's streams.
    private sealed class CountingStream(Stream inner) : Stream
    {
        private long _read;
        private long _written;

        public long ReadBytes => Interlocked.Read(ref _read);
        public long WrittenBytes => Interlocked.Read(ref _written);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = inner.Read(buffer);
            Interlocked.Add(ref _read, count);
            return count;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _read, count);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Interlocked.Add(ref _written, buffer.Length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _written, buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
