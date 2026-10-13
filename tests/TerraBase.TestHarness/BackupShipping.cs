using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;

namespace TerraBase.Testing;

/// <summary>One in-process node backing up to the in-memory bucket, shipped only when a test runs a pass.</summary>
internal sealed class BackupShippingHost(ManagementApi api, FakeS3 s3, FakeTimeProvider time, ManagedDatabaseRef database, HttpClient data) : IAsyncDisposable
{
    public ManagementApi Api { get; } = api;

    public FakeS3 S3 { get; } = s3;

    public FakeTimeProvider Time { get; } = time;

    public ManagedDatabaseRef Database { get; } = database;

    public string File => $"v1/{Database.Organization}/{Database.Project}/{Database.Id}";

    private IContinuousBackup Backup => Api.Server.Services.GetRequiredService<IContinuousBackup>();

    /// <summary>
    /// A node with the database "main" and its collections "people" and "load", on Enterprise so the frozen test clock never
    /// exhausts a request budget.
    /// </summary>
    public static async Task<BackupShippingHost> StartAsync(Action<IServiceCollection>? services = null)
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var api = await ManagementApi.StartAsync(s3.Configure, collection =>
        {
            s3.Route(collection);
            ManualBackupHosting.Configure(collection);
            services?.Invoke(collection);
        }, time);
        try
        {
            var database = await api.CreateManagedDatabaseAsync("main");
            await api.SetPlanAsync(api.User, database.Organization, "enterprise");
            var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, "people");
            await ManagementApi.CreateCollectionAsync(data, database, "load");
            return new BackupShippingHost(api, s3, time, database, data);
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    /// <summary>Advances the clock, then runs one shipping pass of every file.</summary>
    public async Task ShipAsync(TimeSpan advance, CancellationToken cancellationToken)
    {
        Time.Advance(advance);
        await Backup.RunOnceAsync(cancellationToken);
    }

    /// <summary>Starts a shipping pass of every file without waiting for it.</summary>
    public Task StartPassAsync(CancellationToken cancellationToken) => Backup.RunOnceAsync(cancellationToken);

    public BackupFileStatus Status() => Backup.Status.Files.Single(file => file.File == File);

