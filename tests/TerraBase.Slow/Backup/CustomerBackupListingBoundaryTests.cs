using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class CustomerBackupListingBoundaryTests
{
    [Test]
    [Arguments("\u4E2D", 1024)]
    [Arguments("\u4E2D", 1025)]
    [Arguments("\U00010000", 1024)]
    [Arguments("\U00010000", 1025)]
    public async Task Multibyte_key_limit_controls_snapshot_and_segment_selection_restore_and_retention(string scalar, int keyBytes)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(s3, time);
        var (database, text) = await SeedAsync(api, cancellationToken);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ShipAsync(api, time, cancellationToken);
        var file = FilePrefix(database);
        var snapshots = file + "/snapshots/";
        var snapshot = s3.CustomerObjects.Single(item => item.Key.StartsWith(snapshots, StringComparison.Ordinal));
        var parts = snapshot.Key.Split('/')[^1].Split('-');
        var snapshotAt = time.GetUtcNow();
        var boundarySnapshotAt = snapshotAt.AddSeconds(1);
        var boundarySnapshot = MultibyteKey(snapshots, SnapshotName(boundarySnapshotAt, parts[1], parts[2]), scalar, keyBytes);
        await ResealAsync(s3, snapshot, boundarySnapshot, cancellationToken);
        await PutAsync(data, database, "after", text, cancellationToken);
        await ShipAsync(api, time, cancellationToken);
        var segments = file + "/wal/" + parts[1] + "/";
        var segment = s3.CustomerObjects.Single(item => item.Key.StartsWith(segments, StringComparison.Ordinal));
        var boundarySegment = MultibyteKey(segments, segment.Key.Split('/')[^1], scalar, keyBytes);
        await ResealAsync(s3, segment, boundarySegment, cancellationToken);
        foreach (var key in new[] { boundarySnapshot, boundarySegment })
        {
            await Assert.That(Encoding.UTF8.GetByteCount(key)).IsEqualTo(keyBytes);
            await Assert.That(key.Length).IsLessThan(1024);
        }

        var snapshotReads = 0;
        var segmentReads = 0;
        s3.ObjectRequested = (customer, method, key) =>
        {
            if (customer && method == HttpMethod.Get)
            {
                if (key == boundarySnapshot)
                {
                    Interlocked.Increment(ref snapshotReads);
                }

                if (key == boundarySegment)
                {
                    Interlocked.Increment(ref segmentReads);
                }
            }
        };
        if (keyBytes == 1024)
        {
            // The inclusive boundary supplies the real WAL through its nested key, without a duplicate segment.
            s3.CustomerListingOverride = (prefix, keys) => prefix == segments ? [.. keys.Where(key => key != segment.Key)] : keys;
        }

        var latest = await LatestAsync(api.Client, database.Path, cancellationToken);
        await Assert.That(latest).IsEqualTo(time.GetUtcNow());
        using (var plan = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
            new { pointInTime = latest, destination = "customer" }, cancellationToken))
        {
            plan.EnsureSuccessStatusCode();
            await Assert.That((await plan.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken))
                .GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(keyBytes == 1024 ? boundarySnapshotAt : snapshotAt);
        }

        var restored = await RestoreAsync(api.Client, database, database.Path, latest, "boundary-restored", cancellationToken);
        await AssertDocumentAsync(data, restored, "kept", text, cancellationToken);
        await AssertDocumentAsync(data, restored, "after", text, cancellationToken);
        await Assert.That(snapshotReads).IsEqualTo(keyBytes == 1024 ? 2 : 0);
        // Window/plan read marks and restore replay each size the object before downloading it to scratch.
        await Assert.That(segmentReads).IsEqualTo(keyBytes == 1024 ? 6 : 0);
        s3.CustomerListingOverride = null;

        // A valid predecessor protects the live generation while retention deletes the older test generation.
        var expiredGeneration = Ulid.NewUlid().ToString();
        var expiredAt = time.GetUtcNow().AddDays(-4);
        var expiredName = SnapshotName(expiredAt, expiredGeneration, "0000000000000000");
        var expiredSnapshot = MultibyteKey(snapshots + "0", expiredName, scalar, keyBytes);
        var expiredSegments = file + "/wal/" + expiredGeneration + "/";
        var expiredSegmentName = string.Create(CultureInfo.InvariantCulture, $"0000000000000000-{expiredAt.ToUnixTimeMilliseconds():D13}");
        var expiredSegment = MultibyteKey(expiredSegments, expiredSegmentName, scalar, keyBytes);
        var controlSnapshot = snapshots + expiredName;
        var controlSegment = expiredSegments + expiredSegmentName;
        s3.SeedObject(customer: true, expiredSnapshot, []);
        s3.SeedObject(customer: true, expiredSegment, []);
        s3.SeedObject(customer: true, controlSnapshot, []);
        s3.SeedObject(customer: true, controlSegment, []);
        s3.SeedObject(customer: true, snapshots + SnapshotName(expiredAt.AddDays(1), parts[1], parts[2]), snapshot.Value);
        var expiredOperations = 0;
        s3.ObjectRequested = (customer, method, key) =>
        {
            if (customer && (method == HttpMethod.Get || method == HttpMethod.Delete)
                && (key == expiredSnapshot || key == expiredSegment))
            {
                Interlocked.Increment(ref expiredOperations);
            }
        };
        time.Advance(TimeSpan.FromHours(2));
        await ShipAsync(api, time, cancellationToken);
        await Assert.That(s3.CustomerObjects.ContainsKey(controlSnapshot)).IsFalse();
        await Assert.That(s3.CustomerObjects.ContainsKey(controlSegment)).IsFalse();
        await Assert.That(s3.CustomerObjects.ContainsKey(expiredSnapshot)).IsEqualTo(keyBytes > 1024);
        await Assert.That(s3.CustomerObjects.ContainsKey(expiredSegment)).IsEqualTo(keyBytes > 1024);
        await Assert.That(expiredOperations).IsEqualTo(keyBytes == 1024 ? 2 : 0);
        await Assert.That(s3.CustomerObjects.ContainsKey(boundarySnapshot)).IsTrue();
        await Assert.That(s3.CustomerObjects.ContainsKey(boundarySegment)).IsTrue();
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments("duplicate", false)]
    [Arguments("duplicate", true)]
    [Arguments("out-of-order", false)]
    [Arguments("out-of-order", true)]
    [Arguments("nonadvancing", false)]
    [Arguments("nonadvancing", true)]
    public async Task Malformed_snapshot_and_WAL_metadata_returns_fixed_503_on_canonical_and_alias_windows_and_recovers(string failure, bool segments)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(s3, time);
        var (database, text) = await SeedAsync(api, cancellationToken);
        using var project = ProjectClient(api, database);
        await ShipAsync(api, time, cancellationToken);
        await PutAsync(project, database, "after", text, cancellationToken);
        await ShipAsync(api, time, cancellationToken);
        var snapshots = FilePrefix(database) + "/snapshots/";
        var snapshot = s3.CustomerObjects.Keys.Single(key => key.StartsWith(snapshots, StringComparison.Ordinal));
        var prefix = segments ? FilePrefix(database) + "/wal/" + snapshot.Split('/')[^1].Split('-')[1] + "/" : snapshots;
        var entry = s3.CustomerObjects.Keys.Single(key => key.StartsWith(prefix, StringComparison.Ordinal));
        var latest = await LatestAsync(project, database.Data, cancellationToken);
        if (failure == "nonadvancing")
        {
            for (var index = 0; index < 500; index++)
            {
                s3.SeedObject(customer: true, prefix + "z-junk-" + index.ToString("D3", CultureInfo.InvariantCulture), []);
            }
        }
        else
        {
            s3.CustomerListingOverride = (requested, keys) => requested != prefix ? keys
                : failure == "duplicate" ? [entry, entry] : [prefix + "z-marker", entry];
        }

        s3.ObservedListPrefix = prefix;
        foreach (var (client, path) in new[] { (project, database.Data), (api.Client, database.Path) })
        {
            string? firstToken = null;
            if (failure == "nonadvancing")
            {
                s3.CustomerContinuationTokenOverride = (requested, next) => requested == prefix ? firstToken ??= next : next;
            }

            var listed = s3.ListRequests;
            using var response = await client.GetAsync(ManagementApi.Uri(path + "/point-in-time?destination=customer"), cancellationToken);
            await UnavailableAsync(response, cancellationToken);
            await Assert.That(s3.ListRequests - listed).IsEqualTo(failure == "nonadvancing" ? 2 : 1);
        }

        s3.CustomerListingOverride = null;
        s3.CustomerContinuationTokenOverride = null;
        foreach (var (client, path, name) in new[] { (project, database.Data, "canonical-recovered"), (api.Client, database.Path, "alias-recovered") })
        {
            await Assert.That(await LatestAsync(client, path, cancellationToken)).IsEqualTo(latest);
            var restored = await RestoreAsync(client, database, path, latest, name, cancellationToken);
            await AssertDocumentAsync(project, restored, "kept", text, cancellationToken);
            await AssertDocumentAsync(project, restored, "after", text, cancellationToken);
        }

        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Oversized_multibyte_entries_still_consume_raw_snapshot_and_WAL_plan_budgets(bool segments)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(s3, time);
        var (database, _) = await SeedAsync(api, cancellationToken);
        await ShipAsync(api, time, cancellationToken);
        var snapshots = FilePrefix(database) + "/snapshots/";
        var snapshot = s3.CustomerObjects.Keys.Single(key => key.StartsWith(snapshots, StringComparison.Ordinal));
        var prefix = segments ? FilePrefix(database) + "/wal/" + snapshot.Split('/')[^1].Split('-')[1] + "/" : snapshots;
        var filename = segments ? string.Create(CultureInfo.InvariantCulture, $"0000000000000000-{time.GetUtcNow().ToUnixTimeMilliseconds():D13}")
            : snapshot.Split('/')[^1];
        for (var index = 0; index < 10_000; index++)
        {
            var key = MultibyteKey(prefix + "z-" + index.ToString("D5", CultureInfo.InvariantCulture), filename,
                index % 2 == 0 ? "\u4E2D" : "\U00010000", 1025);
            s3.SeedObject(customer: true, key, []);
        }

        await ErrorAssert.ErrorAsync(api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
            new { pointInTime = time.GetUtcNow(), destination = "customer" }, cancellationToken),
            HttpStatusCode.ServiceUnavailable, "pitr_plan_limit", "the point-in-time plan metadata scan exceeds 10000 objects");
        s3.CustomerListingOverride = (requested, keys) => requested == prefix
            ? [.. keys.Where(key => !key.StartsWith(prefix + "z-", StringComparison.Ordinal))] : keys;
        using var recovered = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
            new { pointInTime = time.GetUtcNow(), destination = "customer" }, cancellationToken);
        recovered.EnsureSuccessStatusCode();
        await Assert.That((await recovered.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken))
            .GetProperty("snapshotAt").GetDateTimeOffset()).IsEqualTo(time.GetUtcNow());
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Window_listing_observes_caller_cancellation_on_canonical_and_alias_listeners_and_recovers(bool canonical)
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(s3, time);
        var (database, _) = await SeedAsync(api, cancellationToken);
        using var project = ProjectClient(api, database);
        await ShipAsync(api, time, cancellationToken);
        var client = canonical ? project : api.Client;
        var path = canonical ? database.Data : database.Path;
        var latest = await LatestAsync(client, path, cancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s3.BeforeList = async (prefix, token) =>
        {
            if (prefix == FilePrefix(database) + "/snapshots/")
            {
                entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = client.GetAsync(ManagementApi.Uri(path + "/point-in-time?destination=customer"), caller.Token);
        try
        {
            await entered.Task.WaitAsync(cancellationToken);
            await caller.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            await cancelled.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            release.TrySetResult();
            await caller.CancelAsync();
            await ((Task)pending).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (pending.IsCompletedSuccessfully)
            {
                using var response = await pending;
            }

            s3.BeforeList = null;
        }

        await Assert.That(await LatestAsync(client, path, cancellationToken)).IsEqualTo(latest);
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task A_window_read_from_the_bucket_ends_at_the_second_its_newest_read_mark_proves(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(s3, time);
        var (database, text) = await SeedAsync(api, cancellationToken);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ShipAsync(api, time, cancellationToken);
        var snapshotAt = time.GetUtcNow();
        await PutAsync(data, database, "after", text, cancellationToken);
        await ShipAsync(api, time, cancellationToken);
        var read = time.GetUtcNow();
        var segment = s3.CustomerObjects.Single(item => item.Key.Contains("/wal/", StringComparison.Ordinal));
        // The test clock stands still during a read, so its mark is the read's end.
        await Assert.That(await LatestAsync(api.Client, database.Path, cancellationToken)).IsEqualTo(read);

        // A read that started 300 ms before it ended proves the whole second at or before its start.
        await ResealAsync(s3, segment, segment.Key, log =>
        {
            BinaryPrimitives.WriteInt64BigEndian(log.AsSpan(^8), read.AddMilliseconds(-300).ToUnixTimeMilliseconds());
            return log;
        }, cancellationToken);
        await Assert.That(await LatestAsync(api.Client, database.Path, cancellationToken)).IsEqualTo(read.AddSeconds(-1));
        using (var plan = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
            new { pointInTime = snapshotAt, destination = "customer" }, cancellationToken))
        {
            plan.EnsureSuccessStatusCode();
            await Assert.That((await plan.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken))
                .GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(read.AddSeconds(-1));
        }

        // Without a mark (a read that stopped before the newest commit), only the snapshot's time is proven.
        await ResealAsync(s3, segment, segment.Key, log => log[..^9], cancellationToken);
        await Assert.That(await LatestAsync(api.Client, database.Path, cancellationToken)).IsEqualTo(snapshotAt);
    }

    [Test]
    public async Task A_window_read_from_the_bucket_falls_back_past_an_unreadable_newest_segment(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = Clock();
        await using var api = await StartAsync(s3, time);
        var (database, text) = await SeedAsync(api, cancellationToken);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ShipAsync(api, time, cancellationToken);
        var snapshotAt = time.GetUtcNow();
        await PutAsync(data, database, "first", text, cancellationToken);
        await ShipAsync(api, time, cancellationToken);
        var first = time.GetUtcNow();
        await PutAsync(data, database, "second", text, cancellationToken);
        await ShipAsync(api, time, cancellationToken);
        // The newest segment, which holds second and the newest read mark, fails authentication.
        var newest = s3.CustomerObjects.Keys.Where(key => key.Contains("/wal/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Last();
        var corrupted = s3.CustomerObjects[newest].ToArray();
        corrupted[^20] ^= 0xff;
        s3.SeedObject(customer: true, newest, corrupted);

        // Like a missing segment: the window ends at the previous segment's read mark.
        await Assert.That(await LatestAsync(api.Client, database.Path, cancellationToken)).IsEqualTo(first);
        using (var plan = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/plan"),
            new { pointInTime = snapshotAt, destination = "customer" }, cancellationToken))
        {
            plan.EnsureSuccessStatusCode();
            await Assert.That((await plan.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken))
                .GetProperty("window").GetProperty("latest").GetDateTimeOffset()).IsEqualTo(first);
        }

        var restored = await RestoreAsync(api.Client, database, database.Path, first, "previous-latest", cancellationToken);
        await AssertDocumentAsync(data, restored, "first", text, cancellationToken);
        using var second = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", "second")), cancellationToken);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time)
    {
        var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

    private static async Task<(ManagedDatabaseRef Database, string Text)> SeedAsync(ManagementApi api, CancellationToken cancellationToken)
    {
        var database = await api.CreateManagedDatabaseAsync();
        using var saved = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
        {
            endpoint = FakeS3.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeS3.CustomerBucket,
            accessKeyId = FakeS3.CustomerAccessKey,
            secretKey = FakeS3.CustomerSecretKey,
            mode = "customer",
        }, cancellationToken);
        saved.EnsureSuccessStatusCode();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people", cancellationToken);
        var text = new Faker().Lorem.Sentence(12);
        await PutAsync(data, database, "kept", text, cancellationToken);
        return (database, text);
    }

    private static HttpClient ProjectClient(ManagementApi api, ManagedDatabaseRef database) =>
        api.WithToken(api.Issuer.Mint(Ulid.NewUlid().ToString(), token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
        }));

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static string FilePrefix(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private static string SnapshotName(DateTimeOffset at, string generation, string position) =>
        string.Create(CultureInfo.InvariantCulture, $"{at.ToUnixTimeMilliseconds():D13}-{generation}-{position}");

    private static string MultibyteKey(string prefix, string filename, string scalar, int keyBytes)
    {
        var remaining = keyBytes - Encoding.UTF8.GetByteCount(prefix + "/" + filename);
        var scalarBytes = Encoding.UTF8.GetByteCount(scalar);
        return prefix + string.Concat(Enumerable.Repeat(scalar, remaining / scalarBytes)) + new string('x', remaining % scalarBytes) + "/" + filename;
    }

    private static Task ResealAsync(FakeS3 s3, KeyValuePair<string, byte[]> original, string renamed, CancellationToken cancellationToken) =>
        ResealAsync(s3, original, renamed, log => log, cancellationToken);

    private static async Task ResealAsync(FakeS3 s3, KeyValuePair<string, byte[]> original, string renamed, Func<byte[], byte[]> change, CancellationToken cancellationToken)
    {
        var keyId = Encoding.ASCII.GetString(original.Value, 4, 26);
        var parts = original.Key.Split('/');
        var wrapped = Encoding.ASCII.GetString(s3.CustomerObjects[BackupLayout.Key(parts[1] + "/" + parts[2], keyId)]);
        var key = s3.Unwrap(wrapped);
        try
        {
            using var encrypted = new MemoryStream(original.Value);
            using var plain = new MemoryStream();
            await BackupObject.ReadAsync(encrypted, plain, original.Key, _ => Task.FromResult(key), cancellationToken);
            using var output = new MemoryStream();
            await BackupObject.WriteAsync(new MemoryStream(change(plain.ToArray())), output, renamed, keyId, key, cancellationToken);
            s3.SeedObject(customer: true, renamed, output.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task ShipAsync(ManagementApi api, FakeTimeProvider time, CancellationToken cancellationToken)
    {
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text, CancellationToken cancellationToken)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<DateTimeOffset> LatestAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ManagementApi.Uri(path + "/point-in-time?destination=customer"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
    }

    private static async Task<ManagedDatabaseRef> RestoreAsync(HttpClient client, ManagedDatabaseRef database, string path,
        DateTimeOffset latest, string name, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(path + "/point-in-time/restore"),
            new { pointInTime = latest, name, destination = "customer" }, cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var restored = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        return new(database.Organization, database.Project, restored.GetProperty("id").GetString()!, restored.GetProperty("name").GetString()!);
    }

    private static async Task AssertDocumentAsync(HttpClient client, ManagedDatabaseRef database, string name, string text, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken))
            .GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
    }

    private static async Task UnavailableAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name)).IsEquivalentTo(["error"]);
        var error = body.RootElement.GetProperty("error");
        await Assert.That(error.EnumerateObject().Select(property => property.Name)).IsEquivalentTo(["code", "message"]);
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("unavailable");
        await Assert.That(error.GetProperty("message").GetString())
            .IsEqualTo("backup bucket could not be accessed");
    }
}
