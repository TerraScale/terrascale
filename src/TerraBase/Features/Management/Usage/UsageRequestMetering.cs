using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Features.Regions;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

public static class UsageRequestMetering
{
    public static void Attribute(HttpContext context, TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Set(new AttributedDatabase(database));
    }

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
            var output = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
            using var transfer = new Transfer(context, meter, time, logs);
            var previousOperations = RequestLogOperations.Current.Value;
            var operations = new RequestLogOperations();
            RequestLogOperations.Current.Value = operations;
            if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false)
            {
                context.Request.Body = transfer.Received;
            }

            context.Features.Set<IHttpResponseBodyFeature>(new CountingResponseBody(output, transfer.Sent));
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
                context.Features.Set(output);
                try { transfer.Complete(failed ? 500 : context.Response.StatusCode, Stopwatch.GetElapsedTime(started), operations); }
                finally { RequestLogOperations.Current.Value = previousOperations; }
            }
        });
        return app;
    }

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
            Sent = count => Moved(0, count);
            _tracked = meter.TrackActive(Report);
        }

        public CountingStream Received { get; }

        public Action<int> Sent { get; }

        public void Complete(int status, TimeSpan elapsed, RequestLogOperations operations)
        {
            lock (_gate)
            {
                Resolve();
                ReportLocked();
                if (_context.Features.Get<HomeForwarding.ProxiedRequest>() is null
                    && !_context.WebSockets.IsWebSocketRequest && _database is { } database)
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
        }

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

        private void Report()
        {
            lock (_gate)
            {
                ReportLocked();
            }
        }

        private void ReportLocked()
        {
            if (_context.Features.Get<HomeForwarding.ProxiedRequest>() is null
                && !_context.WebSockets.IsWebSocketRequest && _received + _sent > 0 && _database is { } database)
            {
                _meter.RecordClientBytes(database, _received, _sent, _transferredAt);
                (_received, _sent) = (0, 0);
            }
        }

        private void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            var database = _context.Features.Get<TenantContext>() ?? _context.Features.Get<AttributedDatabase>()?.Database;
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

    private sealed class CountingResponseBody(IHttpResponseBodyFeature inner, Action<int> counted) : IHttpResponseBodyFeature
    {
        private CountingPipeWriter? _writer;
        private CountingStream? _stream;

        public PipeWriter Writer => _writer ??= new CountingPipeWriter(inner.Writer, counted);

        public Stream Stream => _stream ??= new CountingStream(inner.Stream, counted);

        public void DisableBuffering() => inner.DisableBuffering();

        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);

        public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            await inner.StartAsync(cancellationToken).ConfigureAwait(false);
            await SendFileFallback.SendFileAsync(Stream, path, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public Task CompleteAsync() => inner.CompleteAsync();
    }

    private sealed class CountingPipeWriter(PipeWriter inner, Action<int> counted) : PipeWriter
    {
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;

        public override long UnflushedBytes => inner.UnflushedBytes;

        public override void Advance(int bytes)
        {
            inner.Advance(bytes);
            counted(bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        public override async ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            var result = await inner.WriteAsync(source, cancellationToken).ConfigureAwait(false);
            counted(source.Length);
            return result;
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }

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
