using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class SchemaApiTests
{

    private static readonly Faker Faker = new();

    [Test]
    public async Task Collections_are_listed_with_document_counts_and_bytes()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var empty = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/collections")));
        var people = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            people.Add(await PutAsync(data, database, "people", $"p{i}", new { name = Faker.Name.FullName(), email = Faker.Internet.Email() }));
        }

        var orders = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            orders.Add(await PutAsync(data, database, "orders", $"o{i}", new { total = Faker.Random.Decimal(1, 500), item = Faker.Commerce.ProductName() }));
        }

        using var listed = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/collections")));

        await Assert.That(empty.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var items = listed.RootElement.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(items.Select(item => item.GetProperty("name").GetString()!)).IsEquivalentTo(["orders", "people"]);
        await Assert.That(items[0].GetProperty("name").GetString()).IsEqualTo("orders");
        await Assert.That(items[0].GetProperty("documents").GetInt64()).IsEqualTo(2);
        await Assert.That(items[0].GetProperty("bytes").GetInt64()).IsEqualTo(orders.Sum(StoredBytes));
        await Assert.That(items[1].GetProperty("documents").GetInt64()).IsEqualTo(3);
        await Assert.That(items[1].GetProperty("bytes").GetInt64()).IsEqualTo(people.Sum(StoredBytes));
    }

    // Storage keeps the materialized document: canonical JSON with relaxed escaping, fields in name order.
    private static long StoredBytes(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return DocumentState.Bottom.Put(default, parsed.RootElement).Materialize()!.Length;
    }

    [Test]
    public async Task The_schema_is_inferred_from_nested_and_mixed_documents()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await PutAsync(data, database, "people", "a", new
        {
            name = Faker.Name.FullName(),
            age = Faker.Random.Int(18, 90),
            address = new { city = Faker.Address.City(), zip = Faker.Address.ZipCode() },
            tags = new[] { Faker.Lorem.Word() },
        });
        await PutAsync(data, database, "people", "b", new
        {
            name = Faker.Name.FullName(),
            age = Faker.Lorem.Word(),
            address = new { city = Faker.Address.City() },
            score = 1.5,
        });
        await PutAsync(data, database, "people", "c", new { name = (string?)null, age = Faker.Random.Int(18, 90), active = Faker.Random.Bool() });

        using var schema = JsonDocument.Parse(await api.Client.GetStringAsync(Uri(database.Path + "/collections/people/schema")));

        var root = schema.RootElement;
        await Assert.That(root.GetProperty("collection").GetString()).IsEqualTo("people");
        await Assert.That(root.GetProperty("documents").GetInt64()).IsEqualTo(3);
        await Assert.That(root.GetProperty("sampledDocuments").GetInt32()).IsEqualTo(3);
        var fields = root.GetProperty("fields").EnumerateArray().ToDictionary(
            field => field.GetProperty("path").GetString()!,
            field => (Present: field.GetProperty("present").GetInt32(), Types: field.GetProperty("types").EnumerateObject().ToDictionary(type => type.Name, type => type.Value.GetInt32())));
        await Assert.That(root.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("path").GetString()!).ToList())
            .IsEquivalentTo(["active", "address", "address.city", "address.zip", "age", "name", "score", "tags"]);
        await Assert.That(fields["active"].Types).IsEquivalentTo(new Dictionary<string, int> { ["boolean"] = 1 });
        await Assert.That(fields["address"].Present).IsEqualTo(2);
        await Assert.That(fields["address"].Types).IsEquivalentTo(new Dictionary<string, int> { ["object"] = 2 });
        await Assert.That(fields["address.city"].Present).IsEqualTo(2);
        await Assert.That(fields["address.zip"].Present).IsEqualTo(1);
        await Assert.That(fields["age"].Present).IsEqualTo(3);
        await Assert.That(fields["age"].Types).IsEquivalentTo(new Dictionary<string, int> { ["integer"] = 2, ["string"] = 1 });
        await Assert.That(fields["name"].Types).IsEquivalentTo(new Dictionary<string, int> { ["string"] = 2, ["null"] = 1 });
        await Assert.That(fields["score"].Types).IsEquivalentTo(new Dictionary<string, int> { ["number"] = 1 });
        await Assert.That(fields["tags"].Types).IsEquivalentTo(new Dictionary<string, int> { ["array"] = 1 });
    }

    // Sends the exact JSON text, so stored bytes are known; returns it. Creates the collection first.
    private static async Task<string> PutAsync(HttpClient data, ManagedDatabaseRef database, string collection, string name, object document)
    {
        await ManagementApi.CreateCollectionAsync(data, database, collection);
        var json = JsonSerializer.Serialize(document, JsonSerializerOptions.Web);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        (await data.PutAsync(Uri(ManagementApi.DocumentPath(database, collection, name)), content)).EnsureSuccessStatusCode();
        return json;
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
