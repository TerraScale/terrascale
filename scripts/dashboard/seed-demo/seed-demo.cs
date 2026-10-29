// Seeds a project with four demo databases (shop, blog, iot, sql-demo) through the TerraScale .NET SDK, then sends
// some reads, queries and writes so analytics show activity. Re-running drops and rewrites every demo collection,
// so the data returns to the same known state (iot readings stay relative to now).
//
// Usage: dotnet run scripts/seed-demo/seed-demo.cs -- <projectId> <api-key-file | ->
// The API key is a Full access project key (API keys → Full access) read from a 0600 file, or from stdin with "-".
#:package TerraScale.DotNet
#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

if (args.Length != 2) { Console.Error.WriteLine("usage: seed-demo.cs <projectId> <api-key-file | ->"); return 2; }
var projectId = TerraScaleIds.Validate(args[0]);
var key = (args[1] == "-" ? Console.In.ReadLine() : ReadPrivate(args[1]))?.Trim() ?? "";
if (!key.Contains('_') || !TerraScaleIds.IsValid(key[..key.IndexOf('_')])) { Console.Error.WriteLine("The API key is not a TerraScale project key."); return 2; }

var api = new Uri("https://api.terrascale.tech/");
using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var oidc = new TerraScaleAuthOidcClient(new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions { BaseAddress = new Uri("https://auth.terrascale.tech/"), InstanceSlug = "terrascale" }));
string? token = null;
var expires = DateTimeOffset.MinValue;
async ValueTask<string> Token(CancellationToken ct)
{
    if (token is null || DateTimeOffset.UtcNow > expires)
    {
        var issued = await oidc.RequestClientCredentialsTokenAsync(key[..key.IndexOf('_')], key, null!, projectId, ct);
        (token, expires) = (issued.AccessToken, DateTimeOffset.UtcNow.AddSeconds(issued.ExpiresIn - 60));
    }
    return token;
}
var client = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = api, AccessTokenProvider = Token });
var project = client.Project(projectId);
using var health = JsonDocument.Parse(await http.GetStringAsync(new Uri(api, "/healthz")));
var region = health.RootElement.GetProperty("region").GetString()!;

var rng = new Random(20261022);
var ids = new Ids(rng);
var now = DateTimeOffset.UtcNow;
string Pick(string[] values) => values[rng.Next(values.Length)];
decimal Price(int min, int max) => Math.Round(min + (decimal)rng.NextDouble() * (max - min), 2);
string[] first = ["Ana", "Bruno", "Carla", "Diego", "Elena", "Felipe", "Gabriela", "Hugo", "Isabel", "João", "Karin", "Lucas", "Marina", "Nina", "Otávio", "Paula", "Rafael", "Sofia", "Tiago", "Vera"];
string[] last = ["Almeida", "Barbosa", "Costa", "Dias", "Ferreira", "Gomes", "Lima", "Martins", "Nunes", "Oliveira", "Pereira", "Ribeiro", "Santos", "Souza", "Teixeira"];
string[] cities = ["São Paulo", "Rio de Janeiro", "Belo Horizonte", "Curitiba", "Porto Alegre", "Recife", "Lisbon", "Austin", "Berlin", "Tokyo"];
string[] categories = ["coffee", "tea", "kitchen", "books", "audio", "outdoor"];
string[] adjectives = ["Classic", "Organic", "Compact", "Deluxe", "Everyday", "Travel", "Studio", "Smart"];
string[] statuses = ["pending", "paid", "shipped", "delivered", "cancelled"];

