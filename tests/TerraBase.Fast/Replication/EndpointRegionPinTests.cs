using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;
using Cell = TerraBase.Fast.Replication.SqlWriteReplicationTests.Cell;

namespace TerraBase.Fast.Replication;

/// <summary>Authorized pin guidance over real PostgreSQL and Wire listeners in three regions.</summary>
public sealed class EndpointRegionPinTests
{
    private static readonly string[] Sjc = ["sjc"];
    private static readonly string[] Fra = ["fra"];
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Pins_refuse_wrong_regions_without_mutation_and_name_only_authorized_placement(bool allRegions)
    {
        await using var cell = await Cell.StartAsync();
        var org = cell.Database.OrganizationId;
        var project = cell.Database.ProjectId!;
        await cell.Gru.Api.SetPlanAsync(cell.User, org, "enterprise");
        using var created = await cell.Gru.Api.Client.PostAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{org}/projects/{project}/databases"),
            allRegions ? (object)new { name = "pinned", allRegions = true } : new { name = "pinned", replicaRegions = Sjc });
        created.EnsureSuccessStatusCode();
        var path = $"/v1/projects/{project}/databases/pinned";
        var regions = allRegions ? new[] { "fra", "gru", "sjc" } : ["gru", "sjc"];
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var records = await Task.WhenAll(cell.Machines.Select(async machine =>
                await machine.Api.Server.Services.GetRequiredService<IManagementService>().FindDatabaseRecordAsync(org, project, "pinned", token)));
            return records.All(record => record is ManagedDatabase database && database.AllRegions == allRegions);
        });

        await Task.WhenAll(cell.Machines.Select(async machine =>
        {
            var key = await machine.Api.Server.IssueNarrowedAsync(org, project, null, "documents:read", "documents:write");
            using var data = machine.Api.DataClient(key);
            foreach (var pin in new string?[] { null, "gru", "sjc", "fra", "iad" })
            {
                var accepted = regions.Contains(machine.Region) && (pin is null || pin == machine.Region);
                var target = pin is not null && regions.Contains(pin) ? pin : regions[0];
                await using var pg = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
                {
                    Host = "127.0.0.1",
                    Port = machine.Api.Server.PostgresPort,
                    Username = "wire",
                    Password = key,
                    Database = $"{project}/pinned" + (pin is null ? "" : "@" + pin),
                    SslMode = SslMode.Disable,
                    Pooling = false,
                }.ConnectionString);
                if (accepted)
                {
                    await pg.OpenAsync();
                }
                else
                {
                    var error = await Assert.ThrowsAsync<PostgresException>(() => pg.OpenAsync());
                    await Assert.That(error!.SqlState).IsEqualTo("08004");
                    await Assert.That(error.MessageText).IsEqualTo(RegionGuidance.Message(target, regions));
                }

                await using var wire = await WireClient.ConnectAsync(machine.Api.Server.WirePort);
                var hello = pin is null ? await wire.HelloAsync(key, project, "pinned") : await wire.HelloPinnedAsync(key, project, "pinned", pin);
                await Assert.That(hello.Status).IsEqualTo(accepted ? 200 : 421);
                if (accepted)
                {
                    var repeated = await wire.HelloPinnedAsync(key, project, "pinned", machine.Region);
                    await Assert.That((repeated.Status, repeated.Code)).IsEqualTo((400, "unknown_operation"));
                }

                if (!accepted)
                {
                    await Assert.That(hello.Code).IsEqualTo("wrong_region");
                    await Assert.That(hello.Message).IsEqualTo(RegionGuidance.Message(target, regions));
                }
            }

        }));

        if (!allRegions)
        {
            var machine = cell.Sjc;
            var key = await machine.Api.Server.IssueNarrowedAsync(org, project, null, "documents:read");
            await using var pg = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = machine.Api.Server.PostgresPort,
                Username = "wire",
                Password = key,
                Database = "pinned@sjc",
                SslMode = SslMode.Disable,
                Pooling = false,
            }.ConnectionString);
            await pg.OpenAsync();
            await using var wire = await WireClient.ConnectAsync(machine.Api.Server.WirePort);
            await Assert.That((await wire.HelloPinnedAsync(key, project, "pinned", "sjc")).Status).IsEqualTo(200);
            var database = await created.Content.ReadFromJsonAsync<JsonElement>();
            using var removed = await cell.Gru.Api.Client.PatchAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{org}/projects/{project}/databases/{database.GetProperty("id").GetString()}"), new { replicaRegions = Fra });
            removed.EnsureSuccessStatusCode();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await machine.Api.Server.Services.GetRequiredService<IManagementService>().FindDatabaseRecordAsync(org, project, "pinned", token) is ManagedDatabase record && !record.PlacedIn("sjc"));
            await using var command = new NpgsqlCommand("SELECT 1", pg);
            var error = await Assert.ThrowsAsync<PostgresException>(command.ExecuteScalarAsync);
            await Assert.That(error!.SqlState).IsEqualTo("08004");
            await Assert.That(error.MessageText).StartsWith("wrong_region:");
            var answer = await wire.GetAsync("items", EntityIds.New().ToString());
            await Assert.That((answer.Status, answer.Code)).IsEqualTo((421, "wrong_region"));
        }
    }
}
