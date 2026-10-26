using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

/// <summary>
/// The platform databases (ts-auth, ts-dashboard) stay in every region: a placement PATCH that turns allRegions off or names
/// replica regions answers 409 platform_placement_locked and changes nothing, and a branch keeps the all-regions placement.
/// </summary>
public sealed class PlatformPlacementLockTests
{
    private const string LockedMessage = "platform databases are placed in every region and their placement cannot change";

    [Test]
    [Arguments(PlatformDatabases.AuthProjectId, PlatformDatabases.Auth)]
    [Arguments(PlatformDatabases.DashboardProjectId, PlatformDatabases.Dashboard)]
    public async Task Placement_changes_are_refused_and_the_record_stays_in_every_region(string project, string name, CancellationToken cancellationToken)
    {
        var production = TerraBaseOptions.FromValues(new Dictionary<string, string>(), production: true);
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings[TerraBaseTestServer.PlatformSeedKey] = "true";
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
            new { allRegions = false },
            new { replicaRegions = Array.Empty<string>() },
            new { replicaRegions = new[] { "sjc" } },
            new { allRegions = false, replicaRegions = new[] { "sjc", "fra" } },
            new { allRegions = true, replicaRegions = new[] { "sjc" } },
        ];
        foreach (var body in refused)
        {
            await ErrorAssert.ErrorAsync(projectClient.PatchAsJsonAsync(route, body, cancellationToken), HttpStatusCode.Conflict, "platform_placement_locked", LockedMessage);
        }

        // The unchanged placement is accepted and writes nothing.
        using var same = await projectClient.PatchAsJsonAsync(route, new { allRegions = true }, cancellationToken);
        await Assert.That(same.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await same.Content.ReadAsStringAsync(cancellationToken));
        await Assert.That((await same.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("allRegions").GetBoolean()).IsTrue();
        await Assert.That(await RecordAsync(server, project, name, cancellationToken)).IsEqualTo(before);

        using var branched = await projectClient.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{name}/branches"), new { name = name + "-branch" }, cancellationToken);
        await Assert.That(branched.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await branched.Content.ReadAsStringAsync(cancellationToken));
        var branch = await branched.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(branch.GetProperty("allRegions").GetBoolean()).IsTrue();
        var branchRecord = (await RecordAsync(server, project, name + "-branch", cancellationToken))!;
        await Assert.That(branchRecord.AllRegions).IsTrue();
        await Assert.That(branchRecord.HomeRegion).IsNull();
        await Assert.That(branchRecord.ReplicaCount).IsEqualTo(0);
        await ErrorAssert.ErrorAsync(
            projectClient.PatchAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/{name}-branch"), new { replicaRegions = Array.Empty<string>() }, cancellationToken),
            HttpStatusCode.Conflict, "platform_placement_locked", LockedMessage);
    }

    private sealed record Placement(string? HomeRegion, bool AllRegions, long Version, int ReplicaCount);

    private static async Task<Placement?> RecordAsync(TerraBaseTestServer server, string project, string name, CancellationToken cancellationToken) =>
        await server.Services.GetRequiredService<IManagementService>().FindDatabaseRecordAsync(PlatformDatabases.OrganizationId, project, name, cancellationToken) is ManagedDatabase record
            ? new Placement(record.HomeRegion, record.AllRegions, record.PlacementVersion, record.Replicas.Count)
            : null;
}