// shop: nested documents, orders reference customers and products by id.
var customers = Enumerable.Range(1, 500).Select(i => (Id: ids.Next(), Doc: new JsonObject
{
    ["name"] = $"{Pick(first)} {Pick(last)}", ["email"] = $"customer{i:D3}@example.com", ["tier"] = Pick(["free", "free", "plus", "pro"]),
    ["address"] = new JsonObject { ["city"] = Pick(cities), ["postalCode"] = $"{rng.Next(10000, 99999)}" },
    ["createdAt"] = now.AddDays(-rng.Next(30, 720)).ToString("O"),
})).ToArray();
var products = Enumerable.Range(1, 200).Select(i =>
{
    var category = Pick(categories);
    return (Id: ids.Next(), Price: Price(5, 400), Doc: new JsonObject
    {
        ["sku"] = $"SKU-{i:D4}", ["name"] = $"{Pick(adjectives)} {category} item {i}", ["category"] = category,
        ["stock"] = rng.Next(0, 500), ["tags"] = new JsonArray(Pick(adjectives).ToLowerInvariant(), category),
    });
}).ToArray();
foreach (var product in products) product.Doc["price"] = product.Price;
var orders = Enumerable.Range(1, 2000).Select(i =>
{
    var items = new JsonArray();
    decimal total = 0;
    foreach (var product in Enumerable.Range(0, rng.Next(1, 5)).Select(_ => products[rng.Next(products.Length)]).DistinctBy(p => p.Id))
    {
        var quantity = rng.Next(1, 4);
        total += product.Price * quantity;
        items.Add(new JsonObject { ["productId"] = product.Id, ["quantity"] = quantity, ["unitPrice"] = product.Price });
    }
    return (Id: ids.Next(), Doc: new JsonObject
    {
        ["customerId"] = customers[rng.Next(customers.Length)].Id, ["status"] = Pick(statuses), ["items"] = items, ["total"] = total,
        ["currency"] = "USD", ["placedAt"] = now.AddMinutes(-rng.Next(1, 60 * 24 * 90)).ToString("O"),
    });
}).ToArray();

// blog: authors, markdown posts, comments on posts.
var authors = Enumerable.Range(1, 12).Select(i => (Id: ids.Next(), Doc: new JsonObject
{
    ["name"] = $"{Pick(first)} {Pick(last)}", ["handle"] = $"author{i}", ["bio"] = "Writes about databases, distributed systems and coffee.",
})).ToArray();
string[] topics = ["Indexing JSON documents", "Choosing a home region", "Branching databases for previews", "Reading query plans", "Designing ULID keys", "Replication without tears", "Backups you can restore", "Schema-free, not schema-less"];
var posts = Enumerable.Range(1, 60).Select(i =>
{
    var title = $"{Pick(topics)} (part {i})";
    return (Id: ids.Next(), Doc: new JsonObject
    {
        ["authorId"] = authors[rng.Next(authors.Length)].Id, ["title"] = title, ["slug"] = $"post-{i:D3}", ["published"] = rng.Next(5) > 0,
        ["tags"] = new JsonArray(Pick(["sql", "json", "ops", "tutorial"]), Pick(["beginner", "advanced"])),
        ["body"] = $"# {title}\n\nThis post walks through **{title.ToLowerInvariant()}** step by step.\n\n## Why it matters\n\n- Faster reads\n- Fewer surprises in production\n\n```sql\nSELECT * FROM posts WHERE published = true LIMIT 10\n```\n\nThanks for reading!",
        ["publishedAt"] = now.AddDays(-rng.Next(1, 365)).ToString("O"),
    });
}).ToArray();
var comments = Enumerable.Range(1, 400).Select(i => (Id: ids.Next(), Doc: new JsonObject
{
    ["postId"] = posts[rng.Next(posts.Length)].Id, ["author"] = Pick(first), ["body"] = Pick(["Great write-up!", "This saved me an afternoon.", "Could you cover multi-region next?", "The SQL example helped.", "Bookmarked."]),
    ["likes"] = rng.Next(0, 40), ["createdAt"] = now.AddHours(-rng.Next(1, 24 * 300)).ToString("O"),
})).ToArray();

