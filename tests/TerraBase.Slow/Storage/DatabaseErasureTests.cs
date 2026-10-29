using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class DatabaseErasureTests
{
    [Test]
    public async Task An_unlinked_database_is_not_verified_erased_while_a_reader_is_open()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await tenant.ContextAsync(api.Server, "main", token);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenDatabaseAsync(database, token) ?? throw new InvalidOperationException("database is absent");
        var reader = await store.BeginTransactionAsync(write: false, token);
        try
        {
            await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
            await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsFalse();
        }
        finally
        {
            await reader.DisposeAsync();
        }

        _ = await engine.DeleteDatabaseAsync(database, token);
        await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsTrue();
    }

    [Test]
    public async Task An_unlinked_database_is_not_verified_erased_while_its_backup_reader_is_open()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await tenant.ContextAsync(api.Server, "main", token);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var tap = await engine.OpenTapAsync(database, token) ?? throw new InvalidOperationException("tap is absent");
        try
        {
            await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
            await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsFalse();
        }
        finally
        {
            await tap.DisposeAsync();
        }

        _ = await engine.DeleteDatabaseAsync(database, token);
        await Assert.That(await engine.VerifyDatabaseErasureAsync(database, token)).IsTrue();
    }
}