    /// <summary>Every segment of a generation in the bucket, in key (start, then time) order.</summary>
    public IReadOnlyList<SegmentKey> Segments(string generation) =>
        [.. S3.Objects.Keys.Where(key => key.StartsWith($"{File}/wal/{generation}/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(key => BackupLayout.ParseSegment(key)!)];

    public async Task PutAsync(string collection, string name, string text, CancellationToken cancellationToken)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(Database, collection, name)), new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The text of a document of the database named <paramref name="databaseName"/>; null when it does not exist.</summary>
    public async Task<string?> ReadAsync(string databaseName, string collection, string name, CancellationToken cancellationToken)
    {
        using var response = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(Database with { Name = databaseName }, collection, name)), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString();
    }

    /// <summary>Restores the database as of <paramref name="at"/> into a new database named <paramref name="name"/>; it must succeed.</summary>
    public async Task RestoreAsync(DateTimeOffset at, string name, CancellationToken cancellationToken)
    {
        using var response = await Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{Database.Path}/point-in-time/restore"), new { pointInTime = at, name }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(string.Create(CultureInfo.InvariantCulture, $"restore at {at:O}: {body}"));
    }

    /// <summary>The id of the project's database named <paramref name="name"/>, such as a restore.</summary>
    public async Task<string> DatabaseIdAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await Api.Client.GetAsync(ManagementApi.Uri($"/v1/management/organizations/{Database.Organization}/projects/{Database.Project}/databases"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var listing = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return listing.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

        public Task<string> MetricsAsync(CancellationToken cancellationToken) => Api.Server.Client.GetStringAsync(ManagementApi.Uri("/metrics"), cancellationToken);

    /// <summary>
    /// Segments start at 0 and ascend in start, never back in time: no two share a start, so none overlaps another. Reads
    /// in one pass (a shutdown flush) can share a time.
    /// </summary>
    public static async Task AssertContiguousAsync(IReadOnlyList<SegmentKey> segments)
    {
        await Assert.That(segments.Count).IsGreaterThan(0);
        await Assert.That(segments[0].Start).IsEqualTo(0);
        for (var index = 1; index < segments.Count; index++)
        {
            await Assert.That(segments[index].Start).IsGreaterThan(segments[index - 1].Start);
            await Assert.That(segments[index].At).IsGreaterThanOrEqualTo(segments[index - 1].At);
        }
    }

    public async ValueTask DisposeAsync()
    {
        data.Dispose();
        await Api.DisposeAsync();
    }
}

/// <summary>
/// Writes a backlog of large document updates while the database's first snapshot upload is held, then ships it: the
/// generation persists, the backlog ships as several segments that each end between transactions, and a restore at the
/// last write is exact. Fast runs it over one read budget; Slow over the 256 MiB that used to drop every generation.
/// </summary>
internal static class BackupBacklogScenario
{
    private const int Documents = 8;
    private const int DocumentChars = 900_000;

    public static async Task RunAsync(BackupShippingHost host, int writes, int minimumSegments, CancellationToken cancellationToken)
    {
        var bulk = new Faker().Random.String2(DocumentChars, "abcdefghijklmnopqrstuvwxyz");
        var upload = new HeldUpload($"{host.File}/snapshots/");
        host.S3.BeforePut = upload.BeforePutAsync;
        try
        {
            host.Time.Advance(TimeSpan.FromSeconds(10));
            var pass = host.StartPassAsync(cancellationToken);
            await upload.Entered.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
            for (var write = 0; write < writes; write++)
            {
                await host.PutAsync("load", Document(write), Text(write, bulk), cancellationToken);
            }

            upload.Release();
            await pass;
        }
        finally
        {
            // Shutdown ships every file once more; never hold that upload.
            host.S3.BeforePut = null;
            upload.Release();
        }

        var generation = host.Status().Generation;
        await Assert.That(generation).IsNotNull();

        // One read budget per pass: lag stays above zero while the backlog remains, and no pass drops the generation.
        var behind = 0;
        for (var pass = 0; pass < 2 * minimumSegments + 10; pass++)
        {
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
            var status = host.Status();
            await Assert.That(status.Generation).IsEqualTo(generation);
            await Assert.That(status.LastError).IsNull();
            if (status.LagSeconds == 0)
            {
                break;
            }

            behind++;
        }

        var shipped = host.Status();
        await Assert.That(shipped.LagSeconds).IsEqualTo(0);
        await Assert.That(shipped.PendingBytes).IsEqualTo(0);
        await Assert.That(behind).IsGreaterThanOrEqualTo(minimumSegments - 1);
        var segments = host.Segments(generation!);
        await Assert.That(segments.Count).IsGreaterThanOrEqualTo(minimumSegments);
        await BackupShippingHost.AssertContiguousAsync(segments);
        await Assert.That(await host.MetricsAsync(cancellationToken)).DoesNotContain("terrabase_backup_abandoned");

        // The first segment ends between transactions: its restore holds every document as of one cut of the writes.
        await host.RestoreAsync(segments[0].At, "backlog-first", cancellationToken);
        var cut = new int[Documents];
        for (var document = 0; document < Documents; document++)
        {
            var text = await host.ReadAsync("backlog-first", "load", Document(document), cancellationToken);
            cut[document] = text is null ? -1 : int.Parse(text.AsSpan(0, 4), CultureInfo.InvariantCulture);
        }

        var last = cut.Max();
        await Assert.That(last).IsLessThan(writes - 1);
        await Assert.That(string.Join(',', cut)).IsEqualTo(string.Join(',', Enumerable.Range(0, Documents).Select(document => LastWrite(document, last + 1))));

        // The last segment holds every write: its restore is exact.
        await host.RestoreAsync(segments[^1].At, "backlog-last", cancellationToken);
        for (var document = 0; document < Documents; document++)
        {
            await Assert.That(await host.ReadAsync("backlog-last", "load", Document(document), cancellationToken)).IsEqualTo(Text(LastWrite(document, writes), bulk));
        }
    }

    private static string Document(int write) => $"doc-{write % Documents}";

    // Every update differs from every other, so a restore that misses one cannot match by accident.
    private static string Text(int write, string bulk) => string.Create(CultureInfo.InvariantCulture, $"{write:D4}") + bulk[4..];

    // The last of the first `writes` writes that updated `document`, or -1.
    private static int LastWrite(int document, int writes) => writes <= document ? -1 : writes - 1 - (writes - 1 - document) % Documents;

    // Holds each PUT under the prefix until released: a snapshot upload as slow as the test's writes.
    private sealed class HeldUpload(string prefix)
    {
        private readonly TaskCompletionSource<string> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task BeforePutAsync(string key, CancellationToken cancellationToken)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _entered.TrySetResult(key);
                await _release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
