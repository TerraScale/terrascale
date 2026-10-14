using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Shapes;

public sealed class TerraBaseShapeContractTests
{
    [Test]
    public async Task OriginOperationsMatchThePinnedShapeRouteManifest()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Contract("shape-routes.json")));
        var operations = manifest.RootElement.GetProperty("operations").EnumerateArray().ToArray();
        var observed = new List<(string Method, string Path)>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            observed.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"code\":\"unauthorized\",\"message\":\"fixture\"}}")
            });
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseShapeClient(http, new TerraBaseShapeClientOptions
        {
            BaseAddress = new Uri("https://terrabase.example.test/"),
            ApiKey = "tb_fixture"
        });
        await Assert.That(async () =>
        {
            await using var reader = client.SubscribeAsync("project", "database", "notes").GetAsyncEnumerator();
            await reader.MoveNextAsync();
        }).Throws<TerraBaseAuthenticationException>();
        await Assert.That(async () =>
        {
            await client.GetEpochKeyAsync("project", "database", "notes", "h1-f3171a2230109328", 42,
                "01ARZ3NDEKTSV4RRFFQ69G5FAV");
        }).Throws<TerraBaseAuthenticationException>();

        await Assert.That(observed.Count).IsEqualTo(operations.Length);
        for (var index = 0; index < operations.Length; index++)
        {
            var route = operations[index];
            var expected = route.GetProperty("path").GetString()!
                .Replace("{project}", "project", StringComparison.Ordinal)
                .Replace("{database}", "database", StringComparison.Ordinal)
                .Replace("{collection}", "notes", StringComparison.Ordinal);
            await Assert.That(observed[index].Method).IsEqualTo(route.GetProperty("method").GetString());
            await Assert.That(observed[index].Path).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task ConformanceFixtureMatchesItsRecordedSourceHash()
    {
        using var provenance = JsonDocument.Parse(File.ReadAllBytes(Contract("shape-provenance.json")));
        var source = provenance.RootElement.GetProperty("sources").EnumerateArray()
            .Single(item => item.GetProperty("path").GetString() == "docs/terrabase/shape-vectors.json");
        var bytes = File.ReadAllBytes(Contract("fixtures/shape-vectors.json"));
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)))
            .IsEqualTo(source.GetProperty("sha256").GetString());
        await Assert.That(provenance.RootElement.GetProperty("commit").GetString())
            .IsEqualTo("993927ce7ecf8f9b889d24d4d1d21ec96eaf9416");
    }

    private static string Contract(string name) => Path.Combine(AppContext.BaseDirectory, "contracts", "terrabase", name);
}
