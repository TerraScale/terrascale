using System.Net;
using System.Text;

namespace TerraScale.DotNet.Tests.TerraBase;

using TerraScale.DotNet.TerraBase;

public sealed class TerraBaseRegionsTests
{
    [Test]
    public async Task Catalog_is_anonymous_exact_and_not_cached_or_used_for_routing()
    {
        var calls = 0;
        var bodies = new List<StringContent>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls++;
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsoluteUri != "https://api.terrascale.tech/v1/regions"
                || request.Content is not null || request.Headers.Authorization is not null || request.Headers.Contains(TerraBaseRegion.PinHeader)
                || calls <= 2 && request.Headers.Accept.Single().MediaType != "application/json")
                throw new InvalidOperationException("Unexpected catalog request.");
            var body = new StringContent(calls == 1 ? """{"regions":["fra","gru","new-region9","sjc"]}""" : """{"regions":["gru"]}""", Encoding.UTF8, "application/json");
            bodies.Add(body);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
            response.Headers.CacheControl = new() { Public = true, MaxAge = TimeSpan.FromSeconds(5) };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseRegionsClient(http);
        await Assert.That((await client.ListRegionsAsync()).Regions).IsEquivalentTo(["fra", "gru", "new-region9", "sjc"]);
        await Assert.That((await client.ListRegionsAsync()).Regions).IsEquivalentTo(["gru"]);
        await Assert.That(calls).IsEqualTo(2);
        foreach (var body in bodies) await Assert.ThrowsAsync<ObjectDisposedException>(() => body.ReadAsStringAsync());
        // The SDK did not dispose the caller's HttpClient.
        using var callerResponse = await http.GetAsync("https://api.terrascale.tech/v1/regions");
        await Assert.That(calls).IsEqualTo(3);
    }

}
