using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Bogus;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class UsageApiTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Database_usage_counts_storage_documents_and_operations()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var fresh = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/usage")));
        var puts = Faker.Random.Int(3, 6);
        var gets = Faker.Random.Int(1, 4);
        var lists = Faker.Random.Int(1, 3);
        await WriteAsync(data, database, "people", puts);
        (await data.DeleteAsync(Uri(Document(database, "people", "d0")))).EnsureSuccessStatusCode();
        await WriteAsync(data, database, "orders", 1);
        for (var i = 0; i < gets; i++)
        {
            (await data.GetAsync(Uri(Document(database, "people", "d1")))).EnsureSuccessStatusCode();
        }

        for (var i = 0; i < lists; i++)
        {
            (await data.GetAsync(Uri($"{database.Data}/collections/people/documents"))).EnsureSuccessStatusCode();
        }

        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/usage")));

        await Assert.That(fresh.RootElement.GetProperty("reads").GetInt64()).IsEqualTo(0);
        await Assert.That(fresh.RootElement.GetProperty("documents").GetInt64()).IsEqualTo(0);
        var root = usage.RootElement;
        await Assert.That(root.GetProperty("databaseId").GetString()).IsEqualTo(database.Id);
        await Assert.That(root.GetProperty("storageBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(root.GetProperty("collections").GetInt64()).IsEqualTo(2);
        await Assert.That(root.GetProperty("documents").GetInt64()).IsEqualTo(puts - 1 + 1);
        await Assert.That(root.GetProperty("reads").GetInt64()).IsEqualTo(gets + lists);
        await Assert.That(root.GetProperty("writes").GetInt64()).IsEqualTo(puts + 1 + 1);
    }

    [Test]
    public async Task Reads_and_writes_are_counted_apart_per_hour_and_day_and_summed_by_project()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 10, 30, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(time: time);
        var first = await api.CreateManagedDatabaseAsync("first");
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        using var data = await api.CreateClientAsync(first.Organization, "documents:read", "documents:write", "shapes:read");
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();

        // 10:30: N reads and M writes, saved by a flush.
        var (writes, reads) = (Faker.Random.Int(2, 5), Faker.Random.Int(1, 4));
        await WriteAsync(data, first, "people", writes);
        for (var i = 0; i < reads; i++)
        {
            (await data.GetAsync(Uri(Document(first, "people", "d0")))).EnsureSuccessStatusCode();
        }

        await meter.FlushAsync(CancellationToken.None);

        // 11:30: more, still in memory; a SQL run and a shape read are one read each, whatever they return.
        time.Advance(TimeSpan.FromHours(1));
        var (laterWrites, laterReads) = (Faker.Random.Int(1, 3), Faker.Random.Int(1, 3));
        await WriteAsync(data, first, "people", laterWrites);
        for (var i = 0; i < laterReads; i++)
        {
            (await data.GetAsync(Uri($"{first.Data}/collections/people/documents"))).EnsureSuccessStatusCode();
        }

        (await api.Client.PostAsJsonAsync(Uri(first.Path + "/sql"), new { sql = "SELECT * FROM people" })).EnsureSuccessStatusCode();
        using (var shape = await data.GetAsync(Uri($"{first.Data}/collections/people/shape?offset=-1")))
        {
            await Assert.That(shape.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        }

        await WriteAsync(data, second, "people", 1);
        laterReads += 2;

        var hours = $"?granularity=hour&from=2026-10-06T10:00:00Z&to=2026-10-06T12:00:00Z";
        var database = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/usage" + hours));
        var day = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/usage?granularity=day&from=2026-10-06T00:00:00Z&to=2026-10-07T00:00:00Z"));
        var recent = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/usage"));
        var project = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.ProjectPath + "/usage" + hours));
        var collections = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/collections"));

        await Assert.That(Points(database)).IsEquivalentTo(
            [("2026-10-06T10:00:00+00:00", reads, writes), ("2026-10-06T11:00:00+00:00", laterReads, laterWrites)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertPeriodAsync(database, "2026-10-06T10:00:00+00:00", "2026-10-06T12:00:00+00:00", "hour", reads + laterReads, writes + laterWrites);
        await Assert.That(database.GetProperty("reads").GetInt64()).IsEqualTo(reads + laterReads);
        await Assert.That(database.GetProperty("writes").GetInt64()).IsEqualTo(writes + laterWrites);
        await Assert.That(database.GetProperty("logicalBytes").GetInt64())
            .IsEqualTo(collections.GetProperty("items").EnumerateArray().Sum(item => item.GetProperty("bytes").GetInt64()));
        await Assert.That(database.GetProperty("logicalBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(Points(day)).IsEquivalentTo([("2026-10-06T00:00:00+00:00", reads + laterReads, writes + laterWrites)]);
        // Without from: the 24 hours that end with the current one.
        var last = Points(recent);
        await Assert.That(last.Count).IsEqualTo(24);
        await Assert.That(last[^2..]).IsEquivalentTo(Points(database), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(last[..^2].All(point => point is (_, 0, 0))).IsTrue();

        // The project sums both databases; its items carry periods, not series.
        await AssertPeriodAsync(project, "2026-10-06T10:00:00+00:00", "2026-10-06T12:00:00+00:00", "hour", reads + laterReads, writes + laterWrites + 1);
        await Assert.That(Points(project)).IsEquivalentTo(
            [("2026-10-06T10:00:00+00:00", reads, writes), ("2026-10-06T11:00:00+00:00", laterReads, laterWrites + 1)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(project.GetProperty("databases").GetInt64()).IsEqualTo(2);
        await Assert.That(project.GetProperty("items").EnumerateArray().All(item => item.GetProperty("series").GetArrayLength() == 0)).IsTrue();
        await Assert.That(project.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("databaseId").GetString() == second.Id)
            .GetProperty("period").GetProperty("writes").GetInt64()).IsEqualTo(1);

        // Saving changes nothing a reader sees.
        await meter.FlushAsync(CancellationToken.None);
        var saved = await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.Path + "/usage" + hours));
        await Assert.That(Points(saved)).IsEquivalentTo(Points(database), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(await BucketsAsync(api.Server.DataDirectory, first.Id, "hours")).IsEquivalentTo(["2026-10-06T10", "2026-10-06T11"]);
    }

    [Test]
    public async Task A_flush_prunes_expired_buckets_and_saves_held_counts_past_retention_only_in_kept_days_and_lifetime()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-usage-retention-").FullName;
        try
        {
            ManagedDatabaseRef database;
            string user;
            void Settings(Dictionary<string, string> settings)
            {
                settings["data-directory"] = directory;
            }

            await using (var api = await ManagementApi.StartAsync(Settings, time: new FakeTimeProvider(Instant("2026-10-06T10:30:00Z"))))
            {
                user = api.User;
                database = await api.CreateManagedDatabaseAsync();
                using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await WriteAsync(data, database, "people", 1);
                await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
                await Assert.That(await BucketsAsync(directory, database.Id, "hours")).IsEquivalentTo(["2026-10-06T10"]);
            }

            // 36 days later, past the 35 days of hours. Before any flush prunes it, the saved hour already reads as zero; its
            // day is still kept.
            await using (var api = await ManagementApi.StartAsync(Settings, time: new FakeTimeProvider(Instant("2026-11-11T11:30:00Z"))))
            {
                using var member = api.As(user);
                var hour = await member.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage?from=2026-10-06T10:00:00Z&to=2026-10-06T11:00:00Z"));
                var day = await member.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage?granularity=day&from=2026-10-06T00:00:00Z&to=2026-10-07T00:00:00Z"));
                await Assert.That(await BucketsAsync(directory, database.Id, "hours")).IsEquivalentTo(["2026-10-06T10"]);
                await Assert.That(hour.GetProperty("period").GetProperty("writes").GetInt64()).IsEqualTo(0);
                await Assert.That(day.GetProperty("period").GetProperty("writes").GetInt64()).IsEqualTo(1);

                using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await WriteAsync(data, database, "people", 1);
                var meter = api.Server.Services.GetRequiredService<UsageMeter>();
                var owner = new DatabaseOwner(database.Organization, database.Project, database.Id);
                // Held counts: an expired hour, the oldest kept hour (840 hours end with 11:00) and the one before, the oldest
                // kept day (400 days end with today) and the one before.
                foreach (var held in new[] { "2026-10-01T05:00:00Z", "2026-10-07T12:00:00Z", "2026-10-07T11:00:00Z", "2025-10-08T05:00:00Z", "2025-10-07T05:00:00Z" })
                {
                    meter.Add(new UsageKey(owner, Instant(held).ToUnixTimeSeconds() / 3600), new OpCounts(1, 0));
                }

                await meter.FlushAsync(CancellationToken.None);

                await Assert.That(await BucketsAsync(directory, database.Id, "hours")).IsEquivalentTo(["2026-10-07T12", "2026-11-11T11"]);
                await Assert.That(await BucketsAsync(directory, database.Id, "days"))
                    .IsEquivalentTo(["2025-10-08", "2026-10-01", "2026-10-06", "2026-10-07", "2026-11-11"]);
                await AssertSavedAsync(api, database, reads: 5, writes: 2);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Counts_folded_into_a_day_or_the_lifetime_never_show_as_an_hour_or_as_1970()
    {
        var time = new FakeTimeProvider(Instant("2026-11-11T11:30:00Z"));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var owner = new DatabaseOwner(database.Organization, database.Project, database.Id);
        // As compaction leaves them, here or on a follower whose clock runs ahead: day-only entries at a day's first hour (one
        // past the kept hours, one still within them) and a lifetime-only entry at hour 0.
        meter.Add(new UsageKey(owner, Hour("2026-10-01T00:00:00Z"), UsageScope.Day), new OpCounts(3, 0));
        meter.Add(new UsageKey(owner, Hour("2026-11-10T00:00:00Z"), UsageScope.Day), new OpCounts(2, 0));
        meter.Add(new UsageKey(owner, 0, UsageScope.Lifetime), new OpCounts(4, 0));

        foreach (var saved in new[] { false, true })
        {
            if (saved)
            {
                await meter.FlushAsync(CancellationToken.None);
                await Assert.That(await BucketsAsync(api.Server.DataDirectory, database.Id, "hours")).IsEmpty();
                await Assert.That(await BucketsAsync(api.Server.DataDirectory, database.Id, "days")).IsEquivalentTo(["2026-10-01", "2026-11-10"]);
            }

            async Task<long> ReadsAsync(string query)
            {
                return (await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage?" + query))).GetProperty("period").GetProperty("reads").GetInt64();
            }

            await Assert.That(await ReadsAsync("from=2026-09-30T22:00:00Z&to=2026-10-01T02:00:00Z")).IsEqualTo(0);
            await Assert.That(await ReadsAsync("from=2026-11-09T22:00:00Z&to=2026-11-10T02:00:00Z")).IsEqualTo(0);
            await Assert.That(await ReadsAsync("granularity=day&from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z")).IsEqualTo(3);
            await Assert.That(await ReadsAsync("granularity=day&from=2026-11-10T00:00:00Z&to=2026-11-11T00:00:00Z")).IsEqualTo(2);
            await Assert.That(await ReadsAsync("granularity=day&from=1970-01-01T00:00:00Z&to=1970-01-02T00:00:00Z")).IsEqualTo(0);
            await Assert.That(await ReadsAsync("from=1970-01-01T00:00:00Z&to=1970-01-01T01:00:00Z")).IsEqualTo(0);
            var lifetime = await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage"));
            await Assert.That(lifetime.GetProperty("reads").GetInt64()).IsEqualTo(9);
        }
    }

    [Test]
    public async Task Usage_reads_go_on_while_a_batch_waits_for_the_primary()
    {
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(Instant("2026-11-11T11:30:00Z")));
        var database = await api.CreateManagedDatabaseAsync();
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        meter.Add(new UsageKey(new DatabaseOwner(database.Organization, database.Project, database.Id), Hour("2026-11-11T11:00:00Z")), new OpCounts(3, 0));
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> Send(long sequence, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
        {
            sending.TrySetResult();
            return acknowledge.Task;
        }

        // The primary is slow to answer; a usage read served here does not wait for it, and still sees the batch's counts.
        var send = meter.SendAsync(Send, CancellationToken.None).AsTask();
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var (during, _) = await meter.GetAsync(tenant, new UsageWindow(UsageWindow.Hour, Hour("2026-11-11T11:00:00Z"), 1), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        acknowledge.SetResult(true);
        await Assert.That(await send.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        var (after, _) = await meter.GetAsync(tenant, new UsageWindow(UsageWindow.Hour, Hour("2026-11-11T11:00:00Z"), 1), CancellationToken.None);

        await Assert.That(during.Reads).IsEqualTo(3);
        await Assert.That(after.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task Counting_while_batches_go_out_loses_nothing_and_frees_every_emptied_database()
    {
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(Instant("2026-11-11T11:30:00Z")));
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var hour = Hour("2026-11-11T11:00:00Z");
        var owners = Enumerable.Range(0, 50).Select(_ => new DatabaseOwner(EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString())).ToArray();
        long sent = 0;
        Task<bool> Send(long sequence, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref sent, batch.Sum(entry => entry.Value.Reads));
            return Task.FromResult(true);
        }

        // Four writers count while batches keep going out and each acknowledged one empties, and drops, maps.
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++)
            {
                meter.Add(new UsageKey(owners[((writer * 5000) + i) % owners.Length], hour), new OpCounts(1, 0));
            }
        })).ToArray();
        while (!writers.All(writer => writer.IsCompleted))
        {
            await meter.SendAsync(Send, CancellationToken.None);
        }

        await Task.WhenAll(writers);
        while (await meter.SendAsync(Send, CancellationToken.None))
        {
        }

        await Assert.That(Interlocked.Read(ref sent)).IsEqualTo(20_000);
        await Assert.That(meter.PendingDatabases).IsEqualTo(0);
    }

    [Test]
    public async Task Senders_silent_for_seven_days_retire_and_free_their_places()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-usage-retire-").FullName;
        try
        {
            void Settings(Dictionary<string, string> settings)
            {
                settings["data-directory"] = directory;
            }

            // 1,024 followers fill every place; one more is refused while they are recent.
            await using (var api = await ManagementApi.StartAsync(Settings, time: new FakeTimeProvider(Instant("2026-11-01T00:00:00Z"))))
            {
                var meter = api.Server.Services.GetRequiredService<UsageMeter>();
                for (var i = 0; i < 1024; i++)
                {
                    await Assert.That(await meter.AcceptAsync($"node-{i}", "epoch", 1, [], CancellationToken.None)).IsEqualTo(UsageAccept.Added);
                }

                await Assert.That(await meter.AcceptAsync("node-new", "epoch", 1, [], CancellationToken.None)).IsEqualTo(UsageAccept.TooManySenders);
                await meter.FlushAsync(CancellationToken.None);
                await Assert.That(await CountSendersAsync(directory)).IsEqualTo(1024);
            }

            // Eight days later they have retired: the new follower gets a place, and the next flush deletes their records.
            await using (var api = await ManagementApi.StartAsync(Settings, time: new FakeTimeProvider(Instant("2026-11-09T00:00:00Z"))))
            {
                var meter = api.Server.Services.GetRequiredService<UsageMeter>();
                await Assert.That(await meter.AcceptAsync("node-new", "epoch", 1, [], CancellationToken.None)).IsEqualTo(UsageAccept.Added);
                await meter.FlushAsync(CancellationToken.None);
                await Assert.That(await CountSendersAsync(directory)).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Project_usage_whose_bucket_read_outlasts_its_deadline_reads_no_further_page()
    {
        var time = new FakeTimeProvider(Instant("2026-11-11T11:30:00Z"));
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var owner = new DatabaseOwner(database.Organization, database.Project, database.Id);
        // 150 saved hours: their series takes two pages.
        for (var hour = 0; hour < 150; hour++)
        {
            meter.Add(new UsageKey(owner, Hour("2026-11-11T11:00:00Z") - hour), new OpCounts(1, 0));
        }

        await meter.FlushAsync(CancellationToken.None);
        var pages = 0;
        // Reading the first page of buckets takes 6 seconds of the request's 5.
        hook.BeforeList = collection =>
        {
            if (collection.EndsWith("/usage-hours", StringComparison.Ordinal) && Interlocked.Increment(ref pages) == 1)
            {
                time.Advance(TimeSpan.FromSeconds(6));
            }
        };

        using var response = await api.Client.GetAsync(Uri(database.ProjectPath + "/usage?from=2026-11-04T00:00:00Z&to=2026-11-11T12:00:00Z"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
        await Assert.That(pages).IsEqualTo(1);
    }

    [Test]
    public async Task A_follower_that_keeps_retrying_a_batch_never_retires_so_its_retry_counts_once()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-usage-retry-").FullName;
        try
        {
            void Settings(Dictionary<string, string> settings)
            {
                settings["data-directory"] = directory;
            }

            async Task<UsageAccept> AcceptAsync(string at, string sender, Func<UsageMeter, Task>? before = null)
            {
                await using var api = await ManagementApi.StartAsync(Settings, time: new FakeTimeProvider(Instant(at)));
                var meter = api.Server.Services.GetRequiredService<UsageMeter>();
                if (before is not null)
                {
                    await before(meter);
                }

                var accepted = await meter.AcceptAsync(sender, "epoch", 1, [], CancellationToken.None);
                await meter.FlushAsync(CancellationToken.None);
                return accepted;
            }

            // The primary adds the batch, but its acknowledgement never reaches the follower, which keeps sending it.
            await Assert.That(await AcceptAsync("2026-11-01T00:00:00Z", "node-a")).IsEqualTo(UsageAccept.Added);
            await Assert.That(await AcceptAsync("2026-11-07T00:00:00Z", "node-a")).IsEqualTo(UsageAccept.Duplicate);
            // 12 days after the batch was added, 6 after the last retry: another sender loads every record and a flush
            // retires the silent ones. node-a's retry still finds its sequence.
            await Assert.That(await AcceptAsync("2026-11-13T00:00:00Z", "node-a", async meter =>
            {
                await meter.AcceptAsync("node-b", "epoch", 1, [], CancellationToken.None);
                await meter.FlushAsync(CancellationToken.None);
            })).IsEqualTo(UsageAccept.Duplicate);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_batch_unacknowledged_for_a_day_is_given_up_so_no_retry_outlives_its_senders_record()
    {
        var time = new FakeTimeProvider(Instant("2026-11-11T11:30:00Z"));
        // The test sends itself: the background flush, which the day's advance would run, would drop these unknown databases.
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
            services.Remove(services.Single(service => service.ImplementationType == typeof(UsageFlushService))));
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var owner = new DatabaseOwner(EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        List<(long Sequence, KeyValuePair<UsageKey, OpCounts>[] Batch)> sent = [];
        var reachable = false;
        Task<bool> Send(long sequence, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
        {
            sent.Add((sequence, batch));
            return reachable ? Task.FromResult(true) : Task.FromException<bool>(new IOException("the primary is away"));
        }

        // The primary saved batch 1, but its acknowledgement was lost, and the primary stays out of reach for a day.
        meter.Add(new UsageKey(owner, Hour("2026-11-11T11:00:00Z")), new OpCounts(1, 0));
        await Assert.ThrowsAsync<IOException>(async () => await meter.SendAsync(Send, CancellationToken.None));
        time.Advance(UsageMeter.UnsentLifetime - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(async () => await meter.SendAsync(Send, CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1));
        meter.Add(new UsageKey(owner, Hour("2026-11-12T11:00:00Z")), new OpCounts(1, 0));
        reachable = true;
        await Assert.That(await meter.SendAsync(Send, CancellationToken.None)).IsTrue();
        await Assert.That(await meter.SendAsync(Send, CancellationToken.None)).IsFalse();

        // Batch 1 went twice, then was given up; batch 2 carries only the later counts.
        await Assert.That(sent.Select(attempt => attempt.Sequence)).IsEquivalentTo([1L, 1L, 2L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(sent[2].Batch.Single()).IsEqualTo(KeyValuePair.Create(new UsageKey(owner, Hour("2026-11-12T11:00:00Z")), new OpCounts(1, 0)));
    }

    [Test]
    public async Task A_batch_the_primary_refuses_is_dropped_and_later_counts_are_sent()
    {
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(Instant("2026-11-11T11:30:00Z")));
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var owner = new DatabaseOwner(EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        List<(long Sequence, KeyValuePair<UsageKey, OpCounts>[] Batch)> sent = [];
        Task<bool> Send(long sequence, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
        {
            sent.Add((sequence, batch));
            // The first batch is refused for good, as one whose hours ran ahead of the primary's clock.
            return Task.FromResult(sent.Count > 1);
        }

        meter.Add(new UsageKey(owner, Hour("2026-11-13T00:00:00Z")), new OpCounts(5, 0));
        await Assert.That(await meter.SendAsync(Send, CancellationToken.None)).IsTrue();
        meter.Add(new UsageKey(owner, Hour("2026-11-11T11:00:00Z")), new OpCounts(1, 0));
        await Assert.That(await meter.SendAsync(Send, CancellationToken.None)).IsTrue();
        await Assert.That(await meter.SendAsync(Send, CancellationToken.None)).IsFalse();

        await Assert.That(sent.Select(attempt => attempt.Sequence)).IsEquivalentTo([1L, 2L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(sent[1].Batch.Single()).IsEqualTo(KeyValuePair.Create(new UsageKey(owner, Hour("2026-11-11T11:00:00Z")), new OpCounts(1, 0)));
    }

    [Test]
    public async Task Counts_held_while_a_batch_is_unacknowledged_still_fold_and_the_batch_keeps_its_counts()
    {
        var time = new FakeTimeProvider(Instant("2026-11-11T11:30:00Z"));
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var owner = new DatabaseOwner(database.Organization, database.Project, database.Id);
        var now = time.GetUtcNow().ToUnixTimeSeconds() / 3600;
        List<(long Sequence, KeyValuePair<UsageKey, OpCounts>[] Batch)> sent = [];
        var fail = true;
        Task<bool> Send(long sequence, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
        {
            sent.Add((sequence, batch));
            return fail ? Task.FromException<bool>(new IOException("the primary is away")) : Task.FromResult(true);
        }

        // The first batch is not acknowledged. Meanwhile 24 hours of a day 40 days ago arrive; the next attempt still folds
        // them into one entry of that day, without touching the unacknowledged batch.
        meter.Add(new UsageKey(owner, now), new OpCounts(1, 0));
        await Assert.ThrowsAsync<IOException>(async () => await meter.SendAsync(Send, CancellationToken.None));
        var old = Instant("2026-10-02T00:00:00Z").ToUnixTimeSeconds() / 3600;
        for (var hour = old; hour < old + 24; hour++)
        {
            meter.Add(new UsageKey(owner, hour), new OpCounts(1, 0));
        }

        meter.Add(new UsageKey(owner, now), new OpCounts(1, 0));
        await Assert.ThrowsAsync<IOException>(async () => await meter.SendAsync(Send, CancellationToken.None));
        fail = false;
        await Assert.That(await meter.SendAsync(Send, CancellationToken.None)).IsTrue();
        await Assert.That(await meter.SendAsync(Send, CancellationToken.None)).IsTrue();
        await Assert.That(await meter.SendAsync(Send, CancellationToken.None)).IsFalse();

        await Assert.That(sent.Select(attempt => attempt.Sequence)).IsEquivalentTo([1L, 1L, 1L, 2L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(sent[2].Batch.Single()).IsEqualTo(KeyValuePair.Create(new UsageKey(owner, now), new OpCounts(1, 0)));
        await Assert.That(sent[3].Batch.Select(entry => (entry.Key.Hour, entry.Key.Scope, entry.Value.Reads)))
            .IsEquivalentTo([(old, UsageScope.Day, 24L), (now, UsageScope.Hour, 1L)]);
    }

    [Test]
    public async Task Every_pending_key_gets_into_a_batch_while_others_keep_counting()
    {
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(Instant("2026-11-11T11:30:00Z")));
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var hour = Instant("2026-11-11T11:00:00Z").ToUnixTimeSeconds() / 3600;
        var keys = Enumerable.Range(0, UsageMeter.MaxBatch + 904)
            .Select(_ => new UsageKey(new DatabaseOwner(EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString()), hour))
            .ToList();
        foreach (var key in keys)
        {
            meter.Add(key, new OpCounts(1, 0));
        }

        List<KeyValuePair<UsageKey, OpCounts>[]> sent = [];
        Task<bool> Send(long sequence, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
        {
            sent.Add(batch);
            // The keys of this batch keep counting while it is in flight.
            foreach (var (key, _) in batch)
            {
                meter.Add(key, new OpCounts(1, 0));
            }

            return Task.FromResult(true);
        }

        await meter.SendAsync(Send, CancellationToken.None);
        await meter.SendAsync(Send, CancellationToken.None);

        await Assert.That(sent[0].Length).IsEqualTo(UsageMeter.MaxBatch);
        await Assert.That(sent[0].Concat(sent[1]).Select(entry => entry.Key).ToHashSet().SetEquals(keys)).IsTrue();
    }

    [Test]
    [Arguments("?granularity=week", "granularity must be hour or day")]
    [Arguments("?from=yesterday", "from and to must be RFC 3339 instants from 1970 to 9998")]
    [Arguments("?from=1969-12-31T00:00:00Z", "from and to must be RFC 3339 instants from 1970 to 9998")]
    [Arguments("?to=9999-12-31T23:30:00Z", "from and to must be RFC 3339 instants from 1970 to 9998")]
    [Arguments("?from=10:00", "from and to must be RFC 3339 instants from 1970 to 9998")]
    [Arguments("?from=2026-10-06", "from and to must be RFC 3339 instants from 1970 to 9998")]
    [Arguments("?to=2026-10-06T10:00:00", "from and to must be RFC 3339 instants from 1970 to 9998")]
    [Arguments("?to=10/06/2026 10:00:00 +00:00", "from and to must be RFC 3339 instants from 1970 to 9998")]
    [Arguments("?from=2026-10-06T12:00:00Z&to=2026-10-06T10:00:00Z", "from must be before to, at most 840 hours or 400 days earlier")]
    [Arguments("?from=2026-10-06T10:45:00Z&to=2026-10-06T10:15:00Z", "from must be before to, at most 840 hours or 400 days earlier")]
    [Arguments("?from=2026-10-06T10:15:00Z&to=2026-10-06T10:15:00Z", "from must be before to, at most 840 hours or 400 days earlier")]
    [Arguments("?from=2026-01-01T00:00:00Z&to=2026-03-01T00:00:00Z", "from must be before to, at most 840 hours or 400 days earlier")]
    [Arguments("?granularity=day&from=2020-01-01T00:00:00Z&to=2026-01-01T00:00:00Z", "from must be before to, at most 840 hours or 400 days earlier")]
    public async Task A_usage_period_that_is_not_one_of_at_most_840_hours_or_400_days_is_invalid(string query, string message)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();

        foreach (var path in new[] { database.Path, database.ProjectPath })
        {
            await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(path + "/usage" + query)), HttpStatusCode.BadRequest, "invalid_request", message);
        }

        using var offset = await api.Client.GetAsync(Uri(database.Path + "/usage?from=2026-10-06T10:00:00.5-03:00&to=2026-10-06T14:00:00%2B00:00"));
        await Assert.That(offset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await offset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("series").GetArrayLength()).IsEqualTo(1);
        using var longest = await api.Client.GetAsync(Uri(database.Path + "/usage?granularity=day&from=2025-01-01T00:00:00Z&to=2026-02-05T00:00:00Z"));
        await Assert.That(longest.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await longest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("series").GetArrayLength()).IsEqualTo(400);
    }

    [Test]
    public async Task Transactions_patches_and_queries_count_committed_writes_and_answered_reads()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var transactions = Uri($"{database.Data}/transactions");
        var (a, b) = (ManagementApi.DocumentId("a"), ManagementApi.DocumentId("b"));

        // Each write of a committed transaction counts; its assertions do not.
        using var committed = await data.PostAsJsonAsync(transactions, new
        {
            ops = new object[]
            {
                new { op = "create", collection = "people", id = a, document = new { name = Faker.Name.FullName() } },
                new { op = "create", collection = "people", id = b, document = new { name = Faker.Name.FullName() } },
                new { op = "assert", collection = "people", id = ManagementApi.DocumentId("c"), absent = true },
            },
        });
        // A failed assertion rolls the transaction back: its delete is not counted.
        using var rolledBack = await data.PostAsJsonAsync(transactions, new
        {
            ops = new object[]
            {
                new { op = "delete", collection = "people", id = a },
                new { op = "assert", collection = "people", id = b, absent = true },
            },
        });
        using var patch = new HttpRequestMessage(HttpMethod.Patch, Uri(ManagementApi.DocumentPath(database, "people", "a")))
        {
            Content = new StringContent("""{"city":"Lisbon"}""", System.Text.Encoding.UTF8, "application/merge-patch+json"),
        };
        using var patched = await data.SendAsync(patch);
        using var queried = await data.PostAsJsonAsync(Uri($"{database.Data}/collections/people/query"), new { limit = 1 });
        // Rejected before the engine answers: no read.
        using var unknownCollection = await data.GetAsync(Uri(ManagementApi.DocumentPath(database, "nothing", "a")));

        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/usage")));

        await Assert.That(committed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(rolledBack.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(patched.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(queried.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(unknownCollection.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(usage.RootElement.GetProperty("writes").GetInt64()).IsEqualTo(3);
        await Assert.That(usage.RootElement.GetProperty("reads").GetInt64()).IsEqualTo(1);
        await Assert.That(usage.RootElement.GetProperty("documents").GetInt64()).IsEqualTo(2);
    }

    [Test]
    public async Task Project_usage_sums_every_database()
    {
        // A fake clock: on a slow machine the request's 5 seconds of wall-clock time would end it early. It moves between
        // creations, so database ids (UUIDv7) keep their creation order.
        var time = new FakeTimeProvider();
        await using var api = await ManagementApi.StartAsync(time: time);
        var first = await api.CreateManagedDatabaseAsync("first");
        time.Advance(TimeSpan.FromMilliseconds(1));
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        using var data = await api.CreateClientAsync(first.Organization, "documents:read", "documents:write");
        await WriteAsync(data, first, "people", 2);
        await WriteAsync(data, second, "people", 3);
        (await data.GetAsync(Uri(Document(second, "people", "d0")))).EnsureSuccessStatusCode();

        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(first.ProjectPath + "/usage")));

        var root = usage.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(root.GetProperty("projectId").GetString()).IsEqualTo(first.Project);
        await Assert.That(root.GetProperty("databases").GetInt64()).IsEqualTo(2);
        await Assert.That(items.Select(item => item.GetProperty("databaseId").GetString()!).ToList()).IsEquivalentTo([first.Id, second.Id]);
        await Assert.That(items[0].GetProperty("databaseId").GetString()).IsEqualTo(first.Id);
        await Assert.That(root.GetProperty("documents").GetInt64()).IsEqualTo(5);
        await Assert.That(root.GetProperty("writes").GetInt64()).IsEqualTo(5);
        await Assert.That(root.GetProperty("reads").GetInt64()).IsEqualTo(1);
        await Assert.That(root.GetProperty("storageBytes").GetInt64()).IsEqualTo(items.Sum(item => item.GetProperty("storageBytes").GetInt64()));
    }

    [Test]
    public async Task Project_usage_reads_every_database_across_catalog_pages()
    {
        // A fake clock: on a slow machine the request's 5 seconds of wall-clock time would end it early. It moves between
        // creations, so database ids (UUIDv7) keep their creation order.
        var time = new FakeTimeProvider();
        await using var api = await ManagementApi.StartAsync(time: time);
        var first = await api.CreateManagedDatabaseAsync("db-0");
        List<string> ids = [first.Id];
        // More than one catalog page of 10 databases.
        for (var i = 1; i < 12; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            ids.Add((await api.CreateManagedDatabaseAsync(first.Organization, first.Project, $"db-{i}")).Id);
        }

        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(first.ProjectPath + "/usage")));

        await Assert.That(usage.RootElement.GetProperty("databases").GetInt64()).IsEqualTo(12);
        await Assert.That(usage.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!).ToList())
            .IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_usage_whose_counts_outlast_its_deadline_is_too_expensive()
    {
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: hook.Install);
        var first = await api.CreateManagedDatabaseAsync("db-0");
        await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "db-1");
        // Each database's count takes 2 seconds of the request's 5.
        hook.BeforeBegin = (database, _) =>
        {
            if (database.ProjectId == first.Project)
            {
                time.Advance(TimeSpan.FromSeconds(2));
            }

            return ValueTask.CompletedTask;
        };

        using var two = await api.Client.GetAsync(Uri(first.ProjectPath + "/usage"));
        await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "db-2");
        using var three = await api.Client.GetAsync(Uri(first.ProjectPath + "/usage"));

        // The third count starts 4 seconds in and ends 6 seconds in: its usage is not read.
        await Assert.That(two.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(three.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await three.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
    }

    [Test]
    public async Task Project_usage_whose_size_lookup_outlasts_its_deadline_starts_no_count()
    {
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        var counts = 0;
        // Reading the database's size takes 6 seconds of the request's 5.
        hook.BeforeSize = (sized, _) =>
        {
            if (sized.DatabaseId == database.Id)
            {
                time.Advance(TimeSpan.FromSeconds(6));
            }

            return ValueTask.CompletedTask;
        };
        hook.BeforeBegin = (opened, _) =>
        {
            if (opened.DatabaseId == database.Id)
            {
                Interlocked.Increment(ref counts);
            }

            return ValueTask.CompletedTask;
        };

        using var response = await api.Client.GetAsync(Uri(database.ProjectPath + "/usage"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
        await Assert.That(counts).IsEqualTo(0);
    }

    [Test]
    public async Task Project_usage_that_waits_for_a_flush_past_its_deadline_reads_no_usage()
    {
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await WriteAsync(data, database, "people", 1);
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        var counting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hook.BeforeBegin = (opened, _) =>
        {
            if (opened.DatabaseId == database.Id)
            {
                counting.TrySetResult();
            }

            return ValueTask.CompletedTask;
        };

        // A flush holds the meter while the request counts, then waits for it; meanwhile the request's 5 seconds pass.
        using var gate = hook.PauseCommit();
        var flush = meter.FlushAsync(CancellationToken.None).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var read = api.Client.GetAsync(Uri(database.ProjectPath + "/usage"));
        await counting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(6));
        gate.Dispose();
        await flush.WaitAsync(TimeSpan.FromSeconds(10));
        using var response = await read.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
    }

    [Test]
    public async Task Project_usage_whose_last_tenant_lookup_outlasts_its_deadline_and_finds_no_database_is_too_expensive()
    {
        var time = new FakeTimeProvider();
        var vanish = false;
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => SlowManagement.Wrap(
                (IManagementService)registration.ImplementationFactory!(provider),
                method =>
                {
                    // The lookup takes 6 seconds of the request's 5...
                    if (Volatile.Read(ref vanish) && method == nameof(IManagementService.GetDatabaseTenantAsync))
                    {
                        time.Advance(TimeSpan.FromSeconds(6));
                    }
                },
                // ...and finds the database deleted meanwhile.
                method => Volatile.Read(ref vanish) && method == nameof(IManagementService.GetDatabaseTenantAsync) ? new ValueTask<TenantContext?>(default(TenantContext)) : null));
        });
        var database = await api.CreateManagedDatabaseAsync();
        Volatile.Write(ref vanish, true);

        using var response = await api.Client.GetAsync(Uri(database.ProjectPath + "/usage"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
    }

    [Test]
    public async Task Project_usage_starts_no_count_after_a_tenant_lookup_that_outlasts_its_deadline()
    {
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        var slowLookups = false;
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
        {
            hook.Install(services);
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => SlowManagement.Wrap((IManagementService)registration.ImplementationFactory!(provider), method =>
            {
                // Resolving a database's tenant takes 6 seconds of the request's 5.
                if (Volatile.Read(ref slowLookups) && method == nameof(IManagementService.GetDatabaseTenantAsync))
                {
                    time.Advance(TimeSpan.FromSeconds(6));
                }
            }));
        });
        var database = await api.CreateManagedDatabaseAsync();
        var counts = 0;
        hook.BeforeBegin = (opened, _) =>
        {
            if (opened.DatabaseId == database.Id)
            {
                Interlocked.Increment(ref counts);
            }

            return ValueTask.CompletedTask;
        };
        Volatile.Write(ref slowLookups, true);

        using var response = await api.Client.GetAsync(Uri(database.ProjectPath + "/usage"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
        await Assert.That(counts).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Project_usage_whose_last_listing_outlasts_its_deadline_is_too_expensive_even_without_a_database(bool deleted)
    {
        var time = new FakeTimeProvider();
        var slow = false;
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => SlowManagement.Wrap(
                (IManagementService)registration.ImplementationFactory!(provider),
                method =>
                {
                    if (Volatile.Read(ref slow) && method == nameof(IManagementService.ListDatabasesAsync))
                    {
                        time.Advance(TimeSpan.FromSeconds(6));
                    }
                },
                // The slow listing finds its project deleted meanwhile.
                method => deleted && Volatile.Read(ref slow) && method == nameof(IManagementService.ListDatabasesAsync)
                    ? new ValueTask<ManagementPage<ManagedDatabase>?>(default(ManagementPage<ManagedDatabase>))
                    : null));
        });
        var organizationId = await api.CreateOrganizationAsync("Empty");
        var project = await api.CreateProjectAsync(organizationId, "Empty");
        Volatile.Write(ref slow, true);

        using var response = await api.Client.GetAsync(Uri($"/v1/management/organizations/{organizationId}/projects/{project}/usage"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadFromJsonAsync<string>()).IsEqualTo("query too expensive");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Overlapping_flushes_count_interleaved_operations_once_and_retry_failed_commits(bool failFirstCommit)
    {
        // These handshakes assert ordering, not a ten-second performance contract; the suite timeout bounds them.
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        await WriteAsync(data, database, "people", 3);
        (await data.GetAsync(Uri(Document(database, "people", "d1")))).EnsureSuccessStatusCode();

        using var firstGate = hook.PauseCommit(failFirstCommit);
        var firstFlush = meter.FlushAsync(CancellationToken.None).AsTask();
        await firstGate.Entered.Task.WaitAsync(cancellationToken);
        await WriteAsync(data, database, "people", 2);
        for (var i = 0; i < 2; i++)
        {
            (await data.GetAsync(Uri(Document(database, "people", "d1")))).EnsureSuccessStatusCode();
        }

        using var secondGate = hook.PauseCommit();
        var secondFlush = meter.FlushAsync(CancellationToken.None).AsTask();
        await Assert.That(secondFlush.IsCompleted).IsFalse();
        // These counts are uncommitted. A read-only connection must see none of this transaction's writes.
        await Assert.That(await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id)).IsNull();
        firstGate.Dispose();
        if (failFirstCommit)
        {
            await Assert.ThrowsAsync<IOException>(() => firstFlush);
        }
        else
        {
            await firstFlush.WaitAsync(cancellationToken);
        }

        await secondGate.Entered.Task.WaitAsync(cancellationToken);
        if (failFirstCommit)
        {
            await Assert.That(await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id)).IsNull();
        }
        else
        {
            await AssertSavedAsync(api, database, reads: 1, writes: 3);
        }

        // More operations arrive after the second snapshot, and a usage reader overlaps its commit.
        await WriteAsync(data, database, "people", 1);
        (await data.GetAsync(Uri(Document(database, "people", "d1")))).EnsureSuccessStatusCode();
        var usageRead = api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage"));
        secondGate.Dispose();
        await secondFlush.WaitAsync(cancellationToken);
        var usage = await usageRead;
        await Assert.That(usage.GetProperty("reads").GetInt64()).IsEqualTo(4);
        await Assert.That(usage.GetProperty("writes").GetInt64()).IsEqualTo(6);
        await AssertSavedAsync(api, database, reads: 3, writes: 5);

        await meter.FlushAsync(CancellationToken.None);
        await AssertSavedAsync(api, database, reads: 4, writes: 6);
        await meter.FlushAsync(CancellationToken.None);
        await AssertSavedAsync(api, database, reads: 4, writes: 6);
        var final = await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage"));
        await Assert.That(final.GetProperty("reads").GetInt64()).IsEqualTo(4);
        await Assert.That(final.GetProperty("writes").GetInt64()).IsEqualTo(6);
    }

    [Test]
    public async Task A_flush_that_fails_after_its_commit_is_not_counted_again()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        await WriteAsync(data, database, "people", 2);

        // The usage write commits, then the flush fails as if disposing the transaction had thrown.
        using (var gate = hook.PauseAfterCommit(fail: true))
        {
            var flush = meter.FlushAsync(CancellationToken.None).AsTask();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            gate.Dispose();
            await Assert.ThrowsAsync<IOException>(() => flush);
        }

        await AssertSavedAsync(api, database, reads: 0, writes: 2);
        var usage = await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path + "/usage"));
        await WriteAsync(data, database, "people", 1);
        await meter.FlushAsync(CancellationToken.None);

        await Assert.That(usage.GetProperty("writes").GetInt64()).IsEqualTo(2);
        await AssertSavedAsync(api, database, reads: 0, writes: 3);
    }

    [Test]
    public async Task Counts_of_a_database_deleted_during_a_paused_flush_are_dropped_and_its_recreated_name_starts_over()
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var meter = api.Server.Services.GetRequiredService<UsageMeter>();
        await WriteAsync(data, database, "people", 1);
        await meter.FlushAsync(CancellationToken.None);
        await AssertSavedAsync(api, database, reads: 0, writes: 1);
        await WriteAsync(data, database, "people", 1);

        // The flush has its pending snapshot but has not acquired the catalog write transaction yet.
        using var gate = hook.PauseWrite();
        var flush = meter.FlushAsync(CancellationToken.None).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deleted = await api.Client.DeleteAsync(Uri(database.Path));
        deleted.EnsureSuccessStatusCode();
        // The data API creates a database by name in the catalog: the same name is a new database with a new id.
        using var recreated = await data.PutAsync(Uri(database.Data), null);
        recreated.EnsureSuccessStatusCode();
        var recreatedId = (await recreated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await WriteAsync(data, database, "people", 1);
        (await data.GetAsync(Uri(Document(database, "people", "d0")))).EnsureSuccessStatusCode();
        gate.Dispose();
        await flush.WaitAsync(TimeSpan.FromSeconds(10));
        await meter.FlushAsync(CancellationToken.None);

        await Assert.That(recreatedId).IsNotEqualTo(database.Id);
        await Assert.That(await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id)).IsNull();
        using var usage = await api.Client.GetAsync(Uri(database.Path + "/usage"));
        await Assert.That(usage.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await AssertSavedAsync(api, database with { Id = recreatedId }, reads: 1, writes: 1);
    }

    [Test]
    public async Task The_background_flush_saves_every_ten_seconds_and_retries_a_failed_usage_save_on_the_next_tick()
    {
        var time = new FakeTimeProvider();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: time, services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await WriteAsync(data, database, "people", 1);
        using var run = await api.Client.PostAsJsonAsync(Uri(database.Path + "/sql"), new { sql = "SELECT * FROM people WHERE city = 'x'" });
        run.EnsureSuccessStatusCode();

        // The first tick's usage save fails; the same tick still saves the query field counts.
        using var failure = hook.PauseCommit(fail: true);
        time.Advance(TimeSpan.FromSeconds(10));
        await failure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        failure.Dispose();
        await UntilAsync(async () => await CountSavedQueryFieldsAsync(api.Server.DataDirectory, database.Id) == 1);
        await Assert.That(await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id)).IsNull();

        // The next tick saves the counts the failure kept pending: the SQL run is a read.
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(async () => await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id) is not null);
        await AssertSavedAsync(api, database, reads: 1, writes: 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_database_deleted_while_its_usage_is_read_is_not_found_and_left_out_of_its_project(bool project)
    {
        var hook = new UsageCommitHook();
        // A fake clock: the paused count must not use up the request's 5 seconds of wall-clock time.
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var deleted = await api.CreateManagedDatabaseAsync("deleted");
        var kept = await api.CreateManagedDatabaseAsync(deleted.Organization, deleted.Project, "kept");
        // The read has checked membership, measured the file and opened the storage; it pauses before its count transaction.
        UsageCommitHook.Gate? armed = new(fail: false);
        using var gate = armed;
        hook.BeforeBegin = async (database, cancellationToken) =>
        {
            if (database.DatabaseId == deleted.Id && Interlocked.Exchange(ref armed, null) is { } pending)
            {
                await pending.WaitAsync(cancellationToken);
            }
        };

        var read = api.Client.GetAsync(Uri((project ? deleted.ProjectPath : deleted.Path) + "/usage"));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var removed = await api.Client.DeleteAsync(Uri(deleted.Path)))
        {
            removed.EnsureSuccessStatusCode();
        }

        gate.Dispose();
        using var response = await read.WaitAsync(TimeSpan.FromSeconds(10));

        if (project)
        {
            response.EnsureSuccessStatusCode();
            var usage = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(usage.GetProperty("databases").GetInt64()).IsEqualTo(1);
            await Assert.That(usage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!)).IsEquivalentTo([kept.Id]);
        }
        else
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task A_clean_shutdown_saves_pending_counts_across_a_restart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-usage-restart-").FullName;
        try
        {
            var writes = Faker.Random.Int(1, 4);
            var reads = Faker.Random.Int(1, 4);
            string user, path;
            // The fake clocks never move, so the periodic flush never runs: only the shutdown flush can save these counts.
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider()))
            {
                user = api.User;
                var database = await api.CreateManagedDatabaseAsync();
                path = database.Path + "/usage";
                using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
                await WriteAsync(data, database, "people", writes);
                for (var i = 0; i < reads; i++)
                {
                    (await data.GetAsync(Uri(Document(database, "people", "d0")))).EnsureSuccessStatusCode();
                }

                await Assert.That(await ReadSavedOpsAsync(directory, database.Id)).IsNull();
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider()))
            {
                using var owner = api.As(user);
                var usage = await owner.GetFromJsonAsync<JsonElement>(Uri(path));
                await Assert.That(usage.GetProperty("reads").GetInt64()).IsEqualTo(reads);
                await Assert.That(usage.GetProperty("writes").GetInt64()).IsEqualTo(writes);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertSavedAsync(ManagementApi api, ManagedDatabaseRef database, long reads, long writes)
    {
        var saved = await ReadSavedOpsAsync(api.Server.DataDirectory, database.Id);
        await Assert.That(saved).IsNotNull();
        using var ops = JsonDocument.Parse(saved!);
        await Assert.That(ops.RootElement.GetProperty("reads").GetInt64()).IsEqualTo(reads);
        await Assert.That(ops.RootElement.GetProperty("writes").GetInt64()).IsEqualTo(writes);
    }

    [Test]
    public async Task Usage_of_unknown_resources_is_not_found()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var organization = $"/v1/management/organizations/{EntityIds.New()}/projects/{database.Project}";

        using var unknownDatabase = await api.Client.GetAsync(Uri($"{database.ProjectPath}/databases/{EntityIds.New()}/usage"));
        using var unknownProjectDatabase = await api.Client.GetAsync(Uri($"/v1/management/organizations/{database.Organization}/projects/{EntityIds.New()}/databases/{database.Id}/usage"));
        using var unknownProject = await api.Client.GetAsync(Uri($"/v1/management/organizations/{database.Organization}/projects/{EntityIds.New()}/usage"));
        using var unknownOrganization = await api.Client.GetAsync(Uri(organization + "/usage"));

        await Assert.That(unknownDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownProjectDatabase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownOrganization.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static List<(string Start, int Reads, int Writes)> Points(JsonElement usage) =>
        [.. usage.GetProperty("series").EnumerateArray().Select(point => (
            point.GetProperty("start").GetDateTimeOffset().ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            point.GetProperty("reads").GetInt32(), point.GetProperty("writes").GetInt32()))];

    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static long Hour(string value) => Instant(value).ToUnixTimeSeconds() / 3600;

    private static async Task AssertPeriodAsync(JsonElement usage, string from, string to, string granularity, long reads, long writes)
    {
        var period = usage.GetProperty("period");
        await Assert.That(period.GetProperty("from").GetDateTimeOffset()).IsEqualTo(DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(period.GetProperty("to").GetDateTimeOffset()).IsEqualTo(DateTimeOffset.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(period.GetProperty("granularity").GetString()).IsEqualTo(granularity);
        await Assert.That(period.GetProperty("reads").GetInt64()).IsEqualTo(reads);
        await Assert.That(period.GetProperty("writes").GetInt64()).IsEqualTo(writes);
    }

    private static async Task<long> CountSendersAsync(string dataDirectory)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "_system.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM documents WHERE collection = 'usage-senders' AND body IS NOT NULL;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // The saved bucket ids of a database, straight from the catalog file, read-only.
    private static async Task<List<string>> BucketsAsync(string dataDirectory, string databaseId, string granularity)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM documents WHERE collection = $collection AND body IS NOT NULL ORDER BY id;";
        command.Parameters.AddWithValue("$collection", $"databases/{databaseId}/usage-{granularity}");
        List<string> ids = [];
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task WriteAsync(HttpClient data, ManagedDatabaseRef database, string collection, int count)
    {
        await ManagementApi.CreateCollectionAsync(data, database, collection);
        for (var i = 0; i < count; i++)
        {
            (await data.PutAsJsonAsync(Uri(Document(database, collection, $"d{i}")), new { name = Faker.Name.FullName(), city = Faker.Address.City() })).EnsureSuccessStatusCode();
        }
    }

    // Reads the flushed counters straight from the catalog file, read-only.
    private static async Task<string?> ReadSavedOpsAsync(string dataDirectory, string databaseId)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM documents WHERE collection = $collection AND id = 'ops';";
        command.Parameters.AddWithValue("$collection", $"databases/{databaseId}/usage");
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<long> CountSavedQueryFieldsAsync(string dataDirectory, string databaseId)
    {
        var path = Path.Combine(dataDirectory, "_system.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM documents WHERE collection = $collection AND body IS NOT NULL;";
        command.Parameters.AddWithValue("$collection", $"databases/{databaseId}/query_fields");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Background work finishes on its own schedule: polls for up to 10 seconds.
    private static async Task UntilAsync(Func<Task<bool>> done)
    {
        for (var attempt = 0; !await done(); attempt++)
        {
            if (attempt == 200)
            {
                throw new TimeoutException("the background flush did not save in time");
            }

            await Task.Delay(50);
        }
    }

    private static string Document(ManagedDatabaseRef database, string collection, string name) => ManagementApi.DocumentPath(database, collection, name);

    // Forwards every call to the management service, after telling the test which method runs; a call that `answer` returns
    // a result for answers that instead.
    public class SlowManagement : DispatchProxy
    {
        private IManagementService _inner = null!;
        private Action<string> _before = null!;
        private Func<string, object?>? _answer;

        public static IManagementService Wrap(IManagementService inner, Action<string> before, Func<string, object?>? answer = null)
        {
            var proxy = Create<IManagementService, SlowManagement>();
            var slow = (SlowManagement)(object)proxy;
            slow._inner = inner;
            slow._before = before;
            slow._answer = answer;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            _before(targetMethod.Name);
            if (_answer?.Invoke(targetMethod.Name) is { } answer)
            {
                return answer;
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Throw(inner);
                throw;
            }
        }
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
