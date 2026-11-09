using System.Globalization;
using TerraBase.Features.Sync;
using TerraBase.Slow.Platform;

namespace TerraBase.Slow.Sync;

public sealed class BoundedPullMemoryTests
{
    private const string ChildFlag = "TERRABASE_TEST_BOUNDED_PULL_CHILD";
    private const long Budget = 32L * 1024 * 1024;
    private const long DatabaseBytes = 8 * Budget;
    private const long RssGrowthLimit = 2 * Budget;

    [Test]
    public async Task AFullPullOfADatabaseEightTimesTheMemoryBudgetKeepsRssBounded(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            await IsolatedTestChild.RunAsync(typeof(BoundedPullMemoryTests), nameof(AFullPullOfADatabaseEightTimesTheMemoryBudgetKeepsRssBounded), ChildFlag);
            return;
        }

        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        var database = await server.CreateSyncedDatabaseAsync();
        await server.FillAsync(database, DatabaseBytes);
        await server.ExecuteAsync(database, "PRAGMA wal_checkpoint(TRUNCATE)");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await File.WriteAllTextAsync("/proc/self/clear_refs", "5", cancellationToken);
        var baseline = Status("VmRSS");

        using var response = await server.StartPullAsync(database, cancellationToken);
        await using var answer = new BufferedStream(await response.Content.ReadAsStreamAsync(cancellationToken), 64 * 1024);
        var header = await answer.ReadMessageAsync(PullUpdatesHeader.Parser, cancellationToken);
        var pages = 0UL;
        var bytes = 0L;
        while (await answer.ReadMessageAsync(PageData.Parser, cancellationToken) is { } page)
        {
            await Assert.That(page.PageId).IsEqualTo(pages);
            pages++;
            bytes += page.EncodedPage.Length;
        }

        var growth = Status("VmHWM") - baseline;
        Console.WriteLine($"bounded pull: {bytes / (1024 * 1024)} MiB in {pages} pages, budget {Budget / (1024 * 1024)} MiB, peak RSS growth {growth / (1024 * 1024)} MiB");
        await Assert.That(pages).IsEqualTo((ulong)header!.DbSize);
        await Assert.That(bytes).IsGreaterThanOrEqualTo(DatabaseBytes);
        await Assert.That(growth).IsLessThan(RssGrowthLimit);
    }

    private static long Status(string field)
    {
        var line = File.ReadLines("/proc/self/status").Single(line => line.StartsWith(field + ":", StringComparison.Ordinal));
        return long.Parse(line[(field.Length + 1)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * 1024;
    }
}
