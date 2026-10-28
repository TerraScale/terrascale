using System.Diagnostics;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Accounts for completed client requests and actual HTTP body I/O within their authorized database.</summary>
public static class UsageRequestMetering
{
    /// <summary>
    /// Attributes the request to an authorized database an endpoint resolved itself, without publishing it as the request's
    /// tenant: its request count, latency and body bytes are metered, and no document operation is.
    /// </summary>
    public static void Attribute(HttpContext context, TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Set(new AttributedDatabase(database));
    }

    /// <summary>Wraps body streams before authentication; records only requests that resolve an authorized database.</summary>
    public static WebApplication UseTerraBaseUsage(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var meter = app.Services.GetRequiredService<IUsageMeter>();
        var time = app.Services.GetRequiredService<TimeProvider>();
        var logs = app.Services.GetRequiredService<IRequestLogs>();
        app.Use(async (context, next) =>
        {
            if (Listeners.ArrivedOn(context) != TerraBaseListener.Client)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var input = context.Request.Body;
            var output = context.Response.Body;
            using var transfer = new Transfer(context, meter, time, logs);
            var previousOperations = RequestLogOperations.Current.Value;
            var operations = new RequestLogOperations();
            RequestLogOperations.Current.Value = operations;
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
                try { transfer.Complete(failed ? 500 : context.Response.StatusCode, Stopwatch.GetElapsedTime(started), operations); }
                finally { RequestLogOperations.Current.Value = previousOperations; }
            }
        });
        return app;
    }

    // A request's body streams. Bytes are reported into the minute they were transferred in: when the request moves on to
    // another minute, on each flush tick while it stays open (a subscription may idle for long), and when it ends.
    private sealed class Transfer : IDisposable
    {
        private readonly HttpContext _context;
        private readonly IUsageMeter _meter;
        private readonly TimeProvider _time;
        private readonly IRequestLogs _logs;
        private readonly IDisposable _tracked;
        private readonly Lock _gate = new();
        private TenantContext? _database;
        private bool _resolved;
        private long _received;
        private long _sent;
        private DateTimeOffset _transferredAt;

        public Transfer(HttpContext context, IUsageMeter meter, TimeProvider time, IRequestLogs logs)
        {
            (_context, _meter, _time, _logs) = (context, meter, time, logs);
            Received = new CountingStream(context.Request.Body, count => Moved(count, 0));
            Sent = new CountingStream(context.Response.Body, count => Moved(0, count));
            _tracked = meter.TrackActive(Report);
        }

        public CountingStream Received { get; }

        public CountingStream Sent { get; }

        public void Complete(int status, TimeSpan elapsed, RequestLogOperations operations)
        {
            lock (_gate)
            {
                Resolve();
                ReportLocked();
                if (_database is { } database)
                {
                    _meter.RecordRequest(database, _context.Request.Method, status, elapsed, 0, 0);
                    if (_context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route })
                    {
                        var (statements, errors) = operations.Statements(database);
                        _logs.Record(database, _context.Request.Method, route, status, elapsed, operations.Count(database), _context.TraceIdentifier, statements, errors);
                    }
                }
            }
        }

        public void Dispose()
        {
            _tracked.Dispose();
            Received.Dispose();
            Sent.Dispose();
        }

        // On the request's own I/O, where its features may be read.
        private void Moved(long received, long sent)
        {
            lock (_gate)
            {
                Resolve();
                var now = _time.GetUtcNow();
                if (_received + _sent > 0 && now.ToUnixTimeSeconds() / 60 != _transferredAt.ToUnixTimeSeconds() / 60)
                {
                    ReportLocked();
                }

                if (_received + _sent == 0)
                {
                    _transferredAt = now;
                }

                (_received, _sent) = (_received + received, _sent + sent);
            }
        }

        // From the flush tick: uses only the database resolved on the request's own thread.
        private void Report()
        {
            lock (_gate)
            {
                ReportLocked();
            }
        }

        private void ReportLocked()
        {
            if (_received + _sent > 0 && _database is { } database)
            {
                _meter.RecordClientBytes(database, _received, _sent, _transferredAt);
                (_received, _sent) = (0, 0);
            }
        }

        // The authorized database, once resolved.
        private void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            var database = _context.Features.Get<TenantContext>() ?? _context.Features.Get<AttributedDatabase>()?.Database;
            // databaseName is set only once the project catalog resolved the route's database to its ID.
            if (database is null && _context.Features.Get<ProjectAccess>() is { } access
                && _context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null
                && _context.Request.RouteValues.ContainsKey("databaseName") && _context.Request.RouteValues["database"] is string id)
            {
                database = new(access.OrganizationId, PlanTier.Hobby, access.ProjectId, id);
            }

            if (database?.DatabaseId is not null)
            {
                (_database, _resolved) = (database, true);
            }
        }
    }

    // The wrapper owns no buffer and never closes the server's streams.
    private sealed class CountingStream(Stream inner, Action<int> counted) : Stream
    {
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
            counted(count);
            return count;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            counted(count);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            counted(buffer.Length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            counted(buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed record AttributedDatabase(TenantContext Database);
}
