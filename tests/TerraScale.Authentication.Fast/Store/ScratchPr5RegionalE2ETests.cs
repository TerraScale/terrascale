using System.Net;
using System.Text.Json;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Store;

/// <summary>Tester C scratch (never committed): ts-dotnet PR #5's regional contract against the real TerraBase of this commit.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ScratchPr5RegionalE2ETests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Region_pin_and_typed_421_match_the_real_server()
    {
        var health = await fixture.Server.Http.GetFromJsonAsync<JsonElement>(new Uri(fixture.Server.ClientAddress, "healthz"));
        var region = health.GetProperty("region").GetString()!;
        var other = region == "zzz" ? "yyy" : "zzz";
        var tenant = await fixture.Server.CreateTenantAsync();
        TerraBaseDatabase Db(string pin) => new TerraBaseDocumentsClient(fixture.Server.Http,
            new TerraBaseDocumentsClientOptions { BaseAddress = fixture.Server.ClientAddress, ApiKey = tenant.ApiKey, Region = pin }).Database(tenant.Project.Id, "regional");

        await Db(region).CreateAsync();
        await Db(region).CreateCollectionAsync("items");
        var written = await Db(region).PutDocumentAsync("items", "a", JsonDocument.Parse("""{"k":1}""").RootElement.Clone());
        await Assert.That(written.Created).IsTrue();

        var error = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(
            () => Db(other).PutDocumentAsync("items", "b", JsonDocument.Parse("""{"k":2}""").RootElement.Clone()));
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(error.ErrorCode).IsEqualTo("misdirected_request");
        await Assert.That(error.Region).IsEqualTo(region);
        await Assert.That(error.HomeRegion).IsNull();
        await Assert.That(error.Regions).IsEmpty();
        await Assert.That(await Db(region).GetDocumentAsync("items", "b")).IsNull();

        // A non-421 error names the answering region too.
        var missing = await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => Db(region).GetCollectionAsync("nope"));
        await Assert.That(missing!.Region).IsEqualTo(region);

        Console.WriteLine($"SCRATCH region={region}");
    }

    [Test]
    public async Task Dump_served_client_routes()
    {
        using var response = await fixture.Server.Http.GetAsync(new Uri(fixture.Server.ClientAddress, "openapi/v1.json"));
        var text = await response.Content.ReadAsStringAsync();
        var lines = new List<string>();
        if (response.IsSuccessStatusCode)
        {
            foreach (var path in JsonDocument.Parse(text).RootElement.GetProperty("paths").EnumerateObject())
            {
                foreach (var op in path.Value.EnumerateObject())
                {
                    lines.Add($"{op.Name.ToUpperInvariant()} {path.Name}");
                }
            }
        }

        await File.WriteAllLinesAsync(Environment.GetEnvironmentVariable("SCRATCH_ROUTES_OUT")!, [$"status {(int)response.StatusCode}", .. lines.Order(StringComparer.Ordinal)]);
    }
}
