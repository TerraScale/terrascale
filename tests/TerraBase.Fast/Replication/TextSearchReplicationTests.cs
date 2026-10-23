using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Replication;

public sealed class TextSearchReplicationTests
{
    private static readonly string[] TextIndexes = ["text_docs"];
    private static readonly string[] TitleFields = ["title"];
    private static readonly string[] HeadingSearch = ["/title", "heading"];

    [Test]
    public async Task Replicas_build_text_indexes_from_metadata_and_follow_updates_deletes_and_redefinitions()
    {
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = DocumentApi.NewId();
        _ = DocumentField.TryParse("body", out var body);
        _ = DocumentField.TryParse("title", out var title);
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("tb_text_text_docs__0", 1), CancellationToken.None);
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes("""{"body":"original","title":"heading"}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"body":"original","title":"heading"}""")).IsTrue();
        await gru.WriteAsync(async transaction => _ = await transaction.CreateIndexAsync(new IndexDefinition("text_docs", "items", [body], "text"), CancellationToken.None));
        await Assert.That(await sjc.UntilIndexesAsync(TextIndexes)).IsTrue();
        using var client = await sjc.Api.CreateClientAsync(cluster.Database.OrganizationId, "documents:read");
        var query = new Uri($"/v1/projects/{cluster.Database.ProjectId}/databases/main/collections/items/query", UriKind.Relative);
        using var customerCollection = await client.PostAsJsonAsync(
            new Uri($"/v1/projects/{cluster.Database.ProjectId}/databases/main/collections/tb_text_text_docs__0/query", UriKind.Relative), new { limit = 1 });
        await Assert.That(customerCollection.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await MatchesAsync(client, query, "/body", "original", id);

        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes("""{"body":"replacement","title":"heading"}"""), CancellationToken.None));
        await Assert.That(await sjc.UntilAsync(id, """{"body":"replacement","title":"heading"}""")).IsTrue();
        await MatchesAsync(client, query, "/body", "replacement", id);
        await MatchesAsync(client, query, "/body", "original");

        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => _ = await transaction.DropIndexAsync("text_docs", CancellationToken.None));
        await gru.WriteAsync(async transaction => _ = await transaction.CreateIndexAsync(new IndexDefinition("text_docs", "items", [title], "text"), CancellationToken.None));
        await sjc.StartAsync();
        await Assert.That(await sjc.UntilIndexAsync("text_docs", "items", TitleFields)).IsTrue();
        using var reopened = await sjc.Api.CreateClientAsync(cluster.Database.OrganizationId, "documents:read");
        await MatchesAsync(reopened, query, "/title", "heading", id);
        await gru.WriteAsync(async transaction => _ = await transaction.DeleteAsync("items", id, CancellationToken.None));
        await Assert.That(await sjc.UntilAbsentAsync(id)).IsTrue();
        await MatchesAsync(reopened, query, "/title", "heading");
        await gru.WriteAsync(async transaction => _ = await transaction.DropIndexAsync("text_docs", CancellationToken.None));
        await Assert.That(await sjc.UntilIndexesAsync([])).IsTrue();
        using var missingIndex = await reopened.PostAsJsonAsync(query, new { filter = new { search = HeadingSearch }, sort = new[] { new { path = "$score", order = "desc" } } });
        await Assert.That(missingIndex.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await missingIndex.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_query");
    }

    private static async Task MatchesAsync(HttpClient client, Uri query, string path, string terms, params string[] expected)
    {
        using var response = await client.PostAsJsonAsync(query, new { filter = new { search = new[] { path, terms } }, sort = new[] { new { path = "$score", order = "desc" } } });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo(expected);
    }
}
