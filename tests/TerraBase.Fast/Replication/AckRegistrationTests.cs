using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class AckRegistrationTests
{
    [Test]
    public async Task Registering_a_peer_preserves_an_ack_committed_after_the_initial_cursor_read(CancellationToken cancellationToken)
    {
        Action<string, TenantContext>? checkpoint = null;
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database)),
            cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items", cancellationToken);
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var source = api.Server.Services.GetRequiredService<IChangeGroupSource>();
        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
        var head = (await source.HeadAsync(tenant, store.Incarnation, cancellationToken))!.Value;
        await Assert.That(head).IsGreaterThan(0);
        var peer = ReplicaId.Of(99, 1, 0);
        Task<bool>? acknowledging = null;
        var armed = 1;
        checkpoint = (step, scope) =>
        {
            if (step == "reader.reset" && scope == tenant && Interlocked.Exchange(ref armed, 0) == 1)
            {
                // Queue the real ACK before cursor registration obtains the writer.
                acknowledging = source.AckAsync(tenant, store.Incarnation, peer, head, default, cancellationToken).AsTask();
            }
        };

        var first = await source.AckedAsync(tenant, peer, cancellationToken);
        await Assert.That(first!.Value.Acked).IsEqualTo(0);
        await Assert.That(acknowledging is not null).IsTrue();
        await Assert.That(await acknowledging!).IsTrue();
        await Assert.That((await source.AckedAsync(tenant, peer, cancellationToken))!.Value.Acked).IsEqualTo(head);
    }
}
