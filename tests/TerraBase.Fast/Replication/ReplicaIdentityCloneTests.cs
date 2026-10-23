using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class ReplicaIdentityCloneTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task A_clean_stopped_data_root_cannot_reuse_another_owners_member(bool sameDirectory, bool sameOwner)
    {
        var root = Directory.CreateTempSubdirectory("terrabase-replica-clone-").FullName;
        var donorRoot = Directory.CreateDirectory(Path.Combine(root, "donor")).FullName;
        var cloneRoot = Directory.CreateDirectory(Path.Combine(root, "clone")).FullName;
        var donorName = Ulid.NewUlid().ToString().ToLowerInvariant();
        var cloneName = sameOwner ? donorName : Ulid.NewUlid().ToString().ToLowerInvariant();
        try
        {
            ReplicaId original;
            await using (var donor = await StartAsync(donorRoot, donorName))
            {
                using var health = await donor.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
                health.EnsureSuccessStatusCode();
                original = donor.Services.GetRequiredService<TursoStorageEngine>().ReplicaId;
            }

            if (sameDirectory)
            {
                await using var replacement = await StartAsync(donorRoot, cloneName);
                await Assert.That(replacement.Services.GetRequiredService<TursoStorageEngine>().ReplicaId.Member)
                    .IsNotEqualTo(original.Member).Because("a different machine must not inherit ownership at the same path");
                return;
            }

            // Copy after clean disposal, including the stopped identity and all durable SYSTEM state.
            foreach (var directory in Directory.EnumerateDirectories(donorRoot, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(cloneRoot, Path.GetRelativePath(donorRoot, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(donorRoot, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(cloneRoot, Path.GetRelativePath(donorRoot, file)));
            }

            await using var restarted = await StartAsync(donorRoot, donorName);
            await using var clone = await StartAsync(cloneRoot, cloneName);
            var donorId = restarted.Services.GetRequiredService<TursoStorageEngine>().ReplicaId;
            var cloneId = clone.Services.GetRequiredService<TursoStorageEngine>().ReplicaId;

            await Assert.That(donorId).IsEqualTo(original).Because("a clean restart on the donor keeps its identity");
            await Assert.That(cloneId.Member).IsNotEqualTo(original.Member)
                .Because("a different machine must not mint dots or retain certificates under the donor's member identity");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<TerraBaseTestServer> StartAsync(string directory, string name) =>
        TerraBaseTestServer.StartAsync(settings =>
        {
            settings["data-directory"] = directory;
            settings["node-name"] = name;
        }, services => services.AddSingleton<TimeProvider>(new FakeTimeProvider()));
}
