using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class PlatformPlacementLockTests
{
    private const string UnavailableMessage = "databases serve only in their home region until replicas are available";

    private static readonly string[] RemoteRegions = ["sjc"];

    [Test]
    [Arguments(PlatformDatabases.AuthProjectId, PlatformDatabases.Auth)]
    [Arguments(PlatformDatabases.DashboardProjectId, PlatformDatabases.Dashboard)]
    public async Task Placement_changes_are_refused_and_platform_databases_and_branches_stay_local(string project, string name, CancellationToken cancellationToken)
    {
        var production = TerraBaseOptions.FromValues(new Dictionary<string, string>(), production: true);
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings[TerraBaseTestServer.PlatformSeedKey] = "true";
            settings["region"] = "gru";
            settings["auth-issuer"] = production.AuthIssuer ?? string.Empty;
            settings["auth-audience"] = string.Join(',', production.AuthAudiences);
        }, services => services.AddHttpClient("ts-auth").AddHttpMessageHandler(() => new PlatformDatabasesTests.GlobalIssuerRoute()));
        var server = api.Server;
        await TerraBaseTestServer.EventuallyAsync(async token => await RecordAsync(server, project, name, token) is not null);
        var before = (await RecordAsync(server, project, name, cancellationToken))!;
        using var projectClient = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Issuer = TerraBaseOptions.PlatformAuthIssuer;
            token.Audience = TerraBaseOptions.PlatformAuthAudience;
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
        }));
        var route = ManagementApi.Uri($"/v1/projects/{project}/databases/{name}");

        object[] refused =
        [
            new { allRegions = true },
            new { replicaRegions = RemoteRegions },
            new { allRegions = false, replicaRegions = new[] { "sjc", "fra" } },
            new { allRegions = true, replicaRegions = RemoteRegions },
        ];
        foreach (var body in refused)
        {
            await ErrorAssert.ErrorAsync(projectClient.PatchAsJsonAsync(route, body, cancellationToken), HttpStatusCode.BadRequest, "replicas_unavailable", UnavailableMessage);
            await Assert.That(await RecordAsync(server, project, name, cancellationToken)).IsEqualTo(before);
        }

        await ErrorAssert.ErrorAsync(
            projectClient.PatchAsJsonAsync(route, new { homeRegion = "sjc", replicaRegions = Array.Empty<string>() }, cancellationToken),
            HttpStatusCode.BadRequest, "invalid_request", "homeRegion cannot change");
        await Assert.That(await RecordAsync(server, project, name, cancellationToken)).IsEqualTo(before);
        await ErrorAssert.ErrorAsync(projectClient.PatchAsJsonAsync(route, new { allRegions = false }, cancellationToken),
            HttpStatusCode.BadRequest, "invalid_request", "replicaRegions or allRegions is required");
        await Assert.That(await RecordAsync(server, project, name, cancellationToken)).IsEqualTo(before);

        object[] unchanged =
        [
            new { replicaRegions = Array.Empty<string>() },
            new { homeRegion = "gru", allRegions = false, replicaRegions = Array.Empty<string>() },
        ];
        foreach (var body in unchanged)
        {
            using var same = await projectClient.PatchAsJsonAsync(route, body, cancellationToken);
            await Assert.That(same.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await same.Content.ReadAsStringAsync(cancellationToken));
            var placement = await same.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await Assert.That(placement.GetProperty("allRegions").GetBoolean()).IsFalse();
            await Assert.That(placement.GetProperty("homeRegion").GetString()).IsEqualTo("gru");
            await Assert.That(placement.GetProperty("replicaRegions").GetArrayLength()).IsEqualTo(0);
            await Assert.That(await RecordAsync(server, project, name, cancellationToken)).IsEqualTo(before);
        }

        using var branched = await projectClient.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{name}/branches"), new { name = name + "-branch" }, cancellationToken);
        await Assert.That(branched.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await branched.Content.ReadAsStringAsync(cancellationToken));
        var branch = await branched.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(branch.GetProperty("allRegions").GetBoolean()).IsFalse();
        await Assert.That(branch.GetProperty("homeRegion").GetString()).IsEqualTo("gru");
        await Assert.That(branch.GetProperty("replicaRegions").GetArrayLength()).IsEqualTo(0);
        var branchRecord = (await RecordAsync(server, project, name + "-branch", cancellationToken))!;
        await Assert.That(branchRecord.AllRegions).IsFalse();
        await Assert.That(branchRecord.HomeRegion).IsEqualTo("gru");
        await Assert.That(branchRecord.ReplicaCount).IsEqualTo(0);
        await ErrorAssert.ErrorAsync(
            projectClient.PatchAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{name}-branch"), new { replicaRegions = RemoteRegions }, cancellationToken),
            HttpStatusCode.BadRequest, "replicas_unavailable", UnavailableMessage);
        await Assert.That(await RecordAsync(server, project, name + "-branch", cancellationToken)).IsEqualTo(branchRecord);
        await Assert.That(await RecordAsync(server, project, name, cancellationToken)).IsEqualTo(before);
    }

    private sealed record Placement(string? HomeRegion, bool AllRegions, long Version, int ReplicaCount);

    private static async Task<Placement?> RecordAsync(TerraBaseTestServer server, string project, string name, CancellationToken cancellationToken) =>
        await server.Services.GetRequiredService<IManagementService>().FindDatabaseRecordAsync(PlatformDatabases.OrganizationId, project, name, cancellationToken) is ManagedDatabase record
            ? new Placement(record.HomeRegion, record.AllRegions, record.PlacementVersion, record.Replicas.Count)
            : null;
}
