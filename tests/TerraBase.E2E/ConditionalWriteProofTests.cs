using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

public sealed class ConditionalWriteProofTests
{
    [Test]
    [Scenario(Nodes.Processes)]
    [Arguments("If-None-Match")]
    [Arguments("If-Match")]
    public async Task Concurrent_regional_conditional_writes_both_acknowledge_and_converge_to_one_winner(string condition)
    {
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc);
        var uri = regions.Document(EntityIds.New().ToString());
        var expected = "*";
        if (condition == "If-Match")
        {
            await Docs.PutAsync(regions[Cluster.Gru], uri, new JsonObject { ["writer"] = "seed" });
            using var original = await regions[Cluster.Gru].GetAsync(uri);
            expected = original.Headers.ETag!.ToString();
            foreach (var region in regions.Regions)
            {
                _ = await Converge.UntilAsync(async () =>
                {
                    using var read = await regions[region].GetAsync(uri);
                    return read.Headers.ETag?.ToString();
                }, tag => tag == expected, $"the original ETag on {region}");
            }
        }

        foreach (var region in regions.Regions)
        {
            regions.HoldReplication(region);
        }

        var writes = regions.Regions.Select(WriteAsync).ToArray();
        try
        {
            foreach (var region in regions.Regions)
            {
                _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[region], uri),
                    body => body?["writer"]?.GetValue<string>() == region,
                    $"the unreplicated local conditional write on {region}", TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            foreach (var region in regions.Regions)
            {
                regions.ReleaseReplication(region);
            }
        }

        var replies = await Task.WhenAll(writes);
        Console.WriteLine($"TS-757 {condition}: {string.Join(", ", replies.Select(reply => $"{reply.Region}={(int)reply.Status}/{reply.Durability}"))}");
        await Assert.That(replies.Select(reply => reply.Status)).IsEquivalentTo(
            Enumerable.Repeat(condition == "If-None-Match" ? HttpStatusCode.Created : HttpStatusCode.OK, 2));
        await Assert.That(replies.All(reply => reply.Durability == "remote_durable")).IsTrue();
        var copies = await Converge.UntilAsync(async () => await Task.WhenAll(regions.Regions.Select(async region =>
        {
            using var read = await regions[region].GetAsync(uri);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await read.Content.ReadFromJsonAsync<JsonElement>();
            return (Writer: body.GetProperty("document").GetProperty("writer").GetString(), ETag: read.Headers.ETag?.ToString());
        })), read => read.Distinct().Count() == 1, "both regional copies agree on the conditional-write winner");
        await Assert.That(copies[0].Writer is Cluster.Gru or Cluster.Sjc).IsTrue();
        await Assert.That(copies[0].ETag).IsNotNull();
        Console.WriteLine($"TS-757 {condition}: both copies converged to {copies[0].Writer}");

        async Task<(string Region, HttpStatusCode Status, string? Durability)> WriteAsync(string region)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = JsonContent.Create(new { writer = region }),
            };
            request.Headers.TryAddWithoutValidation(condition, expected);
            using var response = await regions[region].SendAsync(request);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (region, response.StatusCode, body.TryGetProperty("durability", out var durability) ? durability.GetString() : null);
        }
    }
}