// iot: 50 devices, one reading per device per hour over the last 30 days (36,000 readings). A seed run writes
// about 41k operations, inside a hobby organization's 100,000 a month (TerraBase counts every organization as hobby today).
string[] kinds = ["thermostat", "air-quality", "freezer", "greenhouse", "server-room"];
var devices = Enumerable.Range(1, 50).Select(i => (Id: ids.Next(), Kind: kinds[i % kinds.Length], Doc: new JsonObject
{
    ["name"] = $"device-{i:D2}", ["kind"] = kinds[i % kinds.Length], ["site"] = Pick(cities), ["firmware"] = $"1.{rng.Next(0, 9)}.{rng.Next(0, 20)}",
})).ToArray();
foreach (var device in devices) device.Doc["kind"] = device.Kind;
var readings = new List<(string Id, JsonObject Doc)>(36_000);
for (var step = 0; step < 720; step++)
    foreach (var (deviceId, kind, _) in devices)
    {
        var at = now.AddHours(-720 + step);
        var daily = Math.Sin((at.Hour + at.Minute / 60.0) / 24 * 2 * Math.PI);
        var baseline = kind switch { "freezer" => -18.0, "server-room" => 21.0, "greenhouse" => 26.0, _ => 22.0 };
        readings.Add((ids.Next(), new JsonObject
        {
            ["deviceId"] = deviceId, ["kind"] = kind, ["at"] = at.ToString("O"),
            ["temperature"] = Math.Round(baseline + daily * 3 + rng.NextDouble() - 0.5, 2), ["humidity"] = Math.Round(45 + daily * 10 + rng.NextDouble() * 4, 1),
            ["battery"] = Math.Round(100 - step / 9.0 - rng.NextDouble(), 1), ["alert"] = rng.Next(400) == 0,
        }));
    }

// sql-demo: flat rows with readable business keys for the SQL editor (TerraBase SQL reads one collection per query).
var sqlCustomers = Enumerable.Range(1, 100).Select(i => (Id: ids.Next(), Doc: new JsonObject
{
    ["customer_no"] = $"C-{i:D4}", ["name"] = $"{Pick(first)} {Pick(last)}", ["country"] = Pick(["BR", "BR", "US", "PT", "DE", "JP"]), ["signup_year"] = rng.Next(2019, 2027),
})).ToArray();
var sqlOrders = new List<(string Id, JsonObject Doc)>();
var sqlItems = new List<(string Id, JsonObject Doc)>();
for (var i = 1; i <= 400; i++)
{
    var orderNo = $"O-{i:D5}";
    decimal total = 0;
    for (var line = 1; line <= rng.Next(1, 4); line++)
    {
        var (quantity, price) = (rng.Next(1, 6), Price(3, 120));
        total += quantity * price;
        sqlItems.Add((ids.Next(), new JsonObject { ["order_no"] = orderNo, ["line"] = line, ["sku"] = $"SKU-{rng.Next(1, 201):D4}", ["quantity"] = quantity, ["unit_price"] = price }));
    }
    sqlOrders.Add((ids.Next(), new JsonObject
    {
        ["order_no"] = orderNo, ["customer_no"] = $"C-{rng.Next(1, 101):D4}", ["status"] = Pick(statuses), ["total"] = total, ["ordered_on"] = now.AddDays(-rng.Next(0, 365)).ToString("yyyy-MM-dd"),
    }));
}

var plan = new (string Database, (string Collection, IReadOnlyList<(string Id, JsonObject Doc)> Rows, string[][] Indexes)[] Collections)[]
{
    ("shop", [("customers", customers.Select(c => (c.Id, c.Doc)).ToArray(), [["email"], ["tier"]]),
              ("products", products.Select(p => (p.Id, p.Doc)).ToArray(), [["category"], ["sku"]]),
              ("orders", orders, [["customerId"], ["status", "placedAt"]])]),
    ("blog", [("authors", authors, []), ("posts", posts, [["authorId"], ["slug"]]), ("comments", comments, [["postId"]])]),
    ("iot", [("devices", devices.Select(d => (d.Id, d.Doc)).ToArray(), []), ("readings", readings, [["deviceId", "at"], ["at"]])]),
    ("sql-demo", [("customers", sqlCustomers, [["customer_no"]]), ("orders", sqlOrders, [["customer_no"], ["status"]]), ("order_items", sqlItems, [["order_no"]])]),
};

var clock = Stopwatch.StartNew();
foreach (var (name, collections) in plan)
{
    // No replica list: TerraBase places the database in every other serving region (a database needs two).
    try { var created = await project.CreateDatabaseAsync(name, region, null!); Console.WriteLine($"{name}: created in {created.HomeRegion} + {string.Join(",", created.ReplicaRegions ?? [])}"); }
    catch (TerraBaseConflictException) { Console.WriteLine($"{name}: exists"); }
    var database = client.Database(projectId, name);
    foreach (var (collection, rows, indexes) in collections)
    {
        try { await database.DropCollectionAsync(collection); } catch (TerraBaseNotFoundException) { }
        await database.CreateCollectionAsync(collection);
        await Parallel.ForEachAsync(rows.Chunk(500), new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (chunk, ct) =>
            await Retry(() => database.TransactAsync(chunk.Select(row => TerraBaseTransactionOperation.Put(collection, row.Id, Json(row.Doc))).ToArray(), ct)));
        foreach (var fields in indexes)
            try { await database.CreateIndexAsync(collection, fields); } catch (TerraBaseConflictException) { }
        Console.WriteLine($"{name}.{collection}: {rows.Count} documents, {indexes.Length} indexes ({clock.Elapsed:mm\\:ss})");
    }
}

