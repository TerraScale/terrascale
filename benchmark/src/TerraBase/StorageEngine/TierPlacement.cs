namespace TerraBase.StorageEngine;

/// <summary>The physical residence class of an immutable storage segment.</summary>
public enum StorageTier
{
    /// <summary>Low latency local storage.</summary>
    Hot,

    /// <summary>Local or attached storage optimized for capacity.</summary>
    Warm,

    /// <summary>Cold storage, usually backed by an object store.</summary>
    Cold,
}

/// <summary>The logical identity used by a tier placement policy.</summary>
public sealed record StorageSegmentDescriptor(string Id, StorageTier Tier);

/// <summary>A resolved segment location returned by a placement policy.</summary>
public sealed record StorageSegmentLocation(StorageTier Tier, string Path);

/// <summary>
/// Resolves an immutable segment to a physical tier. The engine does not
/// assume that the target is a local filesystem, allowing object-store and
/// remote placement implementations to be supplied by the host.
/// </summary>
public interface IStorageTierPlacement
{
    /// <summary>Resolves a segment identity to its current physical location.</summary>
    StorageSegmentLocation Resolve(StorageSegmentDescriptor segment);
}

/// <summary>A deterministic filesystem tier placement used by the local engine.</summary>
public sealed class LocalStorageTierPlacement : IStorageTierPlacement
{
    private readonly string root;

    /// <summary>Creates a placement rooted at one engine directory.</summary>
    public LocalStorageTierPlacement(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    /// <summary>The root directory containing the tier directories.</summary>
    public string Root => root;

    /// <inheritdoc />
    public StorageSegmentLocation Resolve(StorageSegmentDescriptor segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var tierDirectory = segment.Tier.ToString().ToLowerInvariant();
        var directory = Path.Combine(root, tierDirectory);
        Directory.CreateDirectory(directory);
        return new StorageSegmentLocation(segment.Tier, Path.Combine(directory, segment.Id + ".seg"));
    }
}
