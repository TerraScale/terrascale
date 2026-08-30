using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleProjectsRegionContractTests
{
    [Test]
    public async Task CreateAndUpdate_PreserveCanonicalRegionCodesOnTheWire()
    {
        using var handler = new ProjectHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        _ = await client.Projects.CreateAsync(
            "organization-a",
            new TerraScaleCreateProjectRequest(
                "Primary",
                Regions: [TerraScaleRegions.Gru, TerraScaleRegions.Fra],
                BackupRegion: TerraScaleRegions.Gru));
        _ = await client.Projects.UpdateAsync(
            "project-a",
            new TerraScaleUpdateProjectRequest(
                Name: null,
                Regions: [TerraScaleRegions.Sfo],
                PlacementGeneration: 2,
                BackupRegion: TerraScaleRegions.Sfo));

        await Assert.That(handler.Bodies).Count().IsEqualTo(2);
        foreach (var body in handler.Bodies)
        {
            using var json = JsonDocument.Parse(body);
            var regions = json.RootElement.GetProperty("regions").EnumerateArray()
                .Select(static region => region.GetString()
                    ?? throw new InvalidDataException("A serialized project region is null."))
                .ToArray();
            await Assert.That(regions.All(static region =>
                    region is TerraScaleRegions.Gru or TerraScaleRegions.Sfo or TerraScaleRegions.Fra))
                .IsTrue();
            await Assert.That(regions.Contains(
                    json.RootElement.GetProperty("backupRegion").GetString(),
                    StringComparer.Ordinal))
                .IsTrue();
        }
    }

    [Test]
    public async Task CreateAndUpdate_RejectNonCanonicalRegionCodesBeforeSending()
    {
        string[] rejectedCodes = ["gru", " GRU", "GRU ", "SJC", "GR", "GRUU", "ĞRU"];
        foreach (var rejectedCode in rejectedCodes)
        {
            using var createHandler = new ProjectHandler();
            using var createHttpClient = new HttpClient(createHandler);
            var createClient = CreateClient(createHttpClient);
            _ = await Assert.ThrowsAsync<ArgumentException>(() =>
                createClient.Projects.CreateAsync(
                    "organization-a",
                    new TerraScaleCreateProjectRequest(
                        "Primary",
                        Regions: [rejectedCode],
                        BackupRegion: TerraScaleRegions.Gru)));
            await Assert.That(createHandler.Bodies).IsEmpty();

            using var updateHandler = new ProjectHandler();
            using var updateHttpClient = new HttpClient(updateHandler);
            var updateClient = CreateClient(updateHttpClient);
            _ = await Assert.ThrowsAsync<ArgumentException>(() =>
                updateClient.Projects.UpdateAsync(
                    "project-a",
                    new TerraScaleUpdateProjectRequest(
                        Name: null,
                        Regions: null,
                        PlacementGeneration: null,
                        BackupRegion: rejectedCode)));
            await Assert.That(updateHandler.Bodies).IsEmpty();
        }
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerToken = "caller-token"
        });

    private sealed class ProjectHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ProjectJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private const string ProjectJson =
        "{\"projectId\":\"project-a\",\"organizationId\":\"organization-a\",\"name\":\"Primary\",\"ownerUserIds\":[\"user-a\"],\"createdByUserId\":\"user-a\",\"ownershipAssignedAt\":\"2026-08-30T00:00:00Z\",\"defaultPermissionKeys\":[],\"regions\":[\"GRU\"],\"backupRegion\":\"GRU\",\"placementStatus\":\"Ready\",\"placementGeneration\":1,\"pk\":\"project-a\",\"sk\":\"project-a\",\"entityType\":\"Project\",\"createdAt\":\"2026-08-30T00:00:00Z\",\"version\":1}";
}
