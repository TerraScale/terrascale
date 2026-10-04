using System.Net;
using System.Text.Json;
using Bogus;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class DocumentApiTests
{
    private static readonly Faker Faker = new();
    private static readonly int[] JsonArray = [1, 2];
    private static readonly string[] WriteScope = ["documents:write"];

    [Test]
    public async Task Documents_round_trip_through_the_client_listener()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var key = await server.IssueAsync("documents:read", "documents:write");
        using var client = tls.CreateClient(server.ClientAddress!, key);
        var person = new { name = Faker.Name.FullName(), email = Faker.Internet.Email(), age = Faker.Random.Int(18, 90) };

        using var created = await client.PutAsync(new Uri("/v1/databases/shop", UriKind.Relative), null);
        using var again = await client.PutAsync(new Uri("/v1/databases/shop", UriKind.Relative), null);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/databases/shop/collections/people/documents/p1", UriKind.Relative), person);
        using var replaced = await client.PutAsJsonAsync(new Uri("/v1/databases/shop/collections/people/documents/p1", UriKind.Relative), person);
        using var read = JsonDocument.Parse(await client.GetStringAsync(new Uri("/v1/databases/shop/collections/people/documents/p1", UriKind.Relative)));
        using var deleted = await client.DeleteAsync(new Uri("/v1/databases/shop/collections/people/documents/p1", UriKind.Relative));
        using var gone = await client.GetAsync(new Uri("/v1/databases/shop/collections/people/documents/p1", UriKind.Relative));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64()).IsEqualTo(2);
        await Assert.That(read.RootElement.GetProperty("version").GetInt64()).IsEqualTo(2);
        await Assert.That(read.RootElement.GetProperty("data").GetProperty("email").GetString()).IsEqualTo(person.email);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // The settings reached Program: the database lives in this server's own data directory.
        await Assert.That(File.Exists(Path.Combine(server.DataDirectory, "shop.db"))).IsTrue();
    }

    [Test]
    public async Task Listing_pages_through_a_collection_in_id_order()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:read", "documents:write"));
        (await client.PutAsync(new Uri("/v1/databases/catalog", UriKind.Relative), null)).Dispose();
        // An exact multiple of the page size: the last full page must already report no next page.
        var ids = Enumerable.Range(0, 4).Select(i => $"item-{i}").ToList();
        foreach (var id in Faker.Random.Shuffle(ids))
        {
            (await client.PutAsJsonAsync(
                new Uri($"/v1/databases/catalog/collections/items/documents/{id}", UriKind.Relative),
                new { title = Faker.Commerce.ProductName() })).EnsureSuccessStatusCode();
        }

        var seen = new List<string>();
        var pages = 0;
        string? after = null;
        do
        {
            pages++;
            var query = after is null ? "?limit=2" : $"?limit=2&after={after}";
            using var page = JsonDocument.Parse(await client.GetStringAsync(
                new Uri($"/v1/databases/catalog/collections/items/documents{query}", UriKind.Relative)));
            seen.AddRange(page.RootElement.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("id").GetString()!));
            after = page.RootElement.GetProperty("next").GetString();
        }
        while (after is not null);

        await Assert.That(pages).IsEqualTo(2);
        await Assert.That(seen).IsEquivalentTo(ids);
        await Assert.That(seen).IsOrderedBy(id => id, StringComparer.Ordinal);
    }

    [Test]
    public async Task Concurrent_creates_report_exactly_one_creation()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:write"));

        var statuses = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            using var response = await client.PutAsync(new Uri("/v1/databases/race", UriKind.Relative), null);
            return response.StatusCode;
        }));

        await Assert.That(statuses.Count(status => status == HttpStatusCode.Created)).IsEqualTo(1);
        await Assert.That(statuses.Count(status => status == HttpStatusCode.OK)).IsEqualTo(15);
    }

    [Test]
    public async Task The_bearer_scheme_name_is_case_insensitive()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var key = await server.IssueAsync("documents:write");
        using var client = tls.CreateClient(server.ClientAddress!);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"bEaReR {key}");

        using var response = await client.PutAsync(new Uri("/v1/databases/cased", UriKind.Relative), null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Requests_need_a_key_with_the_right_scope()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var anonymous = tls.CreateClient(server.ClientAddress!);
        using var unknown = tls.CreateClient(server.ClientAddress!, "tb_not-a-real-key");
        using var reader = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:read"));

        using var noKey = await anonymous.GetAsync(new Uri("/v1/databases/x/collections/y/documents", UriKind.Relative));
        using var badKey = await unknown.GetAsync(new Uri("/v1/databases/x/collections/y/documents", UriKind.Relative));
        using var readOnlyWrite = await reader.PutAsync(new Uri("/v1/databases/x", UriKind.Relative), null);
        using var readMissing = await reader.GetAsync(new Uri("/v1/databases/x/collections/y/documents", UriKind.Relative));

        await Assert.That(noKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(badKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(readOnlyWrite.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(readMissing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Invalid_names_and_bodies_are_rejected()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:write"));
        (await client.PutAsync(new Uri("/v1/databases/valid", UriKind.Relative), null)).Dispose();

        using var badName = await client.PutAsync(new Uri("/v1/databases/Not_Valid", UriKind.Relative), null);
        using var deviceName = await client.PutAsync(new Uri("/v1/databases/con", UriKind.Relative), null);
        using var trailingNewline = await client.PutAsync(new Uri("/v1/databases/valid%0A", UriKind.Relative), null);
        using var notObject = await client.PutAsync(
            new Uri("/v1/databases/valid/collections/c/documents/d", UriKind.Relative), JsonContent.Create(JsonArray));
        using var notJson = await client.PutAsync(
            new Uri("/v1/databases/valid/collections/c/documents/d", UriKind.Relative), new StringContent("{oops", null, "application/json"));
        using var tooLarge = await client.PutAsync(
            new Uri("/v1/databases/valid/collections/c/documents/d", UriKind.Relative),
            JsonContent.Create(new { blob = new string('x', 1024 * 1024) }));

        await Assert.That(badName.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(deviceName.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(trailingNewline.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(notObject.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(notJson.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(tooLarge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task Client_and_admin_endpoints_stay_on_their_listeners()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:write"));

        using var documentsOnAdmin = await server.Client.PutAsync(new Uri("/v1/databases/x", UriKind.Relative), null);
        using var keysOnClient = await client.PostAsJsonAsync(
            new Uri("/admin/api-keys", UriKind.Relative), new { name = "escalate", scopes = WriteScope });
        using var healthOnClient = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        await Assert.That(documentsOnAdmin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(keysOnClient.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(healthOnClient.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
