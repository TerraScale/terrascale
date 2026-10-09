using System.Diagnostics;
using TerraBase.Features.Replication;
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
        var time = app.Services.GetRequiredService<TimeProvider>();
        app.Use(async (context, next) =>
        {
            if (Listeners.ArrivedOn(context) != TerraBaseListener.Client)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var input = context.Request.Body;
            var output = context.Response.Body;
            using var transfer = new Transfer(context, meter, time);
            context.Request.Body = transfer.Received;
            context.Response.Body = transfer.Sent;
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
                transfer.Complete(failed ? 500 : context.Response.StatusCode, Stopwatch.GetElapsedTime(started));
            }
        });
        return app;
    }

    // A request's body streams. A long request, such as a subscription, reports its body bytes into the buckets they flow in
    // at most every ReportInterval, and the rest with its count and latency when it ends.
    private sealed class Transfer : IDisposable
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(10);
        private readonly HttpContext _context;
        private readonly IUsageMeter _meter;
        private readonly TimeProvider _time;
        private readonly Lock _gate = new();
        private long _reportedReceived;
        private long _reportedSent;
        private long _reportedAt;

        public Transfer(HttpContext context, IUsageMeter meter, TimeProvider time)
        {
            (_context, _meter, _time) = (context, meter, time);
            _reportedAt = time.GetTimestamp();
            Received = new CountingStream(context.Request.Body, Progress);
            Sent = new CountingStream(context.Response.Body, Progress);
        }

        public CountingStream Received { get; }

        public CountingStream Sent { get; }

        public void Complete(int status, TimeSpan elapsed)
        {
            if (Database() is { } database)
            {
                lock (_gate)
                {
                    var (received, sent) = Take();
                    _meter.RecordRequest(database, _context.Request.Method, status, elapsed, received, sent);
                }
            }
        }

        public void Dispose()
        {
            Received.Dispose();
            Sent.Dispose();
        }

        private void Progress()
        {
            if (_time.GetElapsedTime(Volatile.Read(ref _reportedAt)) < ReportInterval || Database() is not { } database)
            {
                return;
            }

            lock (_gate)
            {
                if (_time.GetElapsedTime(_reportedAt) >= ReportInterval)
                {
                    var (received, sent) = Take();
                    Volatile.Write(ref _reportedAt, _time.GetTimestamp());
                    _meter.RecordClientBytes(database, received, sent);
                }
            }
        }

        // Under _gate: the bytes not reported yet.
        private (long Received, long Sent) Take()
        {
            var (received, sent) = (Received.ReadBytes - _reportedReceived, Sent.WrittenBytes - _reportedSent);
            (_reportedReceived, _reportedSent) = (_reportedReceived + received, _reportedSent + sent);
            return (received, sent);
        }

        // The authorized database, once resolved; a follower that relayed the request to the catalog primary leaves it to the
        // primary, which resolved its database.
        private TenantContext? Database()
        {
            if (_context.Features.Get<CatalogForwarder.Relayed>() is not null)
            {
                return null;
            }

            var database = _context.Features.Get<TenantContext>();
            // databaseName is set only once the project catalog resolved the route's database to its ID.
            if (database is null && _context.Features.Get<ProjectAccess>() is { } access
                && _context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null
                && _context.Request.RouteValues.ContainsKey("databaseName") && _context.Request.RouteValues["database"] is string id)
            {
                database = new(access.OrganizationId, PlanTier.Hobby, access.ProjectId, id);
            }

            return database?.DatabaseId is null ? null : database;
        }
    }

    // The wrapper owns no buffer and never closes the server's streams.
    private sealed class CountingStream(Stream inner, Action progress) : Stream
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
            progress();
            return count;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _read, count);
            progress();
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Interlocked.Add(ref _written, buffer.Length);
            progress();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _written, buffer.Length);
            progress();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
