using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class DataKeyOverlapTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Eight_prefetched_platform_keys_allow_an_overlapping_restore_to_wait_or_cancel_without_losing_capacity(bool cancel)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var overlapping = CancellationTokenSource.CreateLinkedTokenSource(requests.Token);
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var request = new ObserveRestore();
        await using var api = await StartAsync(s3, time, request);
        var source = await api.CreateManagedDatabaseAsync("source");
        var other = await api.CreateManagedDatabaseAsync("other");
        request.Path = other.Path + "/point-in-time/restore";
        foreach (var database in new[] { source, other })
        {
            await api.SetPlanAsync(api.User, database.Organization, "pro", token);
        }

        using var data = api.DataClient(await api.Server.IssueAsync(source.Organization, ["documents:read", "documents:write"], token));
        using var otherData = api.DataClient(await api.Server.IssueAsync(other.Organization, ["documents:read", "documents:write"], token));
        var before = new Faker().Lorem.Sentence(12);
        var otherText = new Faker().Lorem.Sentence(12);
        await SeedAsync(data, source, before, token);
        await SeedAsync(otherData, other, otherText, token);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var otherSnapshot = s3.Objects.Keys.Single(key => key.StartsWith(File(other) + "/snapshots/", StringComparison.Ordinal));
        var otherKey = Keys(other) + Encoding.ASCII.GetString(s3.Objects[otherSnapshot], 4, 26);
        var otherReads = s3.ObserveKey(otherKey);
        var stripes = new HashSet<int> { Stripe(otherKey) };
        var segmentKeys = new HashSet<string>(StringComparer.Ordinal);
        var observed = new List<FakeS3.KeyRequests>(8);
        var documents = new List<(string Name, string Text)>(9) { ("before", before) };
        for (var index = 0; index < 8; index++)
        {
            time.Advance(TimeSpan.FromSeconds(61));
            var key = DistinctKey(Keys(source), time.GetUtcNow(), stripes);
            var material = RandomNumberGenerator.GetBytes(32);
            try
            {
                s3.StoreObject(key, Encoding.ASCII.GetBytes(s3.Wrap(material)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }

            segmentKeys.Add(key);
            observed.Add(s3.ObserveKey(key));
            var name = "segment-" + index;
            var text = new Faker().Lorem.Sentence(12);
            documents.Add((name, text));
            await PutAsync(data, source, name, text, token);
            await backup.RunOnceAsync(token);
            var segment = s3.Objects.Single(item => item.Key.StartsWith(File(source) + "/wal/", StringComparison.Ordinal)
                && Encoding.ASCII.GetString(item.Value, 4, 26) == key[Keys(source).Length..]);
            await Assert.That(BackupLayout.ParseSegment(segment.Key)!.At).IsEqualTo(time.GetUtcNow());
        }

        await Assert.That(s3.Objects.Keys.Count(key => key.StartsWith(File(source) + "/wal/", StringComparison.Ordinal))).IsEqualTo(8);
        var sourceAt = await LatestAsync(api, source, token);
        var otherAt = await LatestAsync(api, other, token);
        // Expire both shared platform caches without leaving either pro retention window.
        time.Advance(TimeSpan.FromHours(25));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var bodies = new ConcurrentBag<StalledKeyBody>();
        using var header = new SnapshotHeaderBody(s3.Objects[otherSnapshot]);
        var restores = new List<Task<HttpResponseMessage>>(4);
        s3.PlatformContentOverride = key =>
        {
            if (segmentKeys.Contains(key))
            {
                return Content(AddBody(key));
            }

            return key == otherSnapshot ? Content(header) : null;
        };
        try
        {
            var first = RestoreAsync(api, source, sourceAt, "prefetched", requests.Token);
            restores.Add(first);
            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(Volatile.Read(ref entered)).IsEqualTo(8);
            await Assert.That(first.IsCompleted).IsFalse();
            var second = RestoreAsync(api, other, otherAt, "overlapping", overlapping.Token);
            restores.Add(second);
            await header.Requested.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            // Complete the registered header continuation while all eight slots remain occupied.
            // A fail-fast key read disposes this snapshot and retries; waiting must retain it instead.
            header.ReleaseHeader();
            await Assert.That(header.Disposed.Task.IsCompleted).IsFalse();
            await Assert.That(second.IsCompleted).IsFalse();
            await Assert.That(otherReads.Gets).IsEqualTo(0);
            await Assert.That(release.Task.IsCompleted).IsFalse();

            if (cancel)
            {
                await overlapping.CancelAsync();
                await header.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                // Cleanup can unregister the completed header's callback; observe the aborted request finishing.
                await request.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                await Assert.ThrowsAsync<OperationCanceledException>(() => second.WaitAsync(token));
                await Assert.That(otherReads.Gets).IsEqualTo(0);
                await Assert.That(first.IsCompleted).IsFalse();
            }

            release.TrySetResult();
            using (var restored = await first.WaitAsync(token))
            {
                await AssertRestoredAsync(restored, data, source, [.. documents], token);
            }

            s3.PlatformContentOverride = key => segmentKeys.Contains(key)
                ? Content(AddBody(key)) : null;
            if (cancel)
            {
                var recovering = RestoreAsync(api, other, otherAt, "recovered", requests.Token);
                restores.Add(recovering);
                using var recovered = await recovering.WaitAsync(token);
                await AssertRestoredAsync(recovered, otherData, other, [("before", otherText)], token);
                // A cancelled slot waiter must leave all eight admissions available on the next cold restore.
                time.Advance(TimeSpan.FromHours(25));
                release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                saturated = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var recovery = RestoreAsync(api, source, sourceAt, "capacity-recovered", requests.Token);
                restores.Add(recovery);
                await saturated.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                await Assert.That(Volatile.Read(ref entered)).IsEqualTo(16);
                await Assert.That(recovery.IsCompleted).IsFalse();
                release.TrySetResult();
                using var restored = await recovery.WaitAsync(token);
                await AssertRestoredAsync(restored, data, source, [.. documents], token);
            }
            else
            {
                using var restored = await second.WaitAsync(token);
                await AssertRestoredAsync(restored, otherData, other, [("before", otherText)], token);
            }

            await Assert.That(otherReads.Gets).IsEqualTo(1);
            await Assert.That(otherReads.Unwraps).IsEqualTo(1);
            await Assert.That(bodies.Count).IsEqualTo(cancel ? 16 : 8);
            await Assert.That(bodies.All(body => body.Disposed)).IsTrue();
            foreach (var reads in observed)
            {
                // Setup shipping read each seeded key once; cold restores read it once per wave.
                await Assert.That(reads.Gets).IsEqualTo(cancel ? 3 : 2);
                await Assert.That(reads.Unwraps).IsEqualTo(cancel ? 3 : 2);
            }

            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            s3.PlatformContentOverride = null;
            release.TrySetResult();
            await requests.CancelAsync();
            await ((Task)Task.WhenAll(restores)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var restore in restores)
            {
                if (restore.IsCompletedSuccessfully)
                {
                    using var response = await restore;
                }
            }

            foreach (var body in bodies)
            {
                body.Dispose();
            }
        }

        StalledKeyBody AddBody(string key)
        {
            var body = new StalledKeyBody(s3.Objects[key], release.Task, () =>
            {
                if (Interlocked.Increment(ref entered) % 8 == 0)
                {
                    saturated.TrySetResult();
                }
            });
            bodies.Add(body);
            return body;
        }
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time, ObserveRestore request)
    {
        var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddSingleton<IStartupFilter>(request);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

    private static async Task SeedAsync(HttpClient data, ManagedDatabaseRef database, string text, CancellationToken token)
    {
        using var collection = await data.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/collections"), new { name = "people" }, token);
        collection.EnsureSuccessStatusCode();
        await PutAsync(data, database, "before", text, token);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text, CancellationToken token)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<DateTimeOffset> LatestAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token)
    {
        using var response = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time"), token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("latest").GetDateTimeOffset();
    }

    private static Task<HttpResponseMessage> RestoreAsync(ManagementApi api, ManagedDatabaseRef database, DateTimeOffset at, string name, CancellationToken token) =>
        api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime = at, name }, token);

    private static async Task AssertRestoredAsync(HttpResponseMessage response, HttpClient data, ManagedDatabaseRef source,
        (string Name, string Text)[] documents, CancellationToken token)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        await Assert.That(body.GetProperty("branchedFrom").GetString()).IsEqualTo(source.Id);
        var restored = new ManagedDatabaseRef(source.Organization, source.Project, body.GetProperty("id").GetString()!, body.GetProperty("name").GetString()!);
        foreach (var (name, text) in documents)
        {
            using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", name)), token);
            read.EnsureSuccessStatusCode();
            await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        }
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
    private static string Keys(ManagedDatabaseRef database) => $"v1/keys/{database.Organization}/{database.Project}/";
    private static int Stripe(string key) => (StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % 64;

    private static string DistinctKey(string prefix, DateTimeOffset at, HashSet<int> stripes)
    {
        // Choose test keys that fill the pool rather than serialize on a colliding key stripe.
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var key = prefix + Ulid.NewUlid(at);
            if (stripes.Add(Stripe(key)))
            {
                return key;
            }
        }

        throw new InvalidOperationException("could not choose a distinct backup key stripe");
    }

    private static StreamContent Content(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = body.Length;
        return content;
    }

    private sealed class ObserveRestore : IStartupFilter
    {
        public string? Path { get; set; }
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                var observed = context.Request.Path.Value == Path;
                try
                {
                    await continuation();
                }
                finally
                {
                    if (observed && context.RequestAborted.IsCancellationRequested)
                    {
                        Cancelled.TrySetResult();
                    }
                }
            });
            next(app);
        };
    }

    private sealed class StalledKeyBody(byte[] content, Task release, Action entered) : MemoryStream(content, writable: false)
    {
        private int _entered;
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public override bool CanSeek => false;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _entered, 1) == 0)
            {
                entered();
            }

            await release.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Interlocked.Exchange(ref _disposed, 1);
            }
        }
    }

    // The first header read resumes inline, so the test observes a suspended key wait or a disposed failed attempt.
    private sealed class SnapshotHeaderBody(byte[] content) : MemoryStream(content, writable: false), IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _header;
        private Memory<byte> _buffer;
        private CancellationTokenRegistration _cancellation;
        private int _entered;
        private int _completed;
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;

        public void ReleaseHeader()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _header.SetResult(base.Read(_buffer.Span));
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _entered, 1) != 0)
            {
                return base.ReadAsync(buffer, cancellationToken);
            }

            _buffer = buffer;
            _cancellation = cancellationToken.Register(() =>
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    _header.SetException(new OperationCanceledException(cancellationToken));
                }
            });
            return new(this, _header.Version);
        }

        int IValueTaskSource<int>.GetResult(short token) => _header.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _header.GetStatus(token);

        void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _header.OnCompleted(continuation, state, token, flags);
            Requested.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _cancellation.Dispose();
                Disposed.TrySetResult();
            }
        }
    }
}
