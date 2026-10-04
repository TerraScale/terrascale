using System.Net;
using System.Text.Json;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Slow.Documents;

public sealed class ShapeBudgetTests
{
    [Test]
    public async Task An_expensive_shape_filter_answers_invalid_query_and_the_next_read_works()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:write", "shapes:read");
        var client = tenant.Data;
        (await client.PutAsync(tenant.Database("db"), null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(tenant.Database("db", "/collections"), new { name = "items" })).EnsureSuccessStatusCode();
        var transactions = tenant.Database("db", "/transactions");
        // 20000 documents: the expensive filter exhausts the instruction budget before a page's step limit.
        for (var batch = 0; batch < 20; batch++)
        {
            var ops = Enumerable.Range(0, 1000).Select(_ => new { op = "create", collection = "items", document = new { n = 1 } });
            (await client.PostAsJsonAsync(transactions, new { ops })).EnsureSuccessStatusCode();
        }

        // Matches nothing, so every document is examined: over the 5 million instruction budget.
        var expensive = new
        {
            or = Enumerable.Range(0, 64).Select(_ => new
            {
                and = new object[] { new { exists = "/n" }, new { eq = new object[] { "/n", 999 } } },
            }),
        };
        var shape = tenant.Database("db", "/collections/items/shape").OriginalString;
        var where = Uri.EscapeDataString(JsonSerializer.Serialize(expensive));
        using var start = await client.GetAsync(new Uri($"{shape}?where={where}&offset=-1", UriKind.Relative));
        var handle = start.Headers.GetValues("TB-Shape-Handle").Single();
        using var snapshot = await client.GetAsync(start.Headers.Location!);
        using var chunk = await client.GetAsync(new Uri($"{shape}?where={where}&handle={handle}&offset=0", UriKind.Relative));
        using var cheap = await client.GetAsync(new Uri($"{shape}?offset=-1", UriKind.Relative));
        using var resumed = await client.GetAsync(cheap.Headers.Location!);
        // A position past the head is refused before the filter runs: must_refetch, not invalid_query.
        var head = long.Parse(cheap.Headers.Location!.OriginalString.Split("offset=snap.")[1].Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        using var future = await client.GetAsync(new Uri($"{shape}?where={where}&handle={handle}&offset=snap.{head + 1000}.0", UriKind.Relative));
        var futureError = (await future.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");

        foreach (var exhausted in new[] { snapshot, chunk })
        {
            var error = (await exhausted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
            await Assert.That(exhausted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("invalid_query");
            await Assert.That(error.GetProperty("message").GetString()).IsEqualTo("query too expensive");
            await Assert.That(exhausted.Headers.CacheControl!.NoStore).IsTrue();
        }

        // The budget handler is removed, so the reused session serves the next read.
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(future.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(futureError.GetProperty("code").GetString()).IsEqualTo("must_refetch");
    }

    [Test]
    public async Task A_cheap_filter_that_matches_nothing_pages_through_in_bounded_steps()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:write", "shapes:read");
        var client = tenant.Data;
        (await client.PutAsync(tenant.Database("db"), null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(tenant.Database("db", "/collections"), new { name = "items" })).EnsureSuccessStatusCode();
        var transactions = tenant.Database("db", "/transactions");
        for (var batch = 0; batch < 25; batch++)
        {
            var ops = Enumerable.Range(0, 1000).Select(_ => new { op = "create", collection = "items", document = new { n = 1 } });
            (await client.PostAsJsonAsync(transactions, new { ops })).EnsureSuccessStatusCode();
        }

        // Each step runs fewer instructions than the progress handler's interval; the page's step limit bounds it.
        var shape = tenant.Database("db", "/collections/items/shape").OriginalString;
        var where = Uri.EscapeDataString("""{"eq":["/n",2]}""");
        using var start = await client.GetAsync(new Uri($"{shape}?where={where}&offset=-1", UriKind.Relative));
        var location = start.Headers.Location!.OriginalString;
        var prefix = location[..location.LastIndexOf("offset=", StringComparison.Ordinal)];
        var next = location[(prefix.Length + "offset=".Length)..];
        var pages = new List<(HttpStatusCode Status, int Ops, string Next)>();
        while (next.StartsWith("snap.", StringComparison.Ordinal) && pages.Count < 10)
        {
            using var response = await client.GetAsync(new Uri(prefix + "offset=" + next, UriKind.Relative));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            next = body.GetProperty("next_offset").GetString()!;
            pages.Add((response.StatusCode, body.GetProperty("changes")[0].GetProperty("ops").GetArrayLength(), next));
        }

        // 25000 ids at 10000 per page: two empty pages that continue, then the last one ends at S.
        await Assert.That(pages.Select(page => page.Status).Distinct().ToList()).IsEquivalentTo([HttpStatusCode.OK]);
        await Assert.That(pages.Count).IsEqualTo(3);
        await Assert.That(pages.Sum(page => page.Ops)).IsEqualTo(0);
        await Assert.That(pages[0].Next).StartsWith("snap.");
        await Assert.That(next.StartsWith("snap.", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task A_snapshot_replays_byte_identically_after_later_inserts_cross_the_step_limit()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:write", "shapes:read");
        var client = tenant.Data;
        (await client.PutAsync(tenant.Database("db"), null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(tenant.Database("db", "/collections"), new { name = "items" })).EnsureSuccessStatusCode();
        var transactions = tenant.Database("db", "/transactions");
        var kept = Guid.CreateVersion7().ToString();
        (await client.PutAsJsonAsync(tenant.Database("db", $"/collections/items/documents/{kept}"), new { n = 1 }))
            .EnsureSuccessStatusCode();
        using var start = await client.GetAsync(tenant.Database("db", "/collections/items/shape?offset=-1"));
        using var first = await client.GetAsync(start.Headers.Location!);
        var firstBody = await first.Content.ReadAsStringAsync();

        // More new ids than one page examines, written after S, many of them ordering before the kept document.
        for (var batch = 0; batch < 11; batch++)
        {
            var ops = Enumerable.Range(0, 1000).Select(_ => new { op = "put", collection = "items", id = Guid.NewGuid().ToString(), document = new { n = 2 } });
            (await client.PostAsJsonAsync(transactions, new { ops })).EnsureSuccessStatusCode();
        }

        using var replay = await client.GetAsync(start.Headers.Location!);
        var replayBody = await replay.Content.ReadAsStringAsync();
        var page = JsonElement.Parse(replayBody);

        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replayBody).IsEqualTo(firstBody);
        await Assert.That(replay.Headers.ETag!.Tag).IsEqualTo(first.Headers.ETag!.Tag);
        await Assert.That(page.GetProperty("next_offset").GetString()!.StartsWith("snap.", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task A_snapshot_too_old_for_bounded_work_answers_must_refetch_and_a_restart_succeeds()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:write", "shapes:read");
        var client = tenant.Data;
        (await client.PutAsync(tenant.Database("db"), null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(tenant.Database("db", "/collections"), new { name = "items" })).EnsureSuccessStatusCode();
        var transactions = tenant.Database("db", "/transactions");
        // The snapshot's only document orders last, so a replay steps over every later id before reaching it.
        var kept = "ffffffff-ffff-7fff-bfff-ffffffffffff";
        (await client.PutAsJsonAsync(tenant.Database("db", $"/collections/items/documents/{kept}"), new { n = 1 })).EnsureSuccessStatusCode();
        using var start = await client.GetAsync(tenant.Database("db", "/collections/items/shape?offset=-1"));
        using var first = await client.GetAsync(start.Headers.Location!);
        var firstBody = await first.Content.ReadAsStringAsync();

        for (var batch = 0; batch < 200; batch++)
        {
            var ops = Enumerable.Range(0, 1000).Select(_ => new { op = "put", collection = "items", id = Guid.NewGuid().ToString(), document = new { n = 2 } });
            (await client.PostAsJsonAsync(transactions, new { ops })).EnsureSuccessStatusCode();
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var replay = await client.GetAsync(start.Headers.Location!);
        timer.Stop();
        Console.WriteLine($"replay over 200000 later ids: {(int)replay.StatusCode} in {timer.ElapsedMilliseconds} ms");
        var error = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        using var restart = await client.GetAsync(tenant.Database("db", "/collections/items/shape?offset=-1"));
        using var fresh = await client.GetAsync(restart.Headers.Location!);

        // Stepping over 200000 later ids exceeds the page's bounded work: never other page boundaries, never a
        // query-budget error, but must_refetch; the new snapshot at the head has no later ids to step over.
        await Assert.That(firstBody.Length).IsGreaterThan(0);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("must_refetch");
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
