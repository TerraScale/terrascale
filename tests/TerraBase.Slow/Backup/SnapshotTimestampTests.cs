using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class SnapshotTimestampTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Initial_and_daily_snapshots_keep_the_pin_time_while_copying_allows_later_writes(bool daily)
    {
        var s3 = new FakeS3();
        var time = new ManualTime();
        var testCancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
        var cancellationToken = deadline.Token;
        var pause = new SnapshotPause(time, cancellationToken);
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock,
                new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            services.AddSingleton<IWalBackup>(provider => new PausedBackup(provider.GetRequiredService<TursoStorageEngine>(), pause));
        }, time);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        // Discover the system file before creating the tenant; only explicit passes ship.
        await backup.RunOnceAsync(cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        pause.Database = database;
        await api.SetPlanAsync(api.User, database.Organization, "pro", cancellationToken);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var faker = new Faker();
        var seeded = faker.Lorem.Sentence(12);
        var before = daily ? seeded + " before daily snapshot" : seeded;
        var updated = before + " updated after pin";
        var created = faker.Lorem.Sentence(12);
        await PutAsync("source", seeded);
        time.Advance(TimeSpan.FromSeconds(10));
        string? generation = null;
        if (daily)
        {
            await backup.RunOnceAsync(cancellationToken);
            generation = backup.Status.Files.Single(file => file.File == File(database)).Generation;
            await PutAsync("source", before);
            time.Advance(TimeSpan.FromDays(1));
        }

        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        pause.Enabled = true;
        var pinnedAt = time.GetUtcNow();
        Task? pass = null;
        Exception? primaryFailure = null;
        try
        {
            pass = backup.RunOnceAsync(cancellationToken);
            await Assert.That(await pause.Pinned.Task.WaitAsync(cancellationToken)).IsEqualTo(pinnedAt);
            await Assert.That(pass.IsCompleted).IsFalse();
            time.Advance(TimeSpan.FromSeconds(5));
            // Both HTTPS writes must finish with the pinned reader still open and the online copy blocked.
            await PutAsync("source", updated);
            await PutAsync("later", created);
            await Assert.That(pass.IsCompleted).IsFalse();
            await Assert.That(pause.Release.Task.IsCompleted).IsFalse();
            pause.Release.TrySetResult();
            await pass.WaitAsync(cancellationToken);

            var snapshots = s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray();
            await Assert.That(snapshots.Length).IsEqualTo(daily ? 2 : 1);
            var parts = snapshots[^1].Split('/')[^1].Split('-');
            await Assert.That(DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[0], CultureInfo.InvariantCulture))).IsEqualTo(pinnedAt);
            if (daily)
            {
                await Assert.That(parts[1]).IsEqualTo(generation);
            }

            using var statusResponse = await api.Server.Client.GetAsync(ManagementApi.Uri("/v1/backup"), cancellationToken);
            statusResponse.EnsureSuccessStatusCode();
            var status = (await statusResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("files")
                .EnumerateArray().Single(file => file.GetProperty("file").GetString() == File(database));
            await Assert.That(status.GetProperty("lastShippedAt").GetDateTimeOffset()).IsEqualTo(pinnedAt);
            await Assert.That(status.GetProperty("lastError").ValueKind).IsEqualTo(JsonValueKind.Null);
            using var windowResponse = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time"), cancellationToken);
            windowResponse.EnsureSuccessStatusCode();
            var window = await windowResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await Assert.That(window.GetProperty("latest").GetDateTimeOffset()).IsEqualTo(pinnedAt);
            await PreviewAsync(pinnedAt, 0);
            await RestoreAsync(pinnedAt, "at-pin", before, null);

            time.Advance(TimeSpan.FromSeconds(1));
            var shippedAt = time.GetUtcNow();
            await backup.RunOnceAsync(cancellationToken);
            await Assert.That(backup.Status.Files.Single(file => file.File == File(database)).LastShippedAt).IsEqualTo(shippedAt);
            await PreviewAsync(shippedAt, 1);
            await RestoreAsync(shippedAt, "after-ship", updated, created);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            // Release before server disposal even when a request fails or the pin wait expires.
            pause.Enabled = false;
            pause.Release.TrySetResult();
            if (pass is not null)
            {
                try
                {
                    await pass.WaitAsync(TimeSpan.FromSeconds(30), testCancellationToken);
                }
                catch (Exception error) when (primaryFailure is not null)
                {
                    primaryFailure.Data["BackupCompletionFailure"] = error;
                }
            }
        }

        async Task PutAsync(string name, string text)
        {
            using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        async Task PreviewAsync(DateTimeOffset at, int segments)
        {
            using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"), new { pointInTime = at }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var preview = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await Assert.That(preview.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(pinnedAt);
            await Assert.That(preview.GetProperty("segmentCount").GetInt32()).IsEqualTo(segments);
            await Assert.That(preview.GetProperty("latestSegmentAt").ValueKind == JsonValueKind.Null
                ? (DateTimeOffset?)null : preview.GetProperty("latestSegmentAt").GetDateTimeOffset()).IsEqualTo(segments == 0 ? null : at);
        }

        async Task RestoreAsync(DateTimeOffset at, string name, string expectedSource, string? expectedLater)
        {
            using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime = at, name }, cancellationToken);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var documents = $"/v1/projects/{database.Project}/databases/{name}/collections/people/documents/by-key?id=";
            using var source = await data.GetAsync(ManagementApi.Uri(documents + ManagementApi.DocumentId("source")), cancellationToken);
            source.EnsureSuccessStatusCode();
            await Assert.That((await source.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(expectedSource);
            using var later = await data.GetAsync(ManagementApi.Uri(documents + ManagementApi.DocumentId("later")), cancellationToken);
            if (expectedLater is null)
            {
                await Assert.That(later.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
            else
            {
                later.EnsureSuccessStatusCode();
                await Assert.That((await later.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(expectedLater);
            }
        }
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private sealed class SnapshotPause(TimeProvider time, CancellationToken testDeadline)
    {
        public ManagedDatabaseRef? Database { get; set; }

        public bool Enabled { get; set; }

        public TaskCompletionSource<DateTimeOffset> Pinned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AfterPinnedAsync(CancellationToken cancellationToken)
        {
            if (!Enabled)
            {
                return;
            }

            Pinned.TrySetResult(time.GetUtcNow());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, testDeadline);
            await Release.Task.WaitAsync(deadline.Token);
        }
    }

    private sealed class PausedBackup(TursoStorageEngine engine, SnapshotPause pause) : IWalBackup
    {
        public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
        {
            var tap = await engine.OpenTapAsync(database, cancellationToken);
            if (tap is TursoChangeTap turso && database is not null && pause.Database is { } selected
                && database.OrganizationId == selected.Organization && database.ProjectId == selected.Project && database.DatabaseId == selected.Id)
            {
                turso.AfterSnapshotPinned = pause.AfterPinnedAsync;
            }

            return tap;
        }

        public ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken) =>
            engine.RollForwardAsync(file, segments, cancellationToken);

        public LogCut CutLog(Stream segment, DateTimeOffset at, long? previous) => engine.CutLog(segment, at, previous);

        public DateTimeOffset? ReadMark(Stream segment) => engine.ReadMark(segment);

        public ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken) =>
            engine.ImportDatabaseAsync(database, file, cancellationToken);

        public ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken) => engine.ChecksumAsync(file, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

        public override long TimestampFrequency => _time.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

        public override long GetTimestamp() => _time.GetTimestamp();

        public void Advance(TimeSpan amount) => _time.Advance(amount);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _time.CreateTimer(callback, state, period == Timeout.InfiniteTimeSpan ? dueTime : Timeout.InfiniteTimeSpan, period);
    }
}
