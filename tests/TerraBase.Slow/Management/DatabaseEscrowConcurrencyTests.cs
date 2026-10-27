using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class DatabaseEscrowConcurrencyTests
{
    private const string AllowanceElsewhere = "the rest of the organization's database allowance is held in regions this region cannot reach now; retry shortly";

    [Test]
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
                // Before catalog convergence a peer transfer can hit its deadline even though the other creates
                // have spent every unit. Both refusals are safe; exact capacity and every refusal's envelope still count.
                var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
                var message = body.GetProperty("error").GetProperty("message").GetString();
                await Assert.That(message == DatabaseEscrowCell.Limit(plan, allowed) || message == AllowanceElsewhere).IsTrue().Because(answers);
                await ErrorAssert.ErrorAsync(refused, HttpStatusCode.Conflict, "database_limit", message);
            }

            await cell.CountEverywhereAsync(allowed);
            var created = await Task.WhenAll(responses.Select((response, index) => (Response: response, Region: regions[index]))
                .Where(answer => answer.Response.StatusCode == HttpStatusCode.Created)
                .Select(async answer => (answer.Region, Id: (await answer.Response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!)));
            foreach (var region in Enumerable.Range(0, 3))
            {
                // Every catalog now sees all successful databases. Full capacity must refuse locally with the exact
                // exhausted-plan message, independently of peer scheduling, and must not create another database.
                await ErrorAssert.ErrorAsync(cell.CreateAsync(region, "full"), HttpStatusCode.Conflict, "database_limit", DatabaseEscrowCell.Limit(plan, allowed));
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
