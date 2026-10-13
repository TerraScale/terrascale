using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace TerraBase.E2E.PitrLab;

/// <summary>A listed object: its key, stored bytes and ETag (an opaque version marker, not a content hash here).</summary>
internal readonly record struct ListedObject(string Key, long Size, string? ETag);

/// <summary>Read-only access to a bucket's objects, in S3 key order.</summary>
internal interface IPitrSource
{
    IAsyncEnumerable<ListedObject> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Streams <paramref name="key"/> to <paramref name="read"/>; <see langword="false"/> when it does not exist.</summary>
    Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken);
}

internal static class PitrSources
{
    /// <summary>The source a job names. An <c>s3</c> source reads its bucket and credentials from the environment only.</summary>
    public static IPitrSource Open(PitrSource? source) => source?.Kind switch
    {
        "s3" => S3Source.FromEnvironment(),
        "dir" => DiskBucket.Open(source.Root ?? throw new InvalidDataException("source.root is required for a dir source"), create: false),
        _ => throw new InvalidDataException("source.kind must be s3 or dir"),
    };

    public static async Task<ObjectFingerprint> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(file.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken)));
    }
}

/// <summary>
/// S3 orders keys by UTF-8 bytes; for valid Unicode the order of scalar values is the same, without encoding each key.
/// Same rule as the node's listing check (S3ObjectStore.CompareKeys, private there).
/// </summary>
internal sealed class S3KeyOrder : IComparer<string>
{
    public static readonly S3KeyOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        var left = (x ?? "").EnumerateRunes();
        var right = (y ?? "").EnumerateRunes();
        while (left.MoveNext())
        {
            if (!right.MoveNext())
            {
                return 1;
            }

            var comparison = left.Current.Value.CompareTo(right.Current.Value);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return right.MoveNext() ? -1 : 0;
    }
}

/// <summary>
/// A bucket kept as files: <c>&lt;root&gt;/objects/</c> holds one file per object and <c>&lt;root&gt;/tmp/</c> the uploads
/// in flight. Each key segment between slashes is a directory, percent-encoded (bytes outside <c>A-Za-z0-9-_.</c>, a
/// leading dot, and an empty segment as a lone <c>%</c>); the last one is the file, suffixed <c>@</c> (always encoded in
/// a segment), so a key never collides with the directory of a longer key. Writes land in <c>tmp/</c>, are flushed to
/// disk and renamed into place, so a reader or a crash sees the old or the new object, never part of one. Nothing is
/// ever deleted except by <see cref="Delete"/>; leftover temp files of a crash stay in <c>tmp/</c>. An object s3-serve
/// stored has a sidecar at the same relative path under <c>meta/</c> (<see cref="ObjectMeta"/>: its S3 ETag and user
/// metadata); an object without one, or whose file changed since (a mutation, a crash between the two renames), has a
/// synthetic ETag and no metadata.
/// </summary>
internal sealed class DiskBucket : IPitrSource
{
    // Sorts after every key of at most 1024 UTF-8 bytes (at most 256 maximal scalar values).
    private static readonly string AfterAll = string.Concat(Enumerable.Repeat("\U0010FFFF", 300));
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly IReadOnlyDictionary<string, string> NoMetadata = new Dictionary<string, string>();

    private readonly SortedSet<string> _keys = [with(S3KeyOrder.Instance)];
    private readonly Dictionary<string, Entry> _meta = [with(StringComparer.Ordinal)];
    private readonly Lock _gate = new();

    private DiskBucket(string root)
    {
        Root = root;
        Objects = Path.Combine(root, "objects");
        Scratch = Path.Combine(root, "tmp");
        Meta = Path.Combine(root, "meta");
    }

    public string Root { get; }

    public string Objects { get; }

    public string Scratch { get; }

