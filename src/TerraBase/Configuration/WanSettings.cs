namespace TerraBase.Configuration;

/// <summary>
/// Cross-region replication (<c>docs/cross-region.md</c>): <c>wan-regions</c> lists every region of the replication group
/// as <c>region=cell</c>, this machine's region among them, where the cell is the region's token on ts-orchestrator's
/// NATS fabric. Every member of every listed region pulls the others' change groups over NATS (<c>nats-url</c>).
/// </summary>
public sealed class WanSettings
{
    /// <summary>Every cross-region setting.</summary>
    internal static readonly string[] Keys = ["wan-regions"];

    /// <summary>Every region of the group, with its cell token.</summary>
    public required IReadOnlyDictionary<string, string> Cells { get; init; }

    /// <summary>The cell token of <paramref name="region"/>, this machine's region.</summary>
    public string Cell(string region) => Cells[region];

    /// <summary>The regions of the group other than <paramref name="region"/>, with their cell tokens.</summary>
    public IReadOnlyDictionary<string, string> Remotes(string region) => Cells.Where(pair => pair.Key != region).ToDictionary(StringComparer.Ordinal);

    /// <summary>Reads the settings, or <see langword="null"/> when <c>wan-regions</c> is not set.</summary>
    internal static WanSettings? Parse(IReadOnlyDictionary<string, string> values, NatsSettings? nats)
    {
        if (!values.TryGetValue("wan-regions", out var raw))
        {
            return null;
        }

        var cells = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || !TerraBaseOptions.IsRegion(entry[..separator]) || !IsToken(entry[(separator + 1)..])
                || cells.ContainsValue(entry[(separator + 1)..]) || !cells.TryAdd(entry[..separator], entry[(separator + 1)..]))
            {
                throw new TerraBaseConfigurationException("wan-regions must be comma-separated region=cell entries with distinct regions and cells");
            }
        }

        if (cells.Count < 2)
        {
            throw new TerraBaseConfigurationException("wan-regions must list at least two regions");
        }

        if (nats is null)
        {
            throw new TerraBaseConfigurationException("wan-regions requires nats-url: regions replicate over NATS");
        }

        return new() { Cells = cells };
    }

    /// <summary>A NATS subject token of ts-orchestrator's fabric: lowercase letters, digits, hyphens and underscores, at most 63.</summary>
    internal static bool IsToken(string token) =>
        token.Length is >= 1 and <= 63 && token.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');
}
