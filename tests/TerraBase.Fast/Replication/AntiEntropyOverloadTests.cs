using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class AntiEntropyOverloadTests
{
    // Live (ts-vtest-tb, 10k shape readers): a round's catalog read met full storage admission, the StorageBusyException
    // faulted the anti-entropy worker, the host stopped and the process aborted with exit 134.
    [Test]
    public async Task A_round_that_meets_busy_storage_skips_the_database_instead_of_failing_the_worker()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        // The frozen clock runs no round on its own; the small queue is filled below.
        await using var api = await ManagementApi.StartAsync(settings => settings["storage-queue"] = "2", time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        _ = await api.CreateManagedDatabaseAsync("first");
        // A later catalog commit leaves any cached placement of the first database stale, so the round reads the catalog.
        _ = await api.CreateManagedDatabaseAsync("second");
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var antiEntropy = api.Server.Services.GetRequiredService<AntiEntropy>();

        var held = 0;
        try
        {
            while (true)
            {
                try
                {
                    engine.Io.Admit();
                    held++;
                }
                catch (StorageBusyException)
                {
                    break;
                }
            }

            await antiEntropy.RunRoundAsync(token);
        }
        finally
        {
            for (; held > 0; held--)
            {
                engine.Io.Release();
            }
        }

        await Assert.That(antiEntropy.ExecuteTask!.IsCompleted).IsFalse();
        await antiEntropy.RunRoundAsync(token);
    }
}
