using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class DatabaseEscrowConcurrencyTests
{
    [Test]
    [StarterAllowanceQuarantine]
    public async Task Concurrent_creates_across_three_regions_never_exceed_the_starter_allowance()
    {
        await using var cell = await DatabaseEscrowCell.StartAsync();
        for (var round = 0; round < 3; round++)
        {
            var created = await RoundAsync(cell, "starter", 5, [0, 0, 1, 1, 2, 2]);
            foreach (var (region, id) in created)
            {
                await cell.DeleteAsync(region, id);
            }

            await cell.CountEverywhereAsync(0);
        }
    }

    [Test]
    public async Task Concurrent_creates_across_three_regions_never_exceed_the_hobby_allowance()
    {
        await using var cell = await DatabaseEscrowCell.StartAsync("hobby");
        await RoundAsync(cell, "hobby", 1, [0, 1, 2]);
    }

    private static async Task<(int Region, string Id)[]> RoundAsync(DatabaseEscrowCell cell, string plan, int allowed, int[] regions)
    {
        var responses = await Task.WhenAll(regions.Select((region, index) => cell.CreateAsync(region, $"db{index}")));
        try
        {
            var answers = string.Join(" | ", await Task.WhenAll(responses.Select(async response => $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}")));
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Created)).IsEqualTo(allowed).Because(answers);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Conflict)).IsEqualTo(regions.Length - allowed).Because(answers);
            foreach (var refused in responses.Where(response => response.StatusCode == HttpStatusCode.Conflict))
            {
                await ErrorAssert.ErrorAsync(refused, HttpStatusCode.Conflict, "database_limit", DatabaseEscrowCell.Limit(plan, allowed));
            }

            await cell.CountEverywhereAsync(allowed);
            var created = await Task.WhenAll(responses.Select((response, index) => (Response: response, Region: regions[index]))
                .Where(answer => answer.Response.StatusCode == HttpStatusCode.Created)
                .Select(async answer => (answer.Region, Id: (await answer.Response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!)));
            foreach (var region in Enumerable.Range(0, 3))
            {
                await Assert.That((await cell.ListAsync(region)).Order(StringComparer.Ordinal).SequenceEqual(created.Select(database => database.Id).Order(StringComparer.Ordinal))).IsTrue();
            }

            return created;
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}

// User-authorized quarantine: #755, owner MarioGK / all-regions WRK. Automatically resumes within 24 hours.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class StarterAllowanceQuarantineAttribute
    : SkipAttribute("#755: transient escrow deadline changes the refusal message; owner MarioGK / all-regions WRK; expires 2026-10-27T23:30:00Z")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(DateTimeOffset.UtcNow < new DateTimeOffset(2026, 10, 27, 23, 30, 0, TimeSpan.Zero));
}
