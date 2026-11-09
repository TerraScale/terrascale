using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Primitives;

namespace TerraBase.Fast.Management;

// A project enrolled by its first database create resolves in every region at once (TS-1394).
public sealed class CrossRegionProjectEnrollmentTests
{
    private const string Scopes = "databases:read databases:write";

    [Test]
    public async Task A_first_database_create_enrolls_the_project_in_every_region()
    {
        using var issuer = new TestIssuer();
        var mesh = new Mesh();
        await using var home = await StartAsync("gru", mesh, issuer);
        await using var away = await StartAsync("sjc", mesh, issuer);
        mesh.Peers = [new("gru", home.Server.BaseAddress), new("sjc", away.Server.BaseAddress)];

        var organization = EntityIds.New().ToString();
        var project = EntityIds.New().ToString();
        using (var published = await home.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/organizations/{organization}/plan"), new { plan = "pro" }))
        {
            await Assert.That(published.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await published.Content.ReadAsStringAsync());
        }

        var path = $"/v1/projects/{project}/databases";
        using (var created = await home.WithToken(Token(issuer, organization, project)).PostAsJsonAsync(ManagementApi.Uri(path), new { name = "main" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
        }

        using var located = await PollAsync(away.Server.Client, ManagementApi.Uri($"/v1/internal/home?project={project}"));
        await Assert.That(located.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await located.Content.ReadAsStringAsync());

        using var awayListed = await away.WithToken(Token(issuer, organization, project)).GetAsync(ManagementApi.Uri(path));
        await Assert.That(awayListed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await awayListed.Content.ReadAsStringAsync());
    }

    private static Task<ManagementApi> StartAsync(string region, Mesh mesh, TestIssuer issuer) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services => services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, mesh))),
        sharedIssuer: issuer);

    private static async Task<HttpResponseMessage> PollAsync(HttpClient client, Uri path)
    {
        HttpResponseMessage? held = null;
        for (var attempt = 0; ; attempt++)
        {
            var response = await client.GetAsync(path);
            if (response.StatusCode == HttpStatusCode.OK || attempt == 50)
            {
                held = response;
                break;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return held!;
    }

    private static string Token(TestIssuer issuer, string organization, string project)
    {
        var token = issuer.Mint(EntityIds.New().ToString(), descriptor =>
        {
            descriptor.Claims["project_id"] = project;
            descriptor.Claims["org_id"] = organization;
            descriptor.Claims["scope"] = Scopes;
        });
        issuer.GrantProject(token, organization, project);
        return token;
    }

    private sealed class Mesh
    {
        public RegionPeer[] Peers { get; set; } = [];
    }

    private sealed class RegionView(string region, Mesh mesh) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. mesh.Peers.Where(peer => peer.Region != region)]);
    }
}
