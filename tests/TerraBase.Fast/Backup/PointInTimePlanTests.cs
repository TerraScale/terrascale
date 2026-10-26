using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class PointInTimePlanTests
{
    [Test]
    public async Task Project_tokens_preview_by_database_name_with_read_scope_and_tenant_isolation()
    {
        var s3 = new FakeS3();
        var time = Clock();
        var probe = new StorageProbe();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Route(s3, probe, services), time);
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var at = time.GetUtcNow();
        probe.Listings[File(database) + "/snapshots/"] =
            [string.Create(CultureInfo.InvariantCulture, $"{File(database)}/snapshots/{at.ToUnixTimeMilliseconds():D13}-{Ulid.NewUlid()}-{0:D16}")];
        var route = Uri(database.Data + "/point-in-time/plan");
        var downloads = probe.Downloads;
        try
        {
            using var reader = ProjectClient(database.Project, "databases:read");
            using var allowed = await reader.PostAsJsonAsync(route, new { pointInTime = at });
            allowed.EnsureSuccessStatusCode();
            var plan = await allowed.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(at);
            await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(0);
            using var insufficient = ProjectClient(database.Project, "documents:read");
            using var forbidden = await insufficient.PostAsJsonAsync(route, new { pointInTime = at });
            await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            using var otherProject = ProjectClient(EntityIds.New().ToString(), "databases:read");
            using var hiddenProject = await otherProject.PostAsJsonAsync(route, new { });
            await Assert.That(hiddenProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var restricted = ProjectClient(database.Project, "databases:read", "another");
            using var hiddenDatabase = await restricted.PostAsJsonAsync(route, new { });
            await Assert.That(hiddenDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var anonymous = api.WithToken(null);
            using var unauthenticated = await anonymous.PostAsJsonAsync(route, new { pointInTime = at });
            await Assert.That(unauthenticated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(probe.Downloads).IsEqualTo(downloads);
        }
        finally
        {
            probe.Listings.Clear();
        }

        HttpClient ProjectClient(string project, string scope, string? name = null)
        {
            return api.WithToken(api.Issuer.Mint(api.User, token =>
            {
                token.Claims["project_id"] = project;
                token.Claims["scope"] = scope;
                if (name is not null)
                {
                    token.Claims["database"] = name;
                }
            }));
        }
    }

    [Test]
    public async Task Earlier_and_latest_plans_select_the_restore_snapshots_and_segments_without_changing_any_data()
    {
        var s3 = new FakeS3();
        var time = Clock();
        var probe = new StorageProbe();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Route(s3, probe, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "pro");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence();
        var document = ManagementApi.DocumentPath(database, "people", "source");
        (await data.PutAsJsonAsync(Uri(document), new { text })).EnsureSuccessStatusCode();
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var first = time.GetUtcNow();
        (await data.PutAsJsonAsync(Uri(document), new { text = text + " updated" })).EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var earlier = time.GetUtcNow();
        time.Advance(TimeSpan.FromDays(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var second = time.GetUtcNow();
        (await data.PutAsJsonAsync(Uri(document), new { text = text + " latest" })).EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        // Only explicit passes ship: periodic timers stay dormant while the preview timeout still advances.
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var latest = time.GetUtcNow();
        var objects = s3.Objects.ToDictionary(item => item.Key, item => Convert.ToHexString(SHA256.HashData(item.Value)), StringComparer.Ordinal);
        var files = Files(api);
        var catalog = await api.Client.GetStringAsync(Uri(database.ProjectPath + "/databases"));
        var source = await data.GetStringAsync(Uri(document));
        var writes = probe.Writes;
        var downloads = probe.Downloads;
        var generated = s3.GeneratedKeys;
        using var window = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/point-in-time")));

        foreach (var (at, snapshotAt) in new[] { (first, first), (earlier, first), (second, second), (latest, second) })
        {
            using var response = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = at });
            response.EnsureSuccessStatusCode();
            var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
            var snapshot = Snapshot(s3, database, at);
            var segments = Segments(s3, database, snapshot, at);
            await Assert.That(plan.GetProperty("pointInTime").GetDateTimeOffset()).IsEqualTo(at);
            await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(snapshotAt);
            await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(segments.Count);
            await Assert.That(plan.GetProperty("latestSegmentAt").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : plan.GetProperty("latestSegmentAt").GetDateTimeOffset())
                .IsEqualTo(segments.Count == 0 ? null : segments.Max());
            await Assert.That(plan.GetProperty("window").GetRawText()).IsEqualTo(window.RootElement.GetRawText());
            await Assert.That(plan.EnumerateObject().Select(property => property.Name))
                .IsEquivalentTo(["window", "pointInTime", "snapshotAt", "latestSegmentAt", "segmentCount", "historyCheck"]);
            await Assert.That(plan.GetProperty("historyCheck").GetString()).IsEqualTo("metadata");
        }

        await Assert.That(probe.Writes).IsEqualTo(writes);
        await Assert.That(probe.Downloads).IsEqualTo(downloads);
        await Assert.That(s3.GeneratedKeys).IsEqualTo(generated);
        await Assert.That(s3.Objects.ToDictionary(item => item.Key, item => Convert.ToHexString(SHA256.HashData(item.Value)), StringComparer.Ordinal)).IsEquivalentTo(objects);
        await Assert.That(Files(api)).IsEquivalentTo(files);
        await Assert.That(await api.Client.GetStringAsync(Uri(database.ProjectPath + "/databases"))).IsEqualTo(catalog);
        await Assert.That(await data.GetStringAsync(Uri(document))).IsEqualTo(source);

        // Actual restores through the same selector still apply the matching snapshot and segments.
        foreach (var (at, name, expected) in new[] { (earlier, "earlier", text + " updated"), (latest, "latest", text + " latest") })
        {
            using var restored = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new { pointInTime = at, name });
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var read = await data.GetAsync(Uri($"/v1/projects/{database.Project}/databases/{name}/collections/people/documents/by-key?id={ManagementApi.DocumentId("source")}"));
            await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Validation_is_access_first_and_viewers_can_read_the_plan()
    {
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var viewer = api.As(await api.AddMemberAsync(api.Client, database.Organization, "viewer"));
        using var outsider = api.As(EntityIds.New().ToString());
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var snapshot = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        s3.FailPut = _ => true;
        time.Advance(TimeSpan.FromSeconds(1));
        using var window = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/point-in-time")));
        var earliest = window.RootElement.GetProperty("earliest").GetDateTimeOffset();
        var latest = window.RootElement.GetProperty("latest").GetDateTimeOffset();
        await Assert.That(earliest).IsEqualTo(snapshot);
        await Assert.That(latest.AddTicks(1)).IsLessThan(time.GetUtcNow());
        var retained = s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/", StringComparison.Ordinal)).ToArray();
        await Assert.That(retained.Length).IsGreaterThan(0);
        var route = Uri(database.Path + "/point-in-time/plan");
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(route, new { }), HttpStatusCode.BadRequest, "invalid_request", "pointInTime is required");
        // Outside the window but neither after now nor before the retention window, the backup does not hold the time: 409.
        foreach (var at in new[] { earliest.AddTicks(-1), latest.AddTicks(1), time.GetUtcNow() })
        {
            await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(route, new { pointInTime = at }), HttpStatusCode.Conflict, "backup_history_incomplete",
                "continuous backup cannot prove this database's history complete up to pointInTime");
        }

        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(route, new { pointInTime = time.GetUtcNow().AddTicks(1) }), HttpStatusCode.BadRequest, "invalid_request",
            string.Create(CultureInfo.InvariantCulture, $"pointInTime must lie between the restore window's earliest and latest times: {earliest:O} to {latest:O}"));

        using var missing = await outsider.PostAsJsonAsync(route, new { });
        using var invalid = await outsider.PostAsJsonAsync(route, new { pointInTime = time.GetUtcNow().AddDays(1) });
        using var hidden = await outsider.PostAsJsonAsync(route, new { pointInTime = snapshot });
        using var allowed = await viewer.PostAsJsonAsync(route, new { pointInTime = latest });
        using var anonymous = api.WithToken(null);
        using var unauthenticated = await anonymous.PostAsJsonAsync(route, new { pointInTime = snapshot });
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(hidden.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await allowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(snapshot);
        await Assert.That(unauthenticated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // Hobby retention includes the stored endpoint exactly at its one-day boundary.
        time.Advance(TimeSpan.FromDays(1) - (time.GetUtcNow() - latest));
        using var boundary = await viewer.PostAsJsonAsync(route, new { pointInTime = latest });
        boundary.EnsureSuccessStatusCode();
        var boundaryPlan = await boundary.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(boundaryPlan.GetProperty("window").GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(latest);
        await Assert.That(boundaryPlan.GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(latest);

        // Retained objects cannot keep a fully expired plan window available.
        time.Advance(TimeSpan.FromSeconds(1));
        foreach (var at in new[] { latest, time.GetUtcNow() })
        {
            await ErrorAssert.ErrorAsync(viewer.PostAsJsonAsync(route, new { pointInTime = at }), HttpStatusCode.NotFound, "not_found",
                "continuous backup holds nothing of this database within its plan's retention window");
        }
        await Assert.That(s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/", StringComparison.Ordinal))).IsEquivalentTo(retained);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Backup_off_or_without_a_snapshot_returns_not_found(bool enabled)
    {
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await ManagementApi.StartAsync(enabled ? s3.Configure : null, enabled ? services =>
        {
            s3.Route(services);
            // Keep the snapshot absent without racing background shipping or failing the final shutdown flush.
            ManualBackupHosting.Configure(services);
        } : null, time);
        var database = await api.CreateManagedDatabaseAsync();
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = time.GetUtcNow() }),
            HttpStatusCode.NotFound, "not_found", "continuous backup holds nothing of this database");
        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/restore"), new { pointInTime = time.GetUtcNow(), name = "main-empty" }),
            HttpStatusCode.NotFound, "not_found", "continuous backup holds nothing of this database");
    }

    [Test]
    [Arguments(5000)]
    [Arguments(9999)]
    public async Task Uncached_latest_generation_is_scanned_once_through_later_segments_and_the_budget_boundary(int segmentCount)
    {
        var s3 = new FakeS3();
        var time = Clock();
        var probe = new StorageProbe();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Route(s3, probe, services), time);
        // Finish startup discovery before creating the database; its LastShipped stays null without another pass.
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
        var database = await api.CreateManagedDatabaseAsync();
        var snapshot = time.GetUtcNow().AddMilliseconds(-segmentCount);
        var generation = Ulid.NewUlid();
        var prefix = File(database) + "/wal/" + generation + "/";
        probe.Listings[File(database) + "/snapshots/"] = [string.Create(CultureInfo.InvariantCulture, $"{File(database)}/snapshots/{snapshot.ToUnixTimeMilliseconds():D13}-{generation}-{0:D16}")];
        probe.Listings[prefix] = [.. Enumerable.Range(0, segmentCount)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"{prefix}{i:D16}-{snapshot.AddMilliseconds(i + 1).ToUnixTimeMilliseconds():D13}"))];
        try
        {
            foreach (var count in new[] { 0, segmentCount / 2, segmentCount })
            {
                var at = snapshot.AddMilliseconds(count);
                var listings = probe.SegmentListings;
                using var response = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = at });
                response.EnsureSuccessStatusCode();
                var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
                await Assert.That(plan.GetProperty("pointInTime").GetDateTimeOffset()).IsEqualTo(at);
                await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(snapshot);
                await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(count);
                await Assert.That(plan.GetProperty("latestSegmentAt").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : plan.GetProperty("latestSegmentAt").GetDateTimeOffset())
                    .IsEqualTo(count == 0 ? null : at);
                await Assert.That(plan.GetProperty("window").GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(snapshot);
                // No listed segment holds a read mark (none is even stored): only the snapshot's time is proven.
                await Assert.That(plan.GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(snapshot);
                await Assert.That(probe.SegmentListings).IsEqualTo(listings + 1);
            }

            // Frame order is authoritative: later frames with regressed timestamps do not pass restore's first cutoff.
            probe.Listings[prefix][0] = string.Create(CultureInfo.InvariantCulture, $"{prefix}{0:D16}-{snapshot.AddMilliseconds(10).ToUnixTimeMilliseconds():D13}");
            using var regressed = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = snapshot.AddMilliseconds(5) });
            regressed.EnsureSuccessStatusCode();
            var regressedPlan = await regressed.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(regressedPlan.GetProperty("segmentCount").GetInt32()).IsEqualTo(0);
            await Assert.That(regressedPlan.GetProperty("latestSegmentAt").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(regressedPlan.GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(snapshot);

            if (segmentCount == 9999)
            {
                // Even after the requested time, malformed raw metadata is the 10001st object.
                probe.Listings[prefix] = [.. probe.Listings[prefix], prefix + "malformed"];
                await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = snapshot }),
                    HttpStatusCode.ServiceUnavailable, "pitr_plan_limit", "the point-in-time plan metadata scan exceeds 10000 objects");
            }
        }
        finally
        {
            probe.Listings.Clear();
        }
    }

    [Test]
    public async Task Uncached_earlier_generation_uses_its_segments_and_the_newest_generations_window()
    {
        var s3 = new FakeS3();
        var time = Clock();
        var probe = new StorageProbe();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Route(s3, probe, services), time);
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
        var database = await api.CreateManagedDatabaseAsync();
        var first = time.GetUtcNow().AddSeconds(-4);
        var at = first.AddSeconds(1);
        var last = at.AddSeconds(1);
        var older = Ulid.NewUlid();
        var newer = Ulid.NewUlid();
        probe.Listings[File(database) + "/snapshots/"] =
        [
            string.Create(CultureInfo.InvariantCulture, $"{File(database)}/snapshots/{first.ToUnixTimeMilliseconds():D13}-{older}-{0:D16}"),
            string.Create(CultureInfo.InvariantCulture, $"{File(database)}/snapshots/{last.ToUnixTimeMilliseconds():D13}-{newer}-{0:D16}")
        ];
        probe.Listings[File(database) + "/wal/" + older + "/"] =
        [
            string.Create(CultureInfo.InvariantCulture, $"{File(database)}/wal/{older}/{0:D16}-{at.ToUnixTimeMilliseconds():D13}"),
            string.Create(CultureInfo.InvariantCulture, $"{File(database)}/wal/{older}/{1:D16}-{last.ToUnixTimeMilliseconds():D13}")
        ];
        probe.Listings[File(database) + "/wal/" + newer + "/"] =
            [string.Create(CultureInfo.InvariantCulture, $"{File(database)}/wal/{newer}/{0:D16}-{time.GetUtcNow().ToUnixTimeMilliseconds():D13}")];
        try
        {
            var listings = probe.SegmentListings;
            using var response = await api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = at });
            response.EnsureSuccessStatusCode();
            var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(plan.GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(first);
            await Assert.That(plan.GetProperty("segmentCount").GetInt32()).IsEqualTo(1);
            await Assert.That(plan.GetProperty("latestSegmentAt").GetDateTimeOffset()).IsEqualTo(at);
            await Assert.That(plan.GetProperty("window").GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(first);
            // The newest generation's segment holds no read mark (none is stored): only its snapshot's time is proven.
            await Assert.That(plan.GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(last);
            await Assert.That(probe.SegmentListings).IsEqualTo(listings + 2);
        }
        finally
        {
            probe.Listings.Clear();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Object_store_listing_failures_return_a_sanitized_unavailable_response(bool network)
    {
        var s3 = new FakeS3();
        var time = Clock();
        var probe = new StorageProbe { NetworkFailure = network };
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Route(s3, probe, services), time);
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(CancellationToken.None);
        var database = await api.CreateManagedDatabaseAsync();
        var at = time.GetUtcNow();
        probe.Listings[File(database) + "/snapshots/"] =
            [string.Create(CultureInfo.InvariantCulture, $"{File(database)}/snapshots/{at.ToUnixTimeMilliseconds():D13}-{Ulid.NewUlid()}-{0:D16}")];
        try
        {
            foreach (var prefix in new[] { File(database) + "/snapshots/", File(database) + "/wal/" })
            {
                probe.FailPrefix = prefix;
                await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = at }).WaitAsync(TimeSpan.FromSeconds(10)),
                    HttpStatusCode.ServiceUnavailable, "pitr_plan_unavailable", "the point-in-time plan metadata could not be read");
            }
        }
        finally
        {
            probe.FailPrefix = null;
            probe.Listings.Clear();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Raw_snapshot_and_segment_metadata_share_the_scan_budget(bool segments)
    {
        var s3 = new FakeS3();
        var time = Clock();
        var probe = new StorageProbe();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Route(s3, probe, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        probe.OversizePrefix = File(database) + (segments ? "/wal/" : "/snapshots/");
        probe.OversizeItems = segments ? 10000 : 10001;
        try
        {
            // One snapshot plus 10000 malformed segments exceeds the total budget; malformed snapshots count too.
            await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = time.GetUtcNow() }),
                HttpStatusCode.ServiceUnavailable, "pitr_plan_limit", "the point-in-time plan metadata scan exceeds 10000 objects");
        }
        finally
        {
            probe.OversizePrefix = null;
        }
    }

    [Test]
    public async Task Metadata_listing_times_out_on_the_injected_clock()
    {
        var s3 = new FakeS3();
        var time = Clock();
        var probe = new StorageProbe();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => Route(s3, probe, services), time);
        var database = await api.CreateManagedDatabaseAsync();
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        probe.BlockPrefix = File(database) + "/snapshots/";
        try
        {
            var request = api.Client.PostAsJsonAsync(Uri(database.Path + "/point-in-time/plan"), new { pointInTime = time.GetUtcNow() });
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            time.Advance(TimeSpan.FromSeconds(10));
            await ErrorAssert.ErrorAsync(request.WaitAsync(TimeSpan.FromSeconds(10)), HttpStatusCode.ServiceUnavailable,
                "pitr_plan_timeout", "the point-in-time plan metadata scan timed out");
        }
        finally
        {
            probe.BlockPrefix = null;
        }
    }

    private static ManualTime Clock() => new();

    private static Uri Uri(string path) => ManagementApi.Uri(path);

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    private static string[] Snapshot(FakeS3 s3, ManagedDatabaseRef database, DateTimeOffset at) =>
        s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(key => key.Split('/')[^1].Split('-'))
            .Last(parts => long.Parse(parts[0], CultureInfo.InvariantCulture) <= at.ToUnixTimeMilliseconds());

    private static List<DateTimeOffset> Segments(FakeS3 s3, ManagedDatabaseRef database, string[] snapshot, DateTimeOffset at) =>
        [.. s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/wal/" + snapshot[1] + "/", StringComparison.Ordinal))
            .Select(key => key.Split('/')[^1].Split('-'))
            .Where(parts => long.Parse(parts[0], CultureInfo.InvariantCulture) >= long.Parse(snapshot[2], CultureInfo.InvariantCulture))
            .Select(parts => DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[1], CultureInfo.InvariantCulture))).Where(segment => segment <= at)];

    private static string[] Files(ManagementApi api) =>
        [.. Directory.EnumerateFiles(api.Server.DataDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private static void Route(FakeS3 s3, StorageProbe probe, IServiceCollection services)
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
        services.AddHttpClient("backup-s3").AddHttpMessageHandler(() => new ProbeHandler(probe));
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

    private sealed class StorageProbe
    {
        public int Writes;
        public int Downloads;
        public int SegmentListings;
        public Dictionary<string, string[]> Listings { get; } = [];
        public string? FailPrefix { get; set; }
        public bool NetworkFailure { get; set; }
        public string? OversizePrefix { get; set; }
        public int OversizeItems { get; set; }
        public string? BlockPrefix { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProbeHandler(StorageProbe probe) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var prefix = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["prefix"];
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref probe.Writes);
            }
            else if (prefix is null)
            {
                Interlocked.Increment(ref probe.Downloads);
            }

            if (prefix is not null && prefix.Contains("/wal/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref probe.SegmentListings);
            }

            if (prefix is not null && probe.FailPrefix is { } failed && prefix.StartsWith(failed, StringComparison.Ordinal))
            {
                if (probe.NetworkFailure)
                {
                    throw new HttpRequestException("objectstorage.test: test-secret-key provider transport failure");
                }

                var error = new XElement("Error", new XElement("Code", "AccessDenied"), new XElement("Message", "objectstorage.test: test-secret-key provider listing failure"));
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(error.ToString(), Encoding.UTF8, "application/xml") };
            }

            if (prefix is not null && prefix == probe.BlockPrefix)
            {
                probe.Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            string[]? keys = null;
            if (prefix is not null && probe.OversizePrefix is { } oversized && prefix.StartsWith(oversized, StringComparison.Ordinal))
            {
                keys = [.. Enumerable.Range(0, probe.OversizeItems).Select(i => prefix + "junk-" + i.ToString("D16", CultureInfo.InvariantCulture))];
            }
            else if (prefix is not null && probe.Listings.TryGetValue(prefix, out var listed))
            {
                var after = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["start-after"] ?? "";
                keys = [.. listed.Where(key => string.CompareOrdinal(key, after) > 0).Order(StringComparer.Ordinal)];
            }

            if (keys is not null)
            {
                XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
                var listing = new XElement(ns + "ListBucketResult", new XElement(ns + "IsTruncated", "false"),
                    keys.Select(key => new XElement(ns + "Contents", new XElement(ns + "Key", key), new XElement(ns + "Size", 0))));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listing.ToString(), Encoding.UTF8, "application/xml") };
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