    public string Meta { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _keys.Count;
            }
        }
    }

    /// <summary>Opens <paramref name="root"/>, indexing every object file; refuses a file this layout did not write.</summary>
    public static DiskBucket Open(string root, bool create)
    {
        var bucket = new DiskBucket(Path.GetFullPath(root));
        if (!create && !Directory.Exists(bucket.Objects))
        {
            throw new DirectoryNotFoundException("the root has no objects directory");
        }

        Directory.CreateDirectory(bucket.Objects);
        Directory.CreateDirectory(bucket.Scratch);
        foreach (var path in Directory.EnumerateFiles(bucket.Objects, "*", SearchOption.AllDirectories))
        {
            var key = Decode(Path.GetRelativePath(bucket.Objects, path));
            if (key is null || bucket.PathOf(key) != path)
            {
                throw new InvalidDataException($"{Path.GetRelativePath(bucket.Root, path)} is not an object file of this layout");
            }

            var info = new FileInfo(path);
            bucket._keys.Add(key);
            bucket._meta[key] = bucket.Load(path, info.Length, info.LastWriteTimeUtc);
        }

        return bucket;
    }

    // The object's sidecar while it still describes the file; otherwise a synthetic ETag and no metadata.
    private Entry Load(string path, long size, DateTime modified)
    {
        var sidecar = SidecarOf(path);
        try
        {
            if (File.Exists(sidecar) && JsonSerializer.Deserialize(File.ReadAllBytes(sidecar), PitrJson.Default.ObjectMeta) is { } meta
                && meta.Size == size && meta.ModifiedTicks == modified.Ticks)
            {
                return new(size, modified, meta.ETag, meta.Metadata);
            }
        }
        catch (JsonException)
        {
            // A torn or foreign sidecar describes nothing.
        }

        return new(size, modified, ETagOf(size, modified), NoMetadata);
    }

    private string SidecarOf(string path) => Path.Combine(Meta, Path.GetRelativePath(Objects, path));

    /// <summary>The file of <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException">Not a valid S3 key, or a segment too long for a file name.</exception>
    public string PathOf(string key)
    {
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(key);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("an object key is not valid UTF-8", nameof(key));
        }

        if (bytes is 0 or > 1024)
        {
            throw new ArgumentException("an object key is 1 to 1024 UTF-8 bytes", nameof(key));
        }

        var segments = key.Split('/');
        var names = segments.Select((segment, index) => Encode(segment) + (index == segments.Length - 1 ? "@" : "")).ToArray();
        // shortcut: a key segment over ~80 bytes of non-ASCII exceeds a 255-byte file name; TerraBase's keys are short.
        if (names.Any(name => name.Length > 255))
        {
            throw new ArgumentException("an object key segment is too long for the on-disk layout", nameof(key));
        }

        return Path.Combine([Objects, .. names]);
    }

    public string ETag(string key)
    {
        lock (_gate)
        {
            return _meta.TryGetValue(key, out var entry) ? entry.ETag : "";
        }
    }

    /// <summary>The ETag and user metadata of <paramref name="key"/> as opened (its size and modification time).</summary>
    public (string ETag, IReadOnlyDictionary<string, string> Metadata) Describe(string key, long size, DateTime modified)
    {
        lock (_gate)
        {
            return _meta.TryGetValue(key, out var entry) && entry.Size == size && entry.Modified == modified
                ? (entry.ETag, entry.Metadata)
                : (ETagOf(size, modified), NoMetadata);
        }
    }

    // An object without a sidecar: not an MD5 (it has a dash, like a multipart ETag), so no S3 client checks a download against it.
    public static string ETagOf(long size, DateTime modified) => string.Create(CultureInfo.InvariantCulture, $"\"{size:x}-{modified.Ticks:x}\"");

    /// <summary>Up to <paramref name="max"/> objects under <paramref name="prefix"/> after <paramref name="after"/>, in S3 order.</summary>
    public List<(string Key, long Size, DateTime Modified, string ETag)> Page(string prefix, string? after, int max)
    {
        var lower = after is not null && S3KeyOrder.Instance.Compare(after, prefix) > 0 ? after : prefix;
        var page = new List<(string, long, DateTime, string)>();
        if (max <= 0 || S3KeyOrder.Instance.Compare(lower, AfterAll) >= 0)
        {
            return page;
        }

        lock (_gate)
        {
            foreach (var key in _keys.GetViewBetween(lower, AfterAll))
            {
                if (key == after)
                {
                    continue;
                }

                // Keys with the prefix are contiguous in this order: the first one without it ends the range.
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || page.Count == max)
                {
                    break;
                }

                var entry = _meta[key];
                page.Add((key, entry.Size, entry.Modified, entry.ETag));
            }
        }

        return page;
    }

    public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = Page(prefix, after, 1000);
            foreach (var (key, size, _, etag) in page)
            {
                yield return new(key, size, etag);
            }

            if (page.Count < 1000)
            {
                yield break;
            }

            after = page[^1].Key;
            await Task.Yield();
        }
    }

    public async Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken)
    {
        FileStream file;
        try
        {
            file = new FileStream(PathOf(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        await using (file)
        {
            await read(file, cancellationToken);
            return true;
        }
    }

    /// <summary>A new temp file path in this root's scratch directory (same file system as the objects).</summary>
    public string NewTemp() => Path.Combine(Scratch, Ulid.NewUlid().ToString());

    /// <summary>
    /// Streams <paramref name="content"/> to a temp file, flushed to disk, then renames it into place with S3's single-PUT
    /// ETag (its MD5) and <paramref name="metadata"/>, unless its SHA-256 differs from <paramref name="expectedSha256"/>
    /// (then nothing is stored and the result is null).
    /// </summary>
    public async Task<ObjectFingerprint?> PutAsync(
        string key, Stream content, string? expectedSha256, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? metadata = null)
    {
        _ = PathOf(key);
        if (await WriteTempAsync(content, expectedSha256, cancellationToken) is not { } written)
        {
            return null;
        }

        try
        {
            Commit(written.Temp, key, $"\"{Convert.ToHexStringLower(written.Md5)}\"", metadata);
        }
        catch
        {
            File.Delete(written.Temp);
            throw;
        }

        return written.Fingerprint;
    }

    /// <summary>
    /// Streams <paramref name="content"/> to a new temp file, flushed to disk; null (and nothing kept) when its SHA-256
    /// differs from <paramref name="expectedSha256"/>. The caller moves or deletes the temp file. Md5 is for S3's ETags.
    /// </summary>
    public async Task<(string Temp, ObjectFingerprint Fingerprint, byte[] Md5)?> WriteTempAsync(Stream content, string? expectedSha256, CancellationToken cancellationToken)
    {
        var temp = NewTemp();
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long size = 0;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    md5.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    size += read;
                }

                file.Flush(flushToDisk: true);
            }

            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (expectedSha256 is not null && !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                return null;
            }

            return (temp, new(size, sha256), md5.GetHashAndReset());
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// Moves the finished temp file <paramref name="temp"/> into place as <paramref name="key"/>, replacing any object there
    /// and its sidecar; with <paramref name="etag"/> (an object s3-serve stores) a new sidecar records it and
    /// <paramref name="metadata"/>.
    /// </summary>
    public void Commit(string temp, string key, string? etag = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var path = PathOf(key);
        var sidecar = SidecarOf(path);
        var info = new FileInfo(temp);
        var (size, modified) = (info.Length, info.LastWriteTimeUtc); // a rename keeps both
        string? written = null;
        if (etag is not null)
        {
            written = NewTemp();
            using var file = new FileStream(written, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new ObjectMeta(etag, size, modified.Ticks, metadata ?? NoMetadata), PitrJson.Default.ObjectMeta);
            file.Flush(flushToDisk: true);
        }

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // The old sidecar describes the replaced file only (and stops matching it anyway): it goes first.
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }

            File.Move(temp, path, overwrite: true);
            if (written is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
                File.Move(written, sidecar, overwrite: true);
            }

            _keys.Add(key);
            _meta[key] = new(size, modified, etag ?? ETagOf(size, modified), metadata ?? NoMetadata);
        }
    }

    /// <summary>Deletes <paramref name="key"/>; deleting a missing object succeeds, as in S3.</summary>
    public void Delete(string key)
    {
        var path = PathOf(key);
        var sidecar = SidecarOf(path);
        lock (_gate)
        {
            File.Delete(path);
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }

            _keys.Remove(key);
            _meta.Remove(key);
        }
    }

    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _keys.Contains(key);
        }
    }

    private readonly record struct Entry(long Size, DateTime Modified, string ETag, IReadOnlyDictionary<string, string> Metadata);

    private static string Encode(string segment)
    {
        if (segment.Length == 0)
        {
            return "%";
        }

        var builder = new StringBuilder();
        var bytes = Encoding.UTF8.GetBytes(segment);
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            if (char.IsAsciiLetterOrDigit((char)value) || value is (byte)'-' or (byte)'_' || (value == (byte)'.' && i > 0))
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    // The key of an object file's path relative to objects/, or null when it is not one; PathOf(key) must give it back.
    private static string? Decode(string relative)
    {
        var names = relative.Split(Path.DirectorySeparatorChar);
        if (!names[^1].EndsWith('@'))
        {
            return null;
        }

        names[^1] = names[^1][..^1];
        var segments = new List<string>();
        foreach (var name in names)
        {
            if (name == "%")
            {
                segments.Add("");
                continue;
            }

            var bytes = new List<byte>();
            for (var i = 0; i < name.Length; i++)
            {
                if (name[i] != '%')
                {
                    bytes.Add((byte)name[i]);
                }
                else if (i + 2 < name.Length && byte.TryParse(name.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                {
                    bytes.Add(value);
                    i += 2;
                }
                else
                {
                    return null;
                }
            }

            try
            {
                segments.Add(StrictUtf8.GetString([.. bytes]));
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        return string.Join('/', segments);
    }
}

/// <summary>
/// A real S3-compatible bucket (Tigris), read only: it lists and gets, nothing else. Endpoint, region, bucket and
/// credentials come from the environment (the variables Fly sets for an attached Tigris bucket); errors name the
/// variable, never its value. Client settings mirror the node's (S3ObjectStore): path-style, checksums when required.
/// </summary>
internal sealed class S3Source : IPitrSource, IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;

    private S3Source(AmazonS3Client s3, string bucket)
    {
        _s3 = s3;
        _bucket = bucket;
    }

    public static S3Source FromEnvironment()
    {
        static string Setting(string name)
        {
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidDataException($"{name} is not set");
        }

        var endpoint = Setting("AWS_ENDPOINT_URL_S3");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("AWS_ENDPOINT_URL_S3 must be an https URL");
        }

        return new(new AmazonS3Client(new BasicAWSCredentials(Setting("AWS_ACCESS_KEY_ID"), Setting("AWS_SECRET_ACCESS_KEY")), new AmazonS3Config
        {
            ServiceURL = uri.ToString(),
            AuthenticationRegion = Setting("AWS_REGION"),
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        }), Setting("BUCKET_NAME"));
    }

    public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, MaxKeys = 1000 };
        string? previous = null;
        while (true)
        {
            var page = await _s3.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in page.S3Objects ?? [])
            {
                if (item.Key is not { } key || !key.StartsWith(prefix, StringComparison.Ordinal)
                    || (previous is not null && S3KeyOrder.Instance.Compare(key, previous) <= 0))
                {
                    throw new InvalidDataException("the bucket listing returned a key out of order or outside its prefix");
                }

                previous = key;
                yield return new(key, item.Size ?? 0, item.ETag);
            }

            if (page.IsTruncated != true)
            {
                yield break;
            }

            if (string.IsNullOrEmpty(page.NextContinuationToken) || page.NextContinuationToken == request.ContinuationToken)
            {
                throw new InvalidDataException("the bucket listing did not advance");
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
    }

    public async Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken)
    {
        GetObjectResponse response;
        try
        {
            response = await _s3.GetObjectAsync(_bucket, key, cancellationToken);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        using (response)
        {
            await read(response.ResponseStream, cancellationToken);
            return true;
        }
    }

    public void Dispose() => _s3.Dispose();
}
