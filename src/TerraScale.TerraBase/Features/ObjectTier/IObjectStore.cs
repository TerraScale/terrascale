namespace TerraBase.Features.ObjectTier;

/// <summary>An immutable object's identity and size.</summary>
/// <param name="Key">Object key within the configured bucket and prefix.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="Sha256">SHA-256 of the full content, verified on upload.</param>
public sealed record ObjectInfo(string Key, long Length, ReadOnlyMemory<byte> Sha256);

/// <summary>S3-compatible immutable object storage (manifests, archived segments, backups). Not implemented yet.</summary>
public interface IObjectStore
{
    /// <summary>Uploads an object that must not exist yet; fails instead of overwriting.</summary>
    ValueTask<ObjectInfo> PutIfAbsentAsync(string key, Stream content, long length, CancellationToken cancellationToken);

    /// <summary>Reads up to <c>destination.Length</c> bytes at <paramref name="offset"/>; returns the bytes read.</summary>
    ValueTask<int> ReadRangeAsync(string key, long offset, Memory<byte> destination, CancellationToken cancellationToken);

    /// <summary>Lists objects under a prefix in key order.</summary>
    IAsyncEnumerable<ObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Deletes an object; <see langword="false"/> when it did not exist.</summary>
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}
