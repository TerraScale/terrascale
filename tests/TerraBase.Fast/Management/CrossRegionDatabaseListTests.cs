using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class CrossRegionDatabaseListTests
{
    private const string Scopes = "databases:read databases:write";

    [Test]
    public async Task A_project_lists_the_databases_of_every_region_with_paging()
    {
        await using var away = await ManagementApi.StartAsync(settings => settings["region"] = "sjc");
        await using var home = await ManagementApi.StartAsync(settings => settings["region"] = "gru", services =>
        {
            var peers = away.Server.BaseAddress;
            services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new FixedRegionPeers(peers)));
        });

        var project = EntityIds.New().ToString();
        var local = home.WithToken(Token(home, project));
        var remote = away.WithToken(Token(away, project));
        var path = $"/v1/projects/{project}/databases";

        using (var created = await local.PostAsJsonAsync(ManagementApi.Uri(path), new { name = "athome" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        using (var created = await remote.PostAsJsonAsync(ManagementApi.Uri(path), new { name = "away" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        using var listed = await local.GetAsync(ManagementApi.Uri(path));
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await listed.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["athome", "away"]);

        using var paged = await local.GetAsync(ManagementApi.Uri(path + "?limit=1"));
        var first = await paged.Content.ReadFromJsonAsync<JsonElement>();
        var names = new List<string> { first.GetProperty("items")[0].GetProperty("name").GetString()! };
        var next = first.GetProperty("next").GetString();
        while (next is not null)
        {
            using var following = await local.GetAsync(ManagementApi.Uri(path + $"?limit=1&after={next}"));
            var continuation = await following.Content.ReadFromJsonAsync<JsonElement>();
            names.Add(continuation.GetProperty("items")[0].GetProperty("name").GetString()!);
            next = continuation.GetProperty("next").GetString() is { Length: > 0 } cursor ? cursor : null;
        }

        await Assert.That(names.Order(StringComparer.Ordinal)).IsEquivalentTo(["athome", "away"]);
        var merged = page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "away");
        await Assert.That(merged.GetProperty("region").GetString()).IsEqualTo("sjc");
        await Assert.That(merged.GetProperty("homeRegion").GetString()).IsEqualTo("sjc");
    }

    private static string Token(ManagementApi api, string project) =>
        api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = Scopes;
        });

    private sealed class FixedRegionPeers(Uri peer) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([new RegionPeer("sjc", peer)]);
    }
}
