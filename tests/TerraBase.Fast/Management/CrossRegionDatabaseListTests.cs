using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class CrossRegionDatabaseListTests
{
    [Test]
    public async Task A_project_lists_the_databases_of_every_region_with_paging()
    {
        await using var home = await ManagementApi.StartAsync(settings => settings["region"] = "gru");
        await using var away = await ManagementApi.StartAsync(services =>
        {
            var peers = home.Server.BaseAddress;
            services.Replace(ServiceDescriptor.Singleton<IRemoteRegions>(new FixedRemoteRegions(peers)));
        }, settings => settings["region"] = "sjc");

        var organization = await home.CreateOrganizationAsync("Cross region");
        var project = await home.CreateProjectAsync(organization, "Main");
        away.Issuer.RegisterProject(project, organization);

        using var local = await home.Client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "athome" });
        await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var remote = await away.Client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "away" });
        await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var awayId = (await remote.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using var listed = await home.Client.GetAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"));
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await listed.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()))
            .IsEquivalentTo(["athome", "away"]);

        using var paged = await home.Client.GetAsync(ManagementApi.Uri($"/v1/projects/{project}/databases?limit=1"));
        var first = await paged.Content.ReadFromJsonAsync<JsonElement>();
        var names = new List<string> { first.GetProperty("items")[0].GetProperty("name").GetString()! };
        var next = first.GetProperty("next").GetString();
        while (next is not null)
        {
            using var following = await home.Client.GetAsync(ManagementApi.Uri($"/v1/projects/{project}/databases?limit=1&after={next}"));
            var continuation = await following.Content.ReadFromJsonAsync<JsonElement>();
            names.Add(continuation.GetProperty("items")[0].GetProperty("name").GetString()!);
            next = continuation.GetProperty("next").GetString() is { Length: > 0 } cursor ? cursor : null;
        }

        await Assert.That(names.Order(StringComparer.Ordinal)).IsEquivalentTo(["athome", "away"]);
        await Assert.That(page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "away")
            .GetProperty("region").GetString()).IsEqualTo("sjc");
        await Assert.That(page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == awayId)
            .GetProperty("homeRegion").GetString()).IsEqualTo("sjc");
    }

    private sealed class FixedRemoteRegions(Uri peer) : IRemoteRegions
    {
        public ValueTask<IReadOnlyList<Uri>> InternalListenersAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<Uri>>([peer]);
    }
}
