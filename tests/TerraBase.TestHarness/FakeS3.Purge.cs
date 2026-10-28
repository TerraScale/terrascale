using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace TerraBase.Testing;

public sealed partial class FakeS3
{
    private readonly ConcurrentDictionary<(bool Customer, string Bucket, string Key, string Version), byte[]> _oldVersions = new();

    private readonly ConcurrentDictionary<string, byte> _purgeBuckets = new(StringComparer.Ordinal);

    public void SeedVersion(bool customer, string key, string version, byte[] content, string? bucket = null)
    {
        bucket ??= customer ? CustomerBucket : Bucket;
        _purgeBuckets.TryAdd(bucket, 0);
        _oldVersions[(customer, bucket, key, version)] = content;
    }

    /// <summary>A bucket lifecycle rule expiring one noncurrent version.</summary>
    public bool ExpireVersion(bool customer, string key, string version, string? bucket = null) =>
        _oldVersions.TryRemove((customer, bucket ?? (customer ? CustomerBucket : Bucket), key, version), out _);

    public void SeedPurgeUpload(bool customer, string key) => _uploads[Guid.NewGuid().ToString("N")] = new Upload(customer, key, Time.GetUtcNow(), []);

    private bool IsPurgeBucket(bool customer, string bucket) => customer && _purgeBuckets.ContainsKey(bucket);

    private HttpResponseMessage PurgeUploads(bool customer, string bucket, NameValueCollection query) =>
        PurgeListingOverride?.Invoke(customer, "uploads") ?? ListUploads(customer, bucket, query);

    public int RetainedVersions(bool customer, string prefix, string? bucket = null) => _oldVersions.Keys.Count(item => item.Customer == customer
        && (bucket is null || item.Bucket == bucket) && item.Key.StartsWith(prefix, StringComparison.Ordinal));

    public Func<bool, string, HttpResponseMessage?>? PurgeListingOverride { get; set; }

    public Func<bool, string, bool>? RejectPurgeVersions { get; set; }

    public Action<bool, string?, string?>? PurgeVersionListed { get; set; }

    private HttpResponseMessage PurgeVersions(bool customer, string bucket, NameValueCollection query)
    {
        if (PurgeListingOverride?.Invoke(customer, "versions") is { } response)
        {
            return response;
        }

        if (RejectPurgeVersions?.Invoke(customer, bucket) == true)
        {
            return S3Error(HttpStatusCode.Forbidden, "AccessDenied");
        }

        var keyMarker = query["key-marker"];
        var versionMarker = query["version-id-marker"];
        if (versionMarker == "null")
        {
            return S3Error(HttpStatusCode.BadRequest, "InvalidArgument"); // Tigris: "Version ID is invalid."
        }

        PurgeVersionListed?.Invoke(customer, keyMarker, versionMarker);
        var objects = customer ? _customerObjects : _objects;
        var prefix = query["prefix"] ?? "";
        var max = Math.Clamp(int.Parse(query["max-keys"] ?? "1000", CultureInfo.InvariantCulture), 1, 1000);
        var entries = _oldVersions.Keys.Where(item => item.Customer == customer && item.Bucket == bucket && item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => (item.Key, item.Version)).Concat(objects.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal)).Select(item => (Key: item, Version: "null")))
            .Where(item => keyMarker is null || string.CompareOrdinal(item.Key, keyMarker) > 0
                || item.Key == keyMarker && versionMarker is not null && string.CompareOrdinal(item.Version, versionMarker) > 0)
            .OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal).Take(max + 1).ToArray();
        return Xml(HttpStatusCode.OK, new XElement(S3 + "ListVersionsResult",
            new XElement(S3 + "Name", bucket), new XElement(S3 + "Prefix", prefix), new XElement(S3 + "MaxKeys", max),
            new XElement(S3 + "IsTruncated", entries.Length > max ? "true" : "false"),
            entries.Length > max ? new XElement(S3 + "NextKeyMarker", entries[max - 1].Key) : null,
            // Like Tigris, a page ending on an unversioned object ("null") names no version marker: the next resumes after its key.
            entries.Length > max && entries[max - 1].Version != "null" ? new XElement(S3 + "NextVersionIdMarker", entries[max - 1].Version) : null,
            entries.Take(max).Select(item => new XElement(S3 + "Version", new XElement(S3 + "Key", item.Key),
                new XElement(S3 + "VersionId", item.Version), new XElement(S3 + "IsLatest", item.Version == "null" ? "true" : "false"),
                new XElement(S3 + "LastModified", "2026-10-07T00:00:00.000Z"), new XElement(S3 + "Size", 1)))));
    }

    public Func<bool, string, CancellationToken, Task>? BeforePurgeDelete { get; set; }

    private async ValueTask<HttpResponseMessage?> DeletePurgeVersionAsync(HttpRequestMessage request, bool customer, string key, CancellationToken token)
    {
        var version = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["versionId"];
        if (request.Method != HttpMethod.Delete || version is null or "null")
        {
            return null;
        }

        if (BeforePurgeDelete is { } before)
        {
            await before(customer, key, token).ConfigureAwait(false);
        }

        if (Locked(key))
        {
            return S3Error(HttpStatusCode.Forbidden, "RetentionRuleViolation");
        }

        var bucket = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/').Split('/')[0];
        _oldVersions.TryRemove((customer, bucket, key, version), out _);
        return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) };
    }
}