// The SQL editor's demo queries; saved queries live in the dashboard (SQL editor → Save).
string[] queries =
[
    "SELECT order_no, customer_no, status, total FROM orders WHERE status = 'shipped' ORDER BY total DESC LIMIT 20",
    "SELECT * FROM order_items WHERE order_no = 'O-00042'",
    "SELECT customer_no, name, country FROM customers WHERE country IN ('BR', 'PT') ORDER BY name LIMIT 50",
    "SELECT order_no, total, ordered_on FROM orders WHERE customer_no = 'C-0007' ORDER BY ordered_on DESC",
];
var sqlDemo = client.Database(projectId, "sql-demo");
foreach (var query in queries)
{
    var result = await sqlDemo.RunSqlAsync(query);
    if (result.Matched == 0) throw new InvalidOperationException($"Demo query matched nothing: {query}");
    Console.WriteLine($"sql-demo: {result.Matched} rows for {query}");
}

// Traffic: reads, SQL queries and writes across the databases. Two at a time stays inside the organization request budget.
var shop = client.Database(projectId, "shop");
var iot = client.Database(projectId, "iot");
var blog = client.Database(projectId, "blog");
await Parallel.ForEachAsync(Enumerable.Range(0, 600), new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (i, ct) =>
{
    var pick = new Random(i);
    await Retry(async () =>
    {
        switch (i % 6)
        {
            case 0: await shop.GetDocumentAsync("orders", orders[pick.Next(orders.Length)].Id, ct); break;
            case 1: await shop.RunSqlAsync($"SELECT * FROM orders WHERE status = '{statuses[pick.Next(statuses.Length)]}' LIMIT 25", ct); break;
            case 2: await iot.RunSqlAsync($"SELECT at, temperature FROM readings WHERE deviceId = '{devices[pick.Next(devices.Length)].Id}' ORDER BY at DESC LIMIT 100", ct); break;
            case 3: await blog.ListDocumentsAsync("posts", 20, cancellationToken: ct); break;
            case 4: await shop.PatchDocumentAsync("products", products[pick.Next(products.Length)].Id, Json(new JsonObject { ["stock"] = pick.Next(0, 500) }), TerraBasePrecondition.Exists, ct); break;
            default: await iot.GetDocumentAsync("readings", readings[pick.Next(readings.Count)].Id, ct); break;
        }
        return 0;
    });
});
Console.WriteLine($"traffic: 600 requests; done in {clock.Elapsed:mm\\:ss}");
return 0;

static JsonElement Json(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

static async Task<T> Retry<T>(Func<Task<T>> operation)
{
    for (var attempt = 1; ; attempt++)
        try { return await operation(); }
        catch (Exception error) when (attempt < 8 && error is TerraBaseRateLimitedException or TerraBaseUnavailableException or HttpRequestException)
        { await Task.Delay(TimeSpan.FromSeconds(attempt * 2)); }
}

static string ReadPrivate(string path)
{
    if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
        throw new InvalidOperationException($"{path} must not be readable by group or others (chmod 600).");
    return File.ReadAllText(path);
}

// Seeded ULIDs: the same run order gives the same ids, so links into the demo data survive a reseed.
sealed class Ids(Random rng)
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private long time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    public string Next()
    {
        var bytes = new byte[16];
        var ms = time++;
        for (var i = 5; i >= 0; i--, ms >>= 8) bytes[i] = (byte)ms;
        rng.NextBytes(bytes.AsSpan(6));
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[26];
        for (var i = 25; i >= 0; i--, value >>= 5) chars[i] = Alphabet[(int)(value & 31)];
        return TerraScaleIds.Validate(new string(chars));
    }
}
