using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class UniqueConstraintReplayTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Compound_conflict_chains_converge_after_updates_and_deletes_in_any_order(bool reverse)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var client = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
        }));
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        using var declaration = await client.PutAsJsonAsync(new Uri(database.Data + "/schema/people", UriKind.Relative), new
        {
            fields = new[] { new { name = "a", unique = true }, new { name = "b", unique = true } },
        });
        declaration.EnsureSuccessStatusCode();

        var low = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        var middle = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        var high = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
        DocumentChange[] changes =
        [
            new("people", low, 10, Encoding.UTF8.GetBytes("{\"a\":1,\"b\":1}")),
            new("people", middle, 20, Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2}")),
            new("people", high, 30, Encoding.UTF8.GetBytes("{\"a\":2,\"b\":2}")),
            new("people", low, 40, null),
            new("people", middle, 50, Encoding.UTF8.GetBytes("{\"a\":3,\"b\":3}")),
        ];
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            foreach (var change in reverse ? changes.Reverse() : changes)
            {
                await transaction.ApplyAsync(change, CancellationToken.None);
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        using var page = await client.GetAsync(new Uri(database.Data + "/collections/people/documents", UriKind.Relative));
        page.EnsureSuccessStatusCode();
        var documents = await page.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(documents.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([middle, high]);
        using var missing = await client.GetAsync(new Uri(database.Data + "/collections/people/documents/" + low, UriKind.Relative));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var duplicate = await client.PutAsJsonAsync(new Uri(database.Data + "/collections/people/documents/" + Ulid.NewUlid(), UriKind.Relative), new { a = 2, b = 4 });
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }
}
