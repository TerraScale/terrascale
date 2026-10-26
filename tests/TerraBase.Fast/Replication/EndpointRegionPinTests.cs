using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;
using Cell = TerraBase.Fast.Replication.SqlWriteReplicationTests.Cell;

namespace TerraBase.Fast.Replication;

/// <summary>Authorized pin guidance over real HTTP, PostgreSQL and Wire listeners in three regions.</summary>
public sealed class EndpointRegionPinTests
{
    private static readonly string[] HttpEndpoints = ["documents", "sql", "hrana"];
    private static readonly string[] Sjc = ["sjc"];
    private static readonly string[] Fra = ["fra"];
    private static readonly bool[] ReplayStates = [false, true];
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
                var id = EntityIds.New().ToString();
                foreach (var (endpoint, replayed) in HttpEndpoints.SelectMany(endpoint => ReplayStates, (endpoint, replayed) => (endpoint, replayed)))
                {
                    using var request = new HttpRequestMessage(endpoint == "documents" ? accepted ? HttpMethod.Get : HttpMethod.Put : HttpMethod.Post,
                        ManagementApi.Uri(path + (endpoint == "documents" ? $"/collections/items/documents/by-key?id={id}" : endpoint == "sql" ? "/sql/execute" : "/libsql/v2/pipeline")))
                    {
                        Content = endpoint == "documents" ? accepted ? null : JsonContent.Create(new { value = "refused" })
                            : endpoint == "sql" ? JsonContent.Create(new { statements = new[] { new { sql = "SELECT 1" } } })
                            : JsonContent.Create(new { requests = new object[] { new { type = "execute", stmt = new { sql = "SELECT 1", want_rows = true } }, new { type = "close" } } }),
                    };
                    if (pin is not null)
                    {
                        request.Headers.Add("X-TerraScale-Region-Pin", pin);
                    }

                    if (replayed)
                    {
                        request.Headers.TryAddWithoutValidation("ts-replay-src", "region=fra"); // May suppress replay, never permit serving.
                    }

                    using var response = await data.SendAsync(request);
                    if (accepted)
                    {
                        if (endpoint == "documents")
                        {
                            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                        }
                        else
                        {
                            response.EnsureSuccessStatusCode();
                        }
                    }
                    else
                    {
                        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
                        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
                        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("wrong_region");
                        await Assert.That(error.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!)).IsEquivalentTo(regions);
                        await Assert.That(error.GetProperty("host").GetString()).IsEqualTo(RegionGuidance.Host(target));
                        await Assert.That(error.GetProperty("docs").GetString()).IsEqualTo(RegionGuidance.Docs);
                        var replay = replayed ? null : pin is null ? string.Join(',', regions) : regions.Contains(pin) ? pin : null;
                        await Assert.That(response.Headers.TryGetValues("ts-replay", out var values) ? values.Single() : null).IsEqualTo(replay is null ? null : "region=" + replay);
                    }
                }

                if (!accepted)
                {
                    using var absent = await (regions.Contains(machine.Region) ? data : cell.Gru.Data).GetAsync(ManagementApi.Uri(path + $"/collections/items/documents/by-key?id={id}"));
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

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

            // A valid key narrowed to another database cannot learn this database's placement.
            var narrowed = await machine.Api.Server.IssueNarrowedAsync(org, project, cell.Database.DatabaseId, "documents:read");
            using var restricted = machine.Api.DataClient(narrowed);
            using var denied = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(path + "/collections/items/documents/by-key?id=" + EntityIds.New()));
            denied.Headers.Add("X-TerraScale-Region-Pin", "iad");
            using var refusal = await restricted.SendAsync(denied);
            var text = await refusal.Content.ReadAsStringAsync();
            await Assert.That(refusal.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            await Assert.That(JsonElement.Parse(text).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("wrong_region");
            await Assert.That(text).DoesNotContain("\"regions\"");
            await Assert.That(refusal.Headers.Contains("X-TerraBase-Database-Regions") || refusal.Headers.Contains("X-TerraBase-Database-Home")).IsFalse();
            await Assert.That(JsonElement.Parse(text).GetProperty("error").GetProperty("host").GetString()).IsEqualTo(RegionGuidance.Host("iad"));
            using var unknown = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(path.Replace("pinned", "unknown", StringComparison.Ordinal) + "/collections/items/documents/by-key?id=" + EntityIds.New()));
            unknown.Headers.Add("X-TerraScale-Region-Pin", "iad");
            using var missing = await restricted.SendAsync(unknown);
            await Assert.That(await missing.Content.ReadAsStringAsync()).IsEqualTo(text);
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
