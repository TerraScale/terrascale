using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class CatalogBootstrapTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_dependent_origin_carries_the_supported_catalog_version_to_a_fresh_receiver(bool delayFirstOrigin)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var first = await ManagementApi.StartAsync();
        var organization = await first.CreateOrganizationAsync("First origin");
        var original = await GroupsAsync(first, token);
        await using var middle = await ManagementApi.StartAsync(userId: first.User);
        _ = await middle.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(ChangeGroups.System,
            first.Server.Services.GetRequiredService<LocalReplica>().Id, original, default, token);
        var project = await middle.CreateProjectAsync(organization, "Dependent origin");
        var dependent = await GroupsAsync(middle, token);
        var creation = dependent.Single(group => group.Deltas.Any(delta => delta.Collection == "project_organizations" && delta.Id == project));
        await Assert.That(creation.Deltas[0].Collection).IsEqualTo("catalog");
        await Assert.That(creation.Deltas[0].Id).IsEqualTo("version");
        var originalVersion = original.SelectMany(group => group.Deltas).First(delta => delta.Collection == "catalog" && delta.Id == "version");
        await Assert.That(creation.Deltas[0].State).IsEqualTo(originalVersion.State);
        await Assert.That(creation.Deltas[0].Stamp.Value).IsLessThanOrEqualTo(creation.Deltas.Skip(1).Max(delta => delta.Stamp.Value));

        // Later peer versions cannot change the payload at an already-issued CDC cursor.
        await using var later = await ManagementApi.StartAsync(time: new FakeTimeProvider(TimeProvider.System.GetUtcNow() + TimeSpan.FromMinutes(10)), userId: first.User);
        _ = await later.CreateOrganizationAsync("Later origin");
        _ = await middle.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(ChangeGroups.System,
            later.Server.Services.GetRequiredService<LocalReplica>().Id, await GroupsAsync(later, token), default, token);
        var replayed = (await GroupsAsync(middle, token)).Single(group => group.ChangeId == creation.ChangeId);
        await Assert.That(ChangeGroups.Encode([replayed])).IsEqualTo(ChangeGroups.Encode([creation]));

        await using var last = await ManagementApi.StartAsync(userId: first.User);
        var sink = last.Server.Services.GetRequiredService<IChangeGroupSink>();
        if (!delayFirstOrigin)
        {
            _ = await sink.ApplyAsync(ChangeGroups.System, first.Server.Services.GetRequiredService<LocalReplica>().Id, original, default, token);
        }

        _ = await sink.ApplyAsync(ChangeGroups.System, middle.Server.Services.GetRequiredService<LocalReplica>().Id, dependent, default, token);
        using var accepted = await last.Client.PostAsJsonAsync(ManagementApi.Uri("/v1/management/organizations"), new { name = "Receiver origin" }, token);
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Created);
        if (delayFirstOrigin)
        {
            _ = await sink.ApplyAsync(ChangeGroups.System, first.Server.Services.GetRequiredService<LocalReplica>().Id, original, default, token);
        }

        using var originalOrganization = await last.Client.GetAsync(ManagementApi.Uri("/v1/management/organizations/" + organization), token);
        await Assert.That(originalOrganization.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<IReadOnlyList<OriginGroup>> GroupsAsync(ManagementApi api, CancellationToken token)
    {
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        return (await api.Server.Services.GetRequiredService<IChangeGroupSource>().ReadAsync(
            ChangeGroups.System, system.Incarnation, 0, 512, 1048576, token))!.Groups;
    }
}
