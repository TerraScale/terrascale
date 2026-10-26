using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

/// <summary>
/// A live node's restore window ends at a time its backup proves restorable (A-7): restoring at the advertised latest time
/// succeeds and is exact, rather than answering 409.
/// </summary>
public sealed class RestoreWindowLatestTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task While_a_daily_snapshot_with_no_new_commits_uploads_the_latest_time_restores_exactly(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal) { ["seed"] = Faker.Lorem.Sentence(8), ["a"] = Faker.Lorem.Sentence(8) };
        await host.PutAsync("people", "seed", texts["seed"], cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await host.PutAsync("people", "a", texts["a"], cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var shipped = host.Segments(host.Status().Generation!)[^1].At;
        await Assert.That(await LatestAsync(host, cancellationToken)).IsEqualTo(shipped);

        // A day later the daily snapshot is pinned with nothing new committed, and its upload takes a while.
        var snapshots = $"{host.File}/snapshots/";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.S3.BeforePut = async (key, token) =>
        {
            if (key.StartsWith(snapshots, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var pass = Task.CompletedTask;
        try
        {
            host.Time.Advance(TimeSpan.FromDays(1));
            pass = host.StartPassAsync(cancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);

            var latest = await LatestAsync(host, cancellationToken);
            await RestoresAsync(host, latest, "during-daily", texts, [], cancellationToken);
            await Assert.That(latest).IsEqualTo(shipped);
        }
        finally
        {
            host.S3.BeforePut = null;
            release.TrySetResult();
            await pass;
        }

        // Once stored, the snapshot's own time is the latest, and restores.
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var stored = BackupLayout.ParseSnapshot(host.S3.Objects.Keys.Where(key => key.StartsWith(snapshots, StringComparison.Ordinal)).Order(StringComparer.Ordinal).Last())!;
        await Assert.That(await LatestAsync(host, cancellationToken)).IsEqualTo(stored.At);
        await RestoresAsync(host, stored.At, "after-daily", texts, [], cancellationToken);
    }

    [Test]
    public async Task When_a_commit_shares_the_newest_read_marks_second_the_latest_time_is_that_whole_second_and_restores_exactly(
        CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["seed"] = Faker.Lorem.Sentence(8),
            ["z"] = Faker.Lorem.Sentence(8),
            ["a"] = Faker.Lorem.Sentence(8),
        };
        await host.PutAsync("people", "seed", texts["seed"], cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var start = host.Time.GetUtcNow();
        var stamps = new Stamps(host);

        // z commits in the second after the snapshot, a in the second the next read starts in (Turso records whole seconds).
        await host.PutAsync("people", "z", texts["z"], cancellationToken);
        await stamps.StampAsync(start.AddSeconds(1), cancellationToken);
        await host.PutAsync("people", "a", texts["a"], cancellationToken);
        await stamps.StampAsync(start.AddSeconds(2), cancellationToken);

        // The read takes time: it starts (its read mark) a little before it ends (its segment's time), within a's second.
        host.Time.Advance(TimeSpan.FromMilliseconds(2400));
        host.Time.AutoAdvanceAmount = TimeSpan.FromMilliseconds(1);
        try
        {
            await host.StartPassAsync(cancellationToken);
        }
        finally
        {
            host.Time.AutoAdvanceAmount = TimeSpan.Zero;
        }

        var segment = host.Segments(host.Status().Generation!).Single();
        await Assert.That(segment.At).IsGreaterThan(start.AddSeconds(2));
        var latest = await LatestAsync(host, cancellationToken);
        await RestoresAsync(host, latest, "at-latest", new(texts.Where(pair => pair.Key != "a"), StringComparer.Ordinal), ["a"], cancellationToken);
        await Assert.That(latest).IsEqualTo(start.AddSeconds(2));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(9)]
    public async Task A_latest_cut_with_submillisecond_precision_restores_without_the_shippers_local_progress(int segments, CancellationToken cancellationToken)
    {
        RemoteProgress? progress = null;
        await using var host = await BackupShippingHost.StartAsync(services => services.AddSingleton<IPointInTimeRestore>(provider =>
        {
            progress = new RemoteProgress(provider.GetRequiredService<IContinuousBackup>());
            return ActivatorUtilities.CreateInstance<PointInTimeRestore>(provider, progress);
        }));
        host.Time.Advance(TimeSpan.FromTicks(979627));
        var texts = new Dictionary<string, string>(StringComparer.Ordinal) { ["seed"] = Faker.Lorem.Sentence(8) };
        await host.PutAsync("people", "seed", texts["seed"], cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        for (var index = 0; index < segments; index++)
        {
            var id = "document-" + index;
            texts.Add(id, Faker.Lorem.Sentence(8));
            await host.PutAsync("people", id, texts[id], cancellationToken);
            host.Time.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(979627));
            host.Time.AutoAdvanceAmount = TimeSpan.FromTicks(1);
            try
            {
                await host.StartPassAsync(cancellationToken);
            }
            finally
            {
                host.Time.AutoAdvanceAmount = TimeSpan.Zero;
            }
        }

        await Assert.That(host.Segments(host.Status().Generation!).Count).IsEqualTo(segments);
        var latest = await LatestAsync(host, cancellationToken);
        // A restore routed to another region has only the bucket's millisecond timestamps, not this member's progress.
        progress!.Away = true;
        await RestoresAsync(host, latest, "fractional-latest", texts, [], cancellationToken);
        await Assert.That(latest).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(latest.ToUnixTimeMilliseconds()));
    }

    private sealed class RemoteProgress(IContinuousBackup backup) : IContinuousBackup
    {
        public bool Away { get; set; }
        public BackupStatus Status => backup.Status;
        public DateTimeOffset? LastShipped(TenantContext database, Ulid? generation = null) => Away ? null : backup.LastShipped(database, generation);
        public Task RunOnceAsync(CancellationToken cancellationToken) => backup.RunOnceAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset> LatestAsync(BackupShippingHost host, CancellationToken cancellationToken)
    {
        using var response = await host.Api.Client.GetAsync(ManagementApi.Uri($"{host.Database.Path}/point-in-time"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("latest").GetDateTimeOffset();
    }

    // Restores at the time into a new database holding exactly the present documents, with their texts, and none of the absent.
    private static async Task RestoresAsync(
        BackupShippingHost host, DateTimeOffset at, string name, Dictionary<string, string> present, string[] absent, CancellationToken cancellationToken)
    {
        using (var response = await host.Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{host.Database.Path}/point-in-time/restore"), new { pointInTime = at, name }, cancellationToken))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because($"restore at {at:O}: {body}");
        }

        foreach (var (document, text) in present)
        {
            await Assert.That(await host.ReadAsync(name, "people", document, cancellationToken)).IsEqualTo(text);
        }

        foreach (var document in absent)
        {
            await Assert.That(await host.ReadAsync(name, "people", document, cancellationToken)).IsNull();
        }
    }

    // Turso stamps rows with the machine's whole-second clock: sets the second of every row captured since the last call.
    private sealed class Stamps(BackupShippingHost host)
    {
        private long _stamped;

        public async Task StampAsync(DateTimeOffset second, CancellationToken cancellationToken)
        {
            var tenant = new TenantContext(host.Database.Organization, PlanTier.Enterprise, host.Database.Project, host.Database.Id);
            var store = (TursoDatabaseStore)(await host.Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, cancellationToken))!;
            _stamped = await store.WithWriterAsync(writer =>
            {
                using (var update = writer.Connection.CreateCommand())
                {
                    update.CommandText = "UPDATE turso_cdc SET change_time = $time WHERE change_id > $after;";
                    update.Parameters.AddWithValue("$time", second.ToUnixTimeSeconds());
                    update.Parameters.AddWithValue("$after", _stamped);
                    update.ExecuteNonQuery();
                }

                using var newest = writer.Connection.CreateCommand();
                newest.CommandText = "SELECT coalesce(max(change_id), 0) FROM turso_cdc;";
                return (long)newest.ExecuteScalar()!;
            }, "a test", cancellationToken);
        }
    }
}
