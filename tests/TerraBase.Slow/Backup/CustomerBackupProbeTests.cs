using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Slow.Backup;

public sealed class CustomerBackupProbeTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Cancellation_after_upload_joins_independent_bounded_cleanup_before_save_recovery(bool saveTimeout, bool cleanupTimeout)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var request = new ObserveRequest();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            Manual(s3, services);
            services.AddSingleton<IStartupFilter>(request);
        }, time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        using var body = new StalledProbeBody(Encoding.ASCII.GetBytes("TerraBase backup access check"));
        s3.CustomerContentOverride = key => IsProbe(key) ? new StreamContent(body) : null;
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken deleteToken = default;
        var deletes = 0;
        s3.BeforeCustomerDelete = async (key, cancellationToken) =>
        {
            if (!IsProbe(key))
            {
                return;
            }

            Interlocked.Increment(ref deletes);
            deleteToken = cancellationToken;
            deleting.TrySetResult();
            try
            {
                await releaseDelete.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                deleteExited.TrySetResult();
            }
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var saving = api.Client.PutAsJsonAsync(route, Destination(), caller.Token);
        try
        {
            await body.Entered.Task.WaitAsync(token);
            await Assert.That(s3.CustomerObjects.Count).IsEqualTo(1);
            if (saveTimeout)
            {
                time.Advance(TimeSpan.FromSeconds(30));
            }
            else
            {
                await caller.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => saving);
            }

            await deleting.Task.WaitAsync(token);
            await Assert.That(body.ReadToken.IsCancellationRequested).IsTrue();
            await Assert.That(body.Disposed).IsTrue();
            await Assert.That(deleteToken.IsCancellationRequested).IsFalse();
            await Assert.That(request.Completed.Task.IsCompleted).IsFalse();
            using var absent = await api.Client.GetAsync(route, token);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            if (cleanupTimeout)
            {
                time.Advance(TimeSpan.FromSeconds(5));
            }
            else
            {
                releaseDelete.TrySetResult();
            }

            await deleteExited.Task.WaitAsync(token);
            await request.Completed.Task.WaitAsync(token);
            await Assert.That(deleteToken.IsCancellationRequested).IsEqualTo(cleanupTimeout);
            if (saveTimeout)
            {
                await ErrorAssert.ErrorAsync(saving, HttpStatusCode.ServiceUnavailable,
                    "unavailable", "customer backup bucket could not be accessed");
            }
            else
            {
                await Assert.That(request.RequestToken.IsCancellationRequested).IsTrue();
                await Assert.That(request.Error).IsNull();
                await Assert.That(request.ResponseStatus).IsEqualTo(StatusCodes.Status499ClientClosedRequest);
            }

            await Assert.That(Volatile.Read(ref deletes)).IsEqualTo(1);
            await Assert.That(s3.CustomerObjects.Count).IsEqualTo(cleanupTimeout ? 1 : 0);
            using var stillAbsent = await api.Client.GetAsync(route, token);
            await Assert.That(stillAbsent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            s3.CustomerContentOverride = null;
            s3.BeforeCustomerDelete = null;
            using var recovered = await api.Client.PutAsJsonAsync(route, Destination(), token);
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(s3.CustomerObjects.Count).IsEqualTo(cleanupTimeout ? 1 : 0);
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            body.Release();
            releaseDelete.TrySetResult();
            await caller.CancelAsync();
            await ((Task)saving).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (saving.IsCompletedSuccessfully)
            {
                using var response = await saving;
            }
            await request.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    [Arguments("probe-error")]
    [Arguments("delete-permission")]
    [Arguments("object-lock")]
    public async Task Cleanup_preserves_probe_errors_requires_delete_permission_and_allows_object_lock(string kind)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Manual(s3, services));
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        using var body = new ProbeBody(Encoding.ASCII.GetBytes("TerraBase backup access check"), chunked: true, failRead: kind == "probe-error");
        s3.CustomerContentOverride = key => IsProbe(key) ? new StreamContent(body) : null;
        s3.Locked = key => kind == "object-lock" && IsProbe(key);
        s3.BeforeCustomerDelete = (_, _) => kind switch
        {
            "probe-error" => Task.FromException(new HttpRequestException("cleanup failed after invalid probe")),
            "delete-permission" => Task.FromException(new HttpRequestException("delete permission denied")),
            _ => Task.CompletedTask,
        };
        if (kind == "object-lock")
        {
            using var saved = await api.Client.PutAsJsonAsync(route, Destination(), token);
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        else
        {
            await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, Destination(), token),
                kind == "probe-error" ? HttpStatusCode.InternalServerError : HttpStatusCode.ServiceUnavailable,
                kind == "probe-error" ? "internal_error" : "unavailable",
                kind == "probe-error" ? null : "customer backup bucket could not be accessed");
            using var absent = await api.Client.GetAsync(route, token);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(body.Disposed).IsTrue();
        await Assert.That(s3.CustomerObjects.Count).IsEqualTo(1);
        s3.CustomerContentOverride = null;
        s3.BeforeCustomerDelete = null;
        s3.Locked = _ => false;
        using var recovered = await api.Client.PutAsJsonAsync(route, Destination(), token);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(s3.CustomerObjects.Count).IsEqualTo(1);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments("oversized", false)]
    [Arguments("oversized", true)]
    [Arguments("mismatch", false)]
    [Arguments("mismatch", true)]
    [Arguments("short", true)]
    [Arguments("empty", true)]
    public async Task Invalid_probe_bodies_are_bounded_rejected_and_cleaned_up(string kind, bool chunked)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Manual(s3, services));
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.ProjectPath + "/backup-destination");
        var expected = Encoding.ASCII.GetBytes("TerraBase backup access check");
        var payload = kind switch
        {
            "oversized" => new byte[1024 * 1024],
            "mismatch" => Encoding.ASCII.GetBytes("terraBase backup access check"),
            "short" => expected[..^1],
            "empty" => [],
            _ => throw new ArgumentException("unknown probe test body", nameof(kind)),
        };
        if (kind == "oversized")
        {
            expected.CopyTo(payload, 0);
        }

        using var body = new ProbeBody(payload, chunked);
        long? contentLength = null;
        s3.CustomerContentOverride = key => IsProbe(key) ? new StreamContent(body) : null;
        s3.CustomerGetResponseCreated = (key, response) =>
        {
            if (IsProbe(key))
            {
                contentLength = response.Content.Headers.ContentLength;
                response.Headers.TransferEncodingChunked = chunked ? true : null;
            }
        };
        var deletes = 0;
        var lists = 0;
        s3.ObjectRequested = (customer, method, key) =>
        {
            if (customer && method == HttpMethod.Delete && IsProbe(key))
            {
                Interlocked.Increment(ref deletes);
            }
            if (customer && method == HttpMethod.Get && key.Length == 0)
            {
                Interlocked.Increment(ref lists);
            }
        };

        await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, Destination(), token), HttpStatusCode.ServiceUnavailable,
            "unavailable", "customer backup bucket could not be accessed");
        await Assert.That(body.BytesRead).IsEqualTo(Math.Min(payload.Length, expected.Length + 1));
        await Assert.That(body.LargestRequest).IsLessThanOrEqualTo(expected.Length + 1);
        await Assert.That(body.Disposed).IsTrue();
        await Assert.That(contentLength is null).IsEqualTo(chunked);
        await Assert.That(Volatile.Read(ref deletes)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref lists)).IsEqualTo(0);
        await Assert.That(s3.CustomerObjects.Count).IsEqualTo(0);
        using var absent = await api.Client.GetAsync(route, token);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        s3.CustomerContentOverride = null;
        s3.CustomerGetResponseCreated = null;
        using var recovered = await api.Client.PutAsJsonAsync(route, Destination(), token);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(s3.CustomerObjects.Count).IsEqualTo(0);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Exact_probe_bytes_succeed_across_fragmented_reads_and_cleanup(bool chunked)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Manual(s3, services));
        var database = await api.CreateManagedDatabaseAsync();
        var expected = Encoding.ASCII.GetBytes("TerraBase backup access check");
        using var body = new ProbeBody(expected, chunked);
        s3.CustomerContentOverride = key => IsProbe(key) ? new StreamContent(body) : null;
        s3.CustomerGetResponseCreated = (key, response) =>
        {
            if (IsProbe(key))
            {
                response.Headers.TransferEncodingChunked = chunked ? true : null;
            }
        };

        using var saved = await api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination(), token);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.BytesRead).IsEqualTo(expected.Length);
        await Assert.That(body.LargestRequest).IsLessThanOrEqualTo(expected.Length + 1);
        await Assert.That(body.Reads).IsGreaterThan(1);
        await Assert.That(body.Disposed).IsTrue();
        await Assert.That(s3.CustomerObjects.Count).IsEqualTo(0);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    private static bool IsProbe(string key) => key.Contains("/.probe/", StringComparison.Ordinal);

    private static object Destination() => new
    {
        endpoint = FakeS3.CustomerEndpoint,
        region = "test-region-1",
        bucket = FakeS3.CustomerBucket,
        accessKeyId = FakeS3.CustomerAccessKey,
        secretKey = FakeS3.CustomerSecretKey,
        mode = "customer",
    };

    private static void Manual(FakeS3 s3, IServiceCollection services)
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private sealed class ObserveRequest : IStartupFilter
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Error { get; private set; }
        public CancellationToken RequestToken { get; private set; }
        public int ResponseStatus { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Method != "PUT" || !context.Request.Path.Value!.EndsWith("/backup-destination", StringComparison.Ordinal))
                {
                    await continuation();
                    return;
                }

                RequestToken = context.RequestAborted;
                try
                {
                    await continuation();
                }
                catch (Exception error)
                {
                    Error = error;
                    throw;
                }
                finally
                {
                    ResponseStatus = context.Response.StatusCode;
                    Completed.TrySetResult();
                }
            });
            next(app);
        };
    }

    private sealed class StalledProbeBody(byte[] bytes) : MemoryStream(bytes)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return base.Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public void Release() => _release.TrySetResult();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Disposed |= disposing;
        }
    }

    private sealed class ProbeBody(byte[] bytes, bool chunked, bool failRead = false) : MemoryStream(bytes)
    {
        public override bool CanSeek => !chunked;
        public int BytesRead { get; private set; }
        public int LargestRequest { get; private set; }
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failRead)
            {
                throw new InvalidOperationException("probe stream failed before cleanup");
            }
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            Reads++;
            var read = base.Read(buffer.Span[..Math.Min(buffer.Length, 7)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Disposed |= disposing;
        }
    }
}
