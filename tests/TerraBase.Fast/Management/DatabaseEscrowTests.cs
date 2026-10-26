using System.Net;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Management;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class DatabaseEscrowTests
{
    [Test]
    public async Task Non_genesis_regions_borrow_units_and_a_delete_frees_exactly_one_create()
    {
        await using var cell = await DatabaseEscrowCell.StartAsync();
        var ids = new List<string>();
        foreach (var region in new[] { 1, 2, 0, 1, 2 })
        {
            using var created = await cell.CreateAsync(region, "db" + ids.Count);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        await cell.CountEverywhereAsync(5);
        foreach (var region in Enumerable.Range(0, 3))
        {
            await ErrorAssert.ErrorAsync(cell.CreateAsync(region, "full"), HttpStatusCode.Conflict, "database_limit", DatabaseEscrowCell.Limit("starter", 5));
        }

        // sjc returns its spent unit; once replicated, fra can borrow it for exactly one create.
        await cell.DeleteAsync(1, ids[0]);
        await cell.CountEverywhereAsync(4);
        using var replacement = await cell.CreateAsync(2, "replacement");
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await replacement.Content.ReadAsStringAsync());
        await cell.CountEverywhereAsync(5);
        foreach (var region in Enumerable.Range(0, 3))
        {
            await ErrorAssert.ErrorAsync(cell.CreateAsync(region, "still-full"), HttpStatusCode.Conflict, "database_limit", DatabaseEscrowCell.Limit("starter", 5));
        }
    }

    // A lone region is the genesis region: it holds the whole allowance and grants it to another region exactly once.
    [Test]
    public async Task Repeated_transfers_replay_the_cumulative_grant_and_invalid_requests_are_refused()
    {
        await using var api = await ManagementApi.StartAsync(services: services => services.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance())));
        var organization = await api.CreateOrganizationAsync("Acme");
        await api.SetPlanAsync(api.User, organization, "starter");
        using var tls = new PeerTls();
        using var channel = tls.CreateChannel(api.Server.BaseAddress);
        var client = new DatabaseEscrow.DatabaseEscrowClient(channel);

        // A lost reply's retry (received still 0) answers the same total and grants nothing more.
        var request = new EscrowTransferRequest { Organization = organization, Region = "sjc", Received = 0, Want = 2 };
        await Assert.That((await client.TransferAsync(request)).Granted).IsEqualTo(2L);
        await Assert.That((await client.TransferAsync(request)).Granted).IsEqualTo(2L);

        // Confirmed, the next request draws the rest of the Starter allowance and no more.
        request.Received = 2;
        request.Want = 10;
        await Assert.That((await client.TransferAsync(request)).Granted).IsEqualTo(5L);
        request.Received = 5;
        await Assert.That((await client.TransferAsync(request)).Granted).IsEqualTo(5L);

        foreach (var invalid in new[]
        {
            new EscrowTransferRequest { Organization = "not-a-ulid", Region = "sjc", Want = 1 },
            new EscrowTransferRequest { Organization = organization, Region = "sjc", Want = 0 },
            new EscrowTransferRequest { Organization = organization, Region = "Not A Region", Want = 1 },
        })
        {
            var error = await Assert.ThrowsAsync<RpcException>(async () => _ = await client.TransferAsync(invalid));
            await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task A_region_without_units_refuses_creates_when_the_other_regions_are_unreachable()
    {
        await using var cell = await DatabaseEscrowCell.StartAsync();
        await cell.CountEverywhereAsync(0);
        await cell.StopAsync(0);
        await cell.StopAsync(2);
        // AllRegions remains valid after peer discovery expires, so this reaches the allowance check.
        using var refused = await cell.Regions[1].Client.PostAsJsonAsync(ManagementApi.Uri(cell.Databases),
            new { name = "partitioned", allRegions = true });
        await Assert.That((await cell.ListAsync(1)).Length).IsEqualTo(0);
        await ErrorAssert.ErrorAsync(refused, HttpStatusCode.Conflict, "database_limit",
            "the rest of the organization's database allowance is held in regions this region cannot reach now; retry shortly");
    }
}
