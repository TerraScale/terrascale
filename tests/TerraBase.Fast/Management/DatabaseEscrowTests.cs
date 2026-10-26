using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Management;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class DatabaseEscrowTests
{
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
}
