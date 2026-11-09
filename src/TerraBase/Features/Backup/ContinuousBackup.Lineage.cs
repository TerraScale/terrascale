using System.Text.Json;
using TerraBase.Engine;

namespace TerraBase.Features.Backup;

internal sealed partial class ContinuousBackup
{
    private async Task StoreGenerationProofAsync(ShippedFile file, Ulid generation, string snapshot, WalSnapshot pin, CancellationToken cancellationToken)
    {
        if (pin.Incarnation == Ulid.Empty || pin.Offset < 0)
        {
            return;
        }

        using var body = new MemoryStream();
        await using (var json = new Utf8JsonWriter(body))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 1);
            json.WriteString("generation", generation.ToString());
            json.WriteString("snapshot", snapshot);
            json.WriteString("incarnation", pin.Incarnation.ToString());
            json.WriteNumber("offset", pin.Offset);
            json.WriteEndObject();
        }

        body.Position = 0;
        await StoreAsync(file, BackupLayout.GenerationProof(file.Prefix, generation), body, "lineage", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RetainGenerationProofsAsync(IObjectStore store, string file, Ulid? activeGeneration, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        var prefix = file + "/generations/";
        HashSet<Ulid> candidates = [];
        await foreach (var item in store.ListAsync(prefix, null, cancellationToken).ConfigureAwait(false))
        {
            if (Ulid.TryParse(item.Key[prefix.Length..], out var generation) && generation != activeGeneration && generation.Time <= expires)
            {
                _ = candidates.Add(generation);
                if (candidates.Count == 10_000)
                {
                    break;
                }
            }
        }
        if (candidates.Count == 0)
        {
            return true;
        }

        await foreach (var item in store.ListAsync(BackupLayout.Snapshots(file), null, cancellationToken).ConfigureAwait(false))
        {
            if (BackupLayout.ParseSnapshot(item.Key) is { } snapshot)
            {
                _ = candidates.Remove(snapshot.Generation);
            }
            else
            {
                return false;
            }
        }
        var wal = file + "/wal/";
        await foreach (var item in store.ListAsync(wal, null, cancellationToken).ConfigureAwait(false))
        {
            var suffix = item.Key[wal.Length..].Split('/');
            if (suffix.Length == 2 && Ulid.TryParse(suffix[0], out var generation))
            {
                _ = candidates.Remove(generation);
            }
            else
            {
                return false;
            }
        }

        return await DeleteAsync(store, [.. candidates.Select(generation => BackupLayout.GenerationProof(file, generation))], cancellationToken).ConfigureAwait(false);
    }

}
