using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Configuration;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// The shape mirror (FR-22): closed chunks sealed by the real engine are uploaded encrypted to the platform bucket, and the
/// manifest route lists each uploaded object's presigned URL only after the upload succeeded. The server, store,
/// sealer, mirror and endpoint are production code; only the object store, the lineage, and the chunk and page lookups are replaced.
/// </summary>
public sealed class ShapeMirrorTests
{
    private static readonly string[] ExpectedMetadata = ["x-amz-meta-owner", "x-amz-meta-object-hash", "x-amz-meta-sha256", "x-amz-meta-epoch", "x-amz-meta-key-id", "x-amz-meta-sealed-at", "x-amz-meta-floor-offset"];

    private const string Collection = DocumentApi.Collection;
    private const int LargeBody = 600_000;

    [Test]
    public async Task A_duplicate_queue_puts_the_object_once_and_a_later_manifest_lists_it_for_direct_fetch()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        _ = await harness.Db.SealWithMirrorAsync(harness.Shape, start);
        var chunk = await harness.SealAsync(start);
        await harness.MirroredAsync(1);

        // Background mirroring leaves plaintext origin delivery available.
        using (var origin = await harness.GetShapeAsync(start))
        {
            await Assert.That(origin.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(origin.Headers.Location).IsNull();
            await Assert.That((await origin.Content.ReadAsByteArrayAsync()).SequenceEqual(chunk.Body)).IsTrue();
        }

        await Assert.That(harness.Mirror.Queue(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(start), Text(chunk.EndOffset),
            chunk.EndOffset, chunk.ValuesAt, chunk.Body, harness.Time.GetUtcNow())).IsTrue();
        await Assert.That(harness.Mirror.Queue(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(start), Text(chunk.EndOffset),
            chunk.EndOffset, chunk.ValuesAt, chunk.Body, harness.Time.GetUtcNow())).IsTrue()
            .Because("a duplicate of a pending or cached chunk is accepted without queuing a second upload");

        var manifest = await harness.ManifestAsync(start);
        var entry = manifest.Objects.Single();
        await Assert.That(harness.Puts.Values.Sum()).IsEqualTo(1).Because("the duplicate queue must not upload the object again");

        // The manifest is the one transport: it names the presigned URL, which the platform serves with the stored ciphertext.
        using var listed = await harness.GetManifestAsync(start);
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await CacheControlAssert.EqualAsync(listed.Headers.CacheControl?.ToString());
        using var wire = JsonDocument.Parse(await listed.Content.ReadAsByteArrayAsync());
        var url = wire.RootElement.GetProperty("objects")[0].GetProperty("url").GetString()
            ?? throw new InvalidOperationException("the manifest names no url");
        using var platform = harness.PlatformClient();
        using var fetched = await platform.GetAsync(url);
        await Assert.That(fetched.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("the presigned URL is valid SigV4 for the platform bucket");
        await Assert.That((await fetched.Content.ReadAsByteArrayAsync()).SequenceEqual(harness.S3.Objects[entry.Object])).IsTrue();
        await Assert.That(harness.Puts.Values.Sum()).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_upload_keeps_the_origin_serving_200()
    {
        await using var harness = await Harness.StartAsync();
        harness.S3.FailPut = key => key.StartsWith("v1/shapes/", StringComparison.Ordinal);
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);

        await Assert.That(harness.Mirror.Queue(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(start), Text(chunk.EndOffset),
            chunk.EndOffset, chunk.ValuesAt, chunk.Body, harness.Time.GetUtcNow())).IsTrue();
        await harness.AttemptAsync();

        // Nothing was stored, so the manifest lists no object and the origin keeps serving the chunk.
        var manifest = await harness.ManifestAsync(start);
        await Assert.That(manifest.Objects.Count).IsEqualTo(0);
        using var origin = await harness.GetShapeAsync(start);
        await Assert.That(origin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(origin.Headers.Location).IsNull();
        await Assert.That((await origin.Content.ReadAsByteArrayAsync()).SequenceEqual(chunk.Body)).IsTrue();
    }

    [Test]
    public async Task A_stalled_upload_is_tried_again_and_its_object_is_listed()
    {
        await using var harness = await Harness.StartAsync();
        // The first upload hangs until its attempt's deadline, as a bucket that never answered (#838); the next one lands.
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.S3.BeforePut = async (key, cancellationToken) =>
        {
            if (key.StartsWith("v1/shapes/", StringComparison.Ordinal) && stalled.TrySetResult())
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);

        await Assert.That(harness.Queue(chunk)).IsTrue();
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(30), harness.Cancellation);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        // The pause before the next attempt starts once the stalled one has unwound.
        await TerraBaseTestServer.EventuallyAsync(_ =>
        {
            harness.Time.Advance(ShapeMirror.RetryDelay);
            return Task.FromResult(harness.PendingMirrorCompletions > 0);
        });
        await harness.MirroredAsync(1);

        var entry = (await harness.ManifestAsync(start)).Objects.Single();
        await Assert.That(harness.Puts.Values.Sum()).IsEqualTo(2).Because("one stalled attempt, then the one that stored the object");
        await Assert.That(harness.S3.Objects.ContainsKey(entry.Object)).IsTrue();
    }

    [Test]
    public async Task The_manifest_chains_at_most_eight_objects_and_verifies_each_stored_object()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        for (var commit = 0; commit < 9; commit++)
        {
            _ = await harness.CommitLargeAsync();
        }

        var chunks = await harness.SealAllAsync(start);
        await Assert.That(chunks.Length).IsEqualTo(9).Because("each large commit closes its own 1 MiB chunk");
        foreach (var chunk in chunks)
        {
            await Assert.That(harness.Mirror.Queue(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(chunk.Offset), Text(chunk.EndOffset),
                chunk.EndOffset, chunk.ValuesAt, chunk.Body, harness.Time.GetUtcNow())).IsTrue();
        }

        // Nine objects are listed; the manifest shows the first eight of the chain.
        await harness.MirroredAsync(chunks.Length);
        var manifest = await harness.ManifestAsync(start);
        var objects = manifest.Objects;
        await Assert.That(manifest.Handle).IsEqualTo(harness.Shape.Handle);
        await Assert.That(objects.Count).IsEqualTo(8).Because("a manifest never lists more than eight objects");
        await Assert.That(objects[0].Offset).IsEqualTo(Text(start));
        for (var index = 1; index < objects.Count; index++)
        {
            await Assert.That(objects[index].Offset).IsEqualTo(objects[index - 1].NextOffset).Because("the objects form one contiguous chain");
        }

        foreach (var entry in objects)
        {
            var stored = harness.S3.Objects[entry.Object];
            await Assert.That(entry.Sha256).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(stored)))
                .Because("the digest names the stored, encrypted bytes");
            await Assert.That(entry.Size).IsEqualTo(stored.Length);
            await Assert.That(entry.Epoch.Length).IsGreaterThan(0);
            await Assert.That(EntityIds.IsValid(entry.KeyId)).IsTrue();
            await Assert.That(entry.ExpiresAt).IsGreaterThan(harness.Time.GetUtcNow());
            await Assert.That(entry.Binding.Length).IsGreaterThan(0);
        }

        await Assert.That(objects.Select(entry => entry.Binding).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(objects.Count)
            .Because("each object is bound to its own shape, handle and offset");
    }

    [Test]
    public async Task Trim_below_the_floor_deletes_the_remote_objects_and_refuses_their_keys()
    {
        await using var harness = await Harness.StartAsync(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var start = await harness.Db.HeadAsync();
        // Two chunks upload in the first epoch; after the clock moves a full epoch, the third upload is sealed under the next one.
        for (var commit = 0; commit < 3; commit++)
        {
            _ = await harness.CommitLargeAsync();
        }

        var chunks = await harness.SealAllAsync(start);
        await Assert.That(harness.Mirror.Queue(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(chunks[0].Offset), Text(chunks[0].EndOffset),
            chunks[0].EndOffset, chunks[0].ValuesAt, chunks[0].Body, harness.Time.GetUtcNow())).IsTrue();
        await Assert.That(harness.Mirror.Queue(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(chunks[1].Offset), Text(chunks[1].EndOffset),
            chunks[1].EndOffset, chunks[1].ValuesAt, chunks[1].Body, harness.Time.GetUtcNow())).IsTrue();
        await harness.MirroredAsync(2);

        harness.Time.Advance(TimeSpan.FromSeconds(ShapeCrypto.EpochSeconds));
        await Assert.That(harness.Mirror.Queue(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(chunks[2].Offset), Text(chunks[2].EndOffset),
            chunks[2].EndOffset, chunks[2].ValuesAt, chunks[2].Body, harness.Time.GetUtcNow())).IsTrue();
        await harness.MirroredAsync(1);

        var before = (await harness.ManifestAsync(start)).Objects;
        await Assert.That(before.Count).IsEqualTo(3);
        await Assert.That(before[0].Epoch).IsEqualTo(before[1].Epoch);
        await Assert.That(before[2].Epoch).IsNotEqualTo(before[0].Epoch).Because("the third object was encrypted after the clock moved an epoch");


        var deleted = await harness.Mirror.TrimAsync(harness.Tenant, harness.Shape.Handle, chunks[2].Offset, harness.Cancellation);
        await Assert.That(deleted).IsEqualTo(2);
        await Assert.That(harness.S3.Objects.ContainsKey(before[0].Object)).IsFalse();
        await Assert.That(harness.S3.Objects.ContainsKey(before[1].Object)).IsFalse();
        await Assert.That(harness.S3.Objects.ContainsKey(before[2].Object)).IsTrue();


        var retained = await harness.ManifestAsync(chunks[2].Offset);
        await Assert.That(retained.Objects.Select(entry => entry.Offset).ToArray()).IsEquivalentTo([before[2].Offset],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);

        using (var refused = await harness.GetKeyAsync(before[0].Epoch, before[0].KeyId))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because("the trimmed epoch's key is not released");
            await Assert.That(await MustRefetchCode(refused)).IsEqualTo("must_refetch");
        }

        using var released = await harness.GetKeyAsync(before[2].Epoch, before[2].KeyId);
        await Assert.That(released.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("the current epoch's key is released for the retained object");
    }

    [Test]
    public async Task On_an_R2_bucket_objects_are_linked_by_CDN_path_and_a_trimmed_object_is_purged_from_the_CDN()
    {
        // Disposed after the server, which stops sending purges first.
        using var cloudflare = new FakeCloudflare();
        await using var harness = await Harness.StartAsync(cloudflare: cloudflare);
        var start = await harness.Db.HeadAsync();
        for (var commit = 0; commit < 3; commit++)
        {
            _ = await harness.CommitLargeAsync();
        }

        var chunks = await harness.SealAllAsync(start);
        foreach (var chunk in chunks[..3])
        {
            await Assert.That(harness.Mirror.Queue(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(chunk.Offset), Text(chunk.EndOffset),
                chunk.EndOffset, chunk.ValuesAt, chunk.Body, harness.Time.GetUtcNow())).IsTrue();
        }

        await harness.MirroredAsync(3);
        var before = (await harness.ManifestAsync(start)).Objects;
        await Assert.That(before.Count).IsEqualTo(3);
        foreach (var entry in before)
        {
            await Assert.That(entry.Url).IsEqualTo($"https://{ShapeStorageSettings.CdnHost}/{entry.Object}")
                .Because("an R2 bucket's link is the object's stable path on the bucket's CDN host, with no signature");
        }

        var deleted = await harness.Mirror.TrimAsync(harness.Tenant, harness.Shape.Handle, chunks[2].Offset, harness.Cancellation);
        await Assert.That(deleted).IsEqualTo(2);
        await cloudflare.PurgedAsync(1, harness.Cancellation);
        await Assert.That(cloudflare.Purged.All(prefix => prefix == $"{ShapeStorageSettings.CdnHost}/v1/shapes/{harness.Shape.Handle}/")).IsTrue();
    }

    [Test]
    public async Task On_an_R2_bucket_Cloudflare_analytics_become_CDN_request_counters_window_after_window()
    {
        // Disposed after the server, which stops querying first.
        using var cloudflare = new FakeCloudflare { Analytics = [("hit", 990), ("miss", 10), ("dynamic", 3)] };
        await using var harness = await Harness.StartAsync(cloudflare: cloudflare);
        var server = harness.Server;

        harness.Time.Advance(ShapeCdnAnalytics.Step);
        await cloudflare.QueriedAsync(1, harness.Cancellation);
        await TerraBaseTestServer.EventuallyAsync(async _ =>
            await server.MetricAsync("terrabase_shape_cdn_requests_total", "cache_status=\"hit\"", harness.Cancellation) == 990);
        await Assert.That(await server.MetricAsync("terrabase_shape_cdn_requests_total", "cache_status=\"miss\"", harness.Cancellation)).IsEqualTo(10);
        await Assert.That(await server.MetricAsync("terrabase_shape_cdn_requests_total", "cache_status=\"other\"", harness.Cancellation)).IsEqualTo(3)
            .Because("any status Cloudflare neither served from cache nor read from the bucket is other, a bounded label");

        harness.Time.Advance(ShapeCdnAnalytics.Step);
        await cloudflare.QueriedAsync(2, harness.Cancellation);
        await TerraBaseTestServer.EventuallyAsync(async _ =>
            await server.MetricAsync("terrabase_shape_cdn_requests_total", "cache_status=\"hit\"", harness.Cancellation) == 1980);
        var windows = cloudflare.Windows.ToArray();
        await Assert.That(windows.All(window => window.Host == ShapeStorageSettings.CdnHost)).IsTrue();
        await Assert.That(windows[1].Start).IsEqualTo(windows[0].End).Because("windows are half-open and adjacent, so no request is counted twice");
        await Assert.That(DateTimeOffset.Parse(windows[1].End, CultureInfo.InvariantCulture)).IsLessThanOrEqualTo(harness.Time.GetUtcNow() - ShapeCdnAnalytics.Delay)
            .Because("a window is read only once Cloudflare has had time to ingest it");
    }

    [Test]
    public async Task The_manifest_route_answers_at_most_eight_private_objects_in_one_contiguous_chain()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        for (var commit = 0; commit < 9; commit++)
        {
            _ = await harness.CommitLargeAsync();
        }

        var chunks = await harness.SealAllAsync(start);
        foreach (var chunk in chunks)
        {
            await Assert.That(harness.Queue(chunk)).IsTrue();
        }

        await harness.MirroredAsync(chunks.Length);
        using var response = await harness.GetManifestAsync(start);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await CacheControlAssert.EqualAsync(response.Headers.CacheControl?.ToString());

        using var wire = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var objects = wire.RootElement.GetProperty("objects");
        await Assert.That(objects.GetArrayLength()).IsEqualTo(8).Because("a manifest never lists more than eight objects");
        for (var index = 0; index < objects.GetArrayLength(); index++)
        {
            var entry = objects[index];
            if (index > 0)
            {
                await Assert.That(entry.GetProperty("offset").GetString()).IsEqualTo(objects[index - 1].GetProperty("next_offset").GetString())
                    .Because("the objects form one contiguous chain");
            }

            await Assert.That(entry.GetProperty("url").GetString()).IsNotNull();
            await Assert.That(entry.GetProperty("sha256").GetString()!.Length).IsEqualTo(64);
            await Assert.That(entry.GetProperty("size").GetInt64()).IsGreaterThan(0);
            await Assert.That(entry.GetProperty("key_id").GetString()).IsNotNull();
            await Assert.That(entry.GetProperty("expires_at").GetDateTimeOffset()).IsGreaterThan(harness.Time.GetUtcNow());
        }
    }

    [Test]
    [Arguments("2026-10-07T12:00:00Z")]
    [Arguments("2026-10-07T12:09:59.999Z")]
    public async Task A_presigned_url_serves_the_ciphertext_and_refuses_tampering_and_expiry(string startAt)
    {
        await using var harness = await Harness.StartAsync(new FakeTimeProvider(DateTimeOffset.Parse(startAt, CultureInfo.InvariantCulture)));
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);

        using var response = await harness.GetManifestAsync(start);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var wire = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var entry = wire.RootElement.GetProperty("objects")[0];
        var url = entry.GetProperty("url").GetString() ?? throw new InvalidOperationException("the manifest names no url");
        var key = entry.GetProperty("object").GetString() ?? throw new InvalidOperationException("the manifest names no object");
        var expiresAt = entry.GetProperty("expires_at").GetDateTimeOffset();
        var tenantIds = new[] { harness.Tenant.OrganizationId, harness.Tenant.ProjectId!, harness.Tenant.DatabaseId! };

        using var platform = harness.PlatformClient();
        using (var fetched = await platform.GetAsync(url))
        {
            await Assert.That(fetched.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("the presigned URL is valid SigV4 for the platform bucket");
            var bytes = await fetched.Content.ReadAsByteArrayAsync();
            await Assert.That(bytes.SequenceEqual(harness.S3.Objects[key])).IsTrue();
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(entry.GetProperty("sha256").GetString());
            await Assert.That((long)bytes.Length).IsEqualTo(entry.GetProperty("size").GetInt64());
            await CacheControlAssert.EqualAsync(fetched.Headers.CacheControl?.ToString(), "public, max-age=31536000, immutable");
            await Assert.That(fetched.Content.Headers.ContentType!.MediaType).IsEqualTo("application/octet-stream").Because("the platform serves ciphertext as opaque bytes");
            await Assert.That(fetched.Content.Headers.ContentEncoding).IsEmpty().Because("the ciphertext is not content-encoded");
            await Assert.That(fetched.Content.Headers.ContentDisposition).IsNull();
            var metadata = fetched.Headers.Where(header => header.Key.StartsWith("x-amz-meta-", StringComparison.Ordinal))
                .ToDictionary(header => header.Key, header => string.Join(",", header.Value));
            await Assert.That(metadata.Keys).IsEquivalentTo(ExpectedMetadata);
            await Assert.That(metadata.Values.Any(value => tenantIds.Any(id => value.Contains(id, StringComparison.Ordinal)))).IsFalse().Because("metadata carries hashes and numbers, never tenant identifiers");
            using var repeat = await platform.GetAsync(url);
            await Assert.That((await repeat.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes)).IsTrue().Because("the same signed URL serves the same ciphertext");
        }

        // The signed window ends no later than the manifest's expiry.
        var signedAt = DateTimeOffset.ParseExact(Query(url, "X-Amz-Date")!, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var lifetime = long.Parse(Query(url, "X-Amz-Expires")!, CultureInfo.InvariantCulture);
        await Assert.That(signedAt).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(harness.Time.GetUtcNow().ToUnixTimeSeconds()));
        await Assert.That(lifetime).IsGreaterThan(0).And.IsLessThanOrEqualTo(ShapeCrypto.EpochSeconds);
        await Assert.That(signedAt.AddSeconds(lifetime)).IsLessThanOrEqualTo(expiresAt);
        await Assert.That(tenantIds.Any(id => url.Contains(id, StringComparison.Ordinal))).IsFalse().Because("the presigned path is shape-scoped, not tenant-scoped");

        using (var signature = await platform.GetAsync(url.Replace("X-Amz-Signature=" + Query(url, "X-Amz-Signature"),
            "X-Amz-Signature=" + Flip(Query(url, "X-Amz-Signature")!), StringComparison.Ordinal)))
        {
            await Assert.That(signature.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because("a changed signature names no valid request");
        }

        using (var path = await platform.GetAsync(url.Replace(key, Flip(key), StringComparison.Ordinal)))
        {
            await Assert.That(path.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because("a changed object path is outside the signed request");
        }

        // Past the expiry the platform refuses the same URL, although it was valid when issued.
        harness.Time.Advance(expiresAt - harness.Time.GetUtcNow() + TimeSpan.FromSeconds(1));
        using var expired = await platform.GetAsync(url);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because("the signed window has ended");
    }

    [Test]
    public async Task A_restarted_mirror_adopts_the_stored_object_without_uploading_it_again()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);
        var before = (await harness.ManifestAsync(start)).Objects.Single();
        var cipher = harness.S3.Objects[before.Object].ToArray();

        // A second mirror over the same engine, storage and object store stands in for the process after a restart.
        using var restarted = harness.NewMirror(harness.Time);
        await restarted.StartAsync(harness.Cancellation);
        try
        {
            await Assert.That(harness.Queue(chunk, restarted)).IsTrue();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(harness.Cancellation, deadline.Token);
            ShapeMirrorManifest manifest;
            do
            {
                manifest = await restarted.ManifestAsync(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(start), 0, linked.Token);
                await Task.Yield();
            }
            while (manifest.Objects.Count == 0);

            var after = manifest.Objects.Single();
            await Assert.That(after.Object).IsEqualTo(before.Object);
            await Assert.That(after.Sha256).IsEqualTo(before.Sha256);
            await Assert.That(after.Epoch).IsEqualTo(before.Epoch).Because("the adopted object keeps the epoch it was encrypted under");
            await Assert.That(after.KeyId).IsEqualTo(before.KeyId);
            await Assert.That(harness.S3.Objects[before.Object].SequenceEqual(cipher)).IsTrue().Because("the stored ciphertext is untouched");
            await Assert.That(harness.Puts.Values.Sum()).IsEqualTo(1).Because("the restarted mirror adopts the object with a HEAD, not a second PUT");
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await restarted.StopAsync(deadline.Token);
        }

        await Assert.That(harness.PendingMirrorCompletions).IsEqualTo(0).Because("adoption is not a new upload lag sample");
    }

    [Test]
    public async Task A_floor_notification_defers_remote_cleanup_until_the_worker_runs()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);
        var stored = (await harness.ManifestAsync(start)).Objects.Single();
        var aged = new FakeTimeProvider(harness.Time.GetUtcNow());
        aged.Advance(FeedCursor.Retention + TimeSpan.FromSeconds(1));
        using var mirror = harness.NewMirror(aged);
        harness.S3.ObservedListPrefix = "v1/shapes/" + harness.Shape.Handle + "/";

        await mirror.FloorTrimmedAsync(harness.Db.Store, harness.Shape.Handle, 0, harness.Cancellation);
        await Assert.That(harness.S3.ListRequests).IsEqualTo(0).Because("the retention callback only queues work");
        await Assert.That(harness.S3.Objects.ContainsKey(stored.Object)).IsTrue();

        await mirror.StartAsync(harness.Cancellation);
        try
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!harness.S3.Objects.ContainsKey(stored.Object)),
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await mirror.StopAsync(deadline.Token);
        }
    }

    [Test]
    public async Task A_missing_database_does_not_turn_an_unknown_floor_into_remote_deletion()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);
        var stored = (await harness.ManifestAsync(start)).Objects.Single();

        await Assert.That(await harness.DropDatabaseAsync()).IsTrue();
        var deleted = await harness.Mirror.TrimAsync(harness.Tenant, harness.Shape.Handle, 0, harness.Cancellation);
        await Assert.That(deleted).IsEqualTo(0);
        await Assert.That(harness.S3.Objects.ContainsKey(stored.Object)).IsTrue();
        await Assert.That((await harness.ManifestAsync(start)).Objects.Count).IsEqualTo(0)
            .Because("a missing database cannot prove that its remote object is retained");
    }

    [Test]
    public async Task Objects_past_the_retention_window_are_trimmed_and_their_proofs_reject()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);
        var stored = (await harness.ManifestAsync(start)).Objects.Single();

        // The mirror runs on its own clock and is never started, so no expiry sweep can race this trim.
        var aged = new FakeTimeProvider(harness.Time.GetUtcNow());
        using var mirror = harness.NewMirror(aged);
        aged.Advance(FeedCursor.Retention + TimeSpan.FromSeconds(1));

        var deleted = await mirror.TrimAsync(harness.Tenant, harness.Shape.Handle, 0, harness.Cancellation);
        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(harness.S3.Objects.ContainsKey(stored.Object)).IsFalse();

        var manifest = await mirror.ManifestAsync(harness.Tenant, harness.Shape.ShapeId, harness.Shape.Handle, Text(start), 0, harness.Cancellation);
        await Assert.That(manifest.Objects.Count).IsEqualTo(0);
        await Assert.That(await mirror.HasObjectAsync(harness.Tenant, harness.Shape.Handle, long.Parse(stored.Epoch, CultureInfo.InvariantCulture),
            stored.KeyId, harness.Cancellation)).IsFalse().Because("a trimmed object's key pair is no longer proven retained");
    }

    [Test]
    public async Task A_sealed_snapshot_page_chain_is_mirrored_and_its_manifest_lists_each_page_for_direct_fetch()
    {
        await using var harness = await Harness.StartAsync();
        // Each 600 KiB document fills a snapshot page on its own, so the seal writes three pages.
        var written = await harness.WriteLargeDocumentsAsync(3);
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.OK);

        var snapshot = await harness.SealSnapshotAsync();
        var first = ShapeSnapshots.PageOffset(snapshot.Offset, null);

        // Each stored page is queued under its own offset; the chain follows the pages' continuation ids.
        var offset = first;
        string? after = null;
        var queued = 0;
        do
        {
            using var page = await harness.Snapshots.ReadPageAsync(harness.Db.Store, harness.Shape.Handle, snapshot.Offset, after, harness.Cancellation)
                ?? throw new InvalidOperationException($"snapshot {snapshot.Offset} has no stored page after {after ?? "the start"}");
            var body = page.Body.ToArray();
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            await Assert.That(root.GetProperty("offset").GetString()).IsEqualTo(offset);
            var next = root.GetProperty("next_offset").GetString()!;
            queued++;
            offset = next;
            after = page.NextId;
        }
        while (after is not null);
        await Assert.That(queued).IsEqualTo(3);

        await harness.MirroredAsync(queued);
        using var response = await harness.GetManifestAsync(first);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await CacheControlAssert.EqualAsync(response.Headers.CacheControl?.ToString());
        using var wire = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var objects = wire.RootElement.GetProperty("objects");
        await Assert.That(objects.GetArrayLength()).IsEqualTo(queued).Because("the manifest lists the whole chain, which is under eight pages");
        for (var index = 1; index < objects.GetArrayLength(); index++)
        {
            await Assert.That(objects[index].GetProperty("offset").GetString()).IsEqualTo(objects[index - 1].GetProperty("next_offset").GetString())
                .Because("the pages form one contiguous chain");
        }

        // Each listed URL serves the stored ciphertext, whose digest and size the manifest names.
        using var platform = harness.PlatformClient();
        for (var index = 0; index < objects.GetArrayLength(); index++)
        {
            var entry = objects[index];
            var url = entry.GetProperty("url").GetString() ?? throw new InvalidOperationException("the manifest names no url");
            var key = entry.GetProperty("object").GetString() ?? throw new InvalidOperationException("the manifest names no object");
            using var fetched = await platform.GetAsync(url);
            await Assert.That(fetched.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("the presigned URL is valid SigV4 for the platform bucket");
            var bytes = await fetched.Content.ReadAsByteArrayAsync();
            await Assert.That(bytes.SequenceEqual(harness.S3.Objects[key])).IsTrue();
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(entry.GetProperty("sha256").GetString());
            await Assert.That((long)bytes.Length).IsEqualTo(entry.GetProperty("size").GetInt64());
            await Assert.That(bytes.AsSpan(0, 4).SequenceEqual("TBS1"u8)).IsTrue().Because("every mirrored object is a TBS1 envelope");
        }
    }

    [Test]
    public async Task Repeated_origin_reads_back_off_head_requests_for_a_missing_mirror()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        _ = await harness.CommitLargeAsync();
        var chunks = await harness.SealAllAsync(start);
        var heads = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var previous = harness.S3.ObjectRequested;
        harness.S3.ObjectRequested = (customer, method, key) =>
        {
            previous?.Invoke(customer, method, key);
            if (!customer && method == HttpMethod.Head)
            {
                heads.AddOrUpdate(key, 1, (_, count) => count + 1);
            }
        };

        using (var first = await harness.GetShapeAsync(start))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // The next queue item settles after the earlier warm item's missing HEAD has been cached.
        await Assert.That(harness.Queue(chunks[1])).IsTrue();
        await harness.MirroredAsync(1);
        var missingKey = ShapeObjectStore.Key(harness.Shape.ShapeId, harness.Shape.Handle, Text(start));
        await Assert.That(heads[missingKey]).IsEqualTo(1);
        for (var request = 0; request < 2; request++)
        {
            using var origin = await harness.GetShapeAsync(start);
            await Assert.That(origin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(heads[missingKey]).IsEqualTo(1);
        await Assert.That(harness.S3.Objects.ContainsKey(missingKey)).IsFalse().Because("warming never uploads missing ciphertext");
    }

    [Test]
    public async Task A_cold_historical_key_proof_is_cached_without_repeating_remote_heads()
    {
        await using var harness = await Harness.StartAsync();
        var start = await harness.Db.HeadAsync();
        _ = await harness.CommitLargeAsync();
        var chunk = await harness.SealAsync(start);
        await Assert.That(harness.Queue(chunk)).IsTrue();
        await harness.MirroredAsync(1);
        var entry = (await harness.ManifestAsync(start)).Objects.Single();
        var heads = 0;
        var previous = harness.S3.ObjectRequested;
        harness.S3.ObjectRequested = (customer, method, key) =>
        {
            previous?.Invoke(customer, method, key);
            if (!customer && method == HttpMethod.Head && key == entry.Object)
            {
                Interlocked.Increment(ref heads);
            }
        };

        using var cold = harness.NewMirror(harness.Time);
        var epoch = long.Parse(entry.Epoch, CultureInfo.InvariantCulture);
        await Assert.That(await cold.HasObjectAsync(harness.Tenant, harness.Shape.Handle, epoch, entry.KeyId, harness.Cancellation)).IsTrue();
        await Assert.That(await cold.HasObjectAsync(harness.Tenant, harness.Shape.Handle, epoch, entry.KeyId, harness.Cancellation)).IsTrue();
        await Assert.That(Volatile.Read(ref heads)).IsEqualTo(1);
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    // The decoded value of one query parameter, or null when the URL does not carry it.
    private static string? Query(string url, string name) => new Uri(url).Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .Where(parts => Uri.UnescapeDataString(parts[0]) == name && parts.Length == 2)
        .Select(parts => Uri.UnescapeDataString(parts[1]))
        .SingleOrDefault();

    // One hex character changed: the same length and alphabet, so the request is well formed but no longer what was signed.
    private static string Flip(string value) => value[..^1] + (value[^1] == '0' ? '1' : '0');

    private static async Task<string?> MustRefetchCode(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    /// <summary>One production server with the mirror, a fake platform bucket, and one shape of the items collection.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly DocumentApi _api;
        // Released by the upload lag metric after a winning PUT is cached; adoption emits no release.
        private readonly SemaphoreSlim _mirrored;
        // Released by the platform for each PUT attempt, whether or not the platform accepts it.
        private readonly SemaphoreSlim _attempts;

        private Harness(DocumentApi api, SemaphoreSlim mirrored, SemaphoreSlim attempts, FakeS3 s3, FakeTimeProvider time,
            ConcurrentDictionary<string, int> puts, Database db, TenantContext tenant, ShapeSealDefinition shape, ShapeSnapshots snapshots)
        {
            _api = api;
            _mirrored = mirrored;
            _attempts = attempts;
            S3 = s3;
            Time = time;
            Puts = puts;
            Db = db;
            Tenant = tenant;
            Shape = shape;
            Snapshots = snapshots;
        }

        public FakeS3 S3 { get; }

        public FakeTimeProvider Time { get; }

        /// <summary>Platform PUTs by key; a retried upload counts again.</summary>
        public ConcurrentDictionary<string, int> Puts { get; }

        public Database Db { get; }

        public TenantContext Tenant { get; }

        public ShapeSealDefinition Shape { get; }

        /// <summary>The production snapshot store over this server's engine, used to seal and to read stored pages.</summary>
        public ShapeSnapshots Snapshots { get; }

        public IShapeMirror Mirror => _api.Server.Services.GetRequiredService<IShapeMirror>();

        public TerraBaseTestServer Server => _api.Server;

        public int PendingMirrorCompletions => _mirrored.CurrentCount;

        public Task<bool> DropDatabaseAsync() => _api.Server.Services.GetRequiredService<IStorageEngine>()
            .DeleteDatabaseAsync(Tenant, Cancellation).AsTask();

        public CancellationToken Cancellation { get; } = TUnit.Core.TestContext.Current!.Execution.CancellationToken;

        /// <summary>With <paramref name="cloudflare"/>, the bucket is an R2 one whose CDN copies that fake purges.</summary>
        public static async Task<Harness> StartAsync(FakeTimeProvider? time = null, FakeCloudflare? cloudflare = null)
        {
            time ??= new FakeTimeProvider(DateTimeOffset.UtcNow);
            var s3 = new FakeS3 { Time = time, R2 = cloudflare is not null };
            var puts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var mirrored = new SemaphoreSlim(0);
            var attempts = new SemaphoreSlim(0);
            s3.ObjectRequested = (customer, method, key) =>
            {
                if (!customer && method == HttpMethod.Put && key.StartsWith("v1/shapes/", StringComparison.Ordinal))
                {
                    puts.AddOrUpdate(key, 1, (_, count) => count + 1);
                    attempts.Release();
                }
            };
            var lineage = new CurrentLineage();
            var api = await DocumentApi.StartAsync(s3.Configure, services =>
            {
                s3.Route(services);
                cloudflare?.Route(services);
                ManualBackupHosting.Configure(services);
                services.AddSingleton<TimeProvider>(time);
                services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, time);
                // These retention scenarios serve one manually sealed lineage; ShapeMirrorHttpTests covers the real HTTP adapters.
                services.AddSingleton<IShapeLineages>(lineage);
                services.AddSingleton<IShapeChunks>(provider => new StoredChunks(provider));
                services.AddSingleton<IShapeSnapshots>(provider => new StoredSnapshots(provider));
                // The mirror reports lag only after it lists an uploaded object, so this is the completion event.
                services.AddSingleton<IShapeMetrics>(provider => new MirrorSignal(ActivatorUtilities.CreateInstance<ShapeMetrics>(provider), mirrored));
            });
            try
            {
                var tenant = await api.TenantOfAsync();
                var db = await Database.OpenAsync(api.Server, tenant);
                var shape = await ShapeAsync(db);
                lineage.Shape = shape;
                return new Harness(api, mirrored, attempts, s3, time, puts, db, tenant, shape, SnapshotStore(api.Server.Services));
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await _api.DisposeAsync();

        /// <summary>Commits one document of about 600 KB: two such commits pass 1 MiB, so each commit closes its own chunk.</summary>
        public Task<long> CommitLargeAsync() => Db.CommitAsync(async transaction =>
            _ = await transaction.PutAsync(Collection, DocumentApi.NewId(), Json(new { pad = new string('x', LargeBody) }), CancellationToken.None));

        public async Task<Sealed> SealAsync(long start)
        {
            _ = await Db.SealAsync(Shape, start);
            return await Db.ChunkAsync(Shape.Handle, start);
        }

        public async Task<Sealed[]> SealAllAsync(long start)
        {
            var head = await Db.SealAsync(Shape, start);
            return await Db.ChunksAsync(Shape.Handle, start, head);
        }

        public Task<HttpResponseMessage> GetShapeAsync(long offset) =>
            _api.Client.GetAsync(new Uri($"{_api.Collections}/{Collection}/shape?offset={offset.ToString(CultureInfo.InvariantCulture)}&handle={Shape.Handle}", UriKind.Relative));

        public Task<HttpResponseMessage> GetManifestAsync(long offset) => GetManifestAsync(Text(offset));

        public Task<HttpResponseMessage> GetManifestAsync(string offset) =>
            _api.Client.GetAsync(new Uri($"{_api.Collections}/{Collection}/shape/manifest?offset={offset}&handle={Shape.Handle}", UriKind.Relative));

        /// <summary>Writes <paramref name="count"/> documents of about 600 KB over HTTP in one transaction.</summary>
        public Task<ApiResponse> WriteLargeDocumentsAsync(int count) => _api.TransactAsync(
            [.. Enumerable.Range(0, count).Select(_ => (object)new { op = "put", collection = Collection, id = DocumentApi.NewId(), document = new { pad = new string('x', LargeBody) } })],
            Cancellation);

        /// <summary>Seals a snapshot of the shape's collection at the feed head, through the production snapshot store.</summary>
        public ValueTask<ShapeSnapshot> SealSnapshotAsync() => Snapshots.SealIfDueAsync(Db.Store,
            new ShapeSnapshotDefinition(Shape.Handle, Shape.ShapeId, Shape.Collection, Shape.CollectionGeneration, Shape.ResumeScope,
                Shape.Incarnation), Cancellation);

        public Task<HttpResponseMessage> GetKeyAsync(string epoch, string keyId) =>
            _api.Client.GetAsync(new Uri($"{_api.Collections}/{Collection}/shape/keys?handle={Shape.Handle}&epoch={epoch}&key_id={keyId}", UriKind.Relative));

        /// <summary>The platform's own client: the presigned URL goes straight to the platform handler, as the SDK sends it.</summary>
        public HttpClient PlatformClient() => _api.Server.Services.GetRequiredService<IHttpClientFactory>().CreateClient("backup-s3");

        public Task<ShapeMirrorManifest> ManifestAsync(long floor) =>
            Mirror.ManifestAsync(Tenant, Shape.ShapeId, Shape.Handle, Text(floor), floor, Cancellation).AsTask();

        /// <summary>Waits until <paramref name="count"/> more objects are listed by the mirror; the test's own cancellation bounds the wait.</summary>
        public async Task MirroredAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                await _mirrored.WaitAsync(Cancellation).WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
            }
        }

        /// <summary>Waits for one PUT attempt to the platform, successful or refused.</summary>
        public Task AttemptAsync() => _attempts.WaitAsync(Cancellation).WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        /// <summary>Queues a sealed chunk under its own offsets, valued at its sealing head, on the server's mirror or the given one.</summary>
        public bool Queue(Sealed chunk, IShapeMirror? mirror = null) => (mirror ?? Mirror).Queue(Tenant, Shape.ShapeId, Shape.Handle,
            Text(chunk.Offset), Text(chunk.EndOffset), chunk.EndOffset, chunk.ValuesAt, chunk.Body, Time.GetUtcNow());

        /// <summary>
        /// A second production mirror over this server's engine, shape storage, metrics, memory, crypto and object store, on the given
        /// clock. It is not hosted: the caller starts it only when it needs the queue worker.
        /// </summary>
        public ShapeMirror NewMirror(TimeProvider time)
        {
            var services = _api.Server.Services;
            var objects = new ShapeObjectStore(services.GetRequiredService<ShapeStorageSettings>(), services.GetRequiredService<IHttpClientFactory>(),
                time, services.GetRequiredService<IShapeCdnPurge>());
            var crypto = services.GetRequiredService<IShapeCrypto>();
            return new ShapeMirror(services.GetRequiredService<IStorageEngine>(), services.GetRequiredService<IShapeStorage>(),
                services.GetRequiredService<IShapeMetrics>(), services.GetRequiredService<IMemoryBudget>(), time,
                () => crypto, objects, NullLogger<ShapeMirror>.Instance);
        }

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

        private static async Task<ShapeSealDefinition> ShapeAsync(Database db)
        {
            await using var transaction = await db.Store.BeginTransactionAsync(write: false, CancellationToken.None);
            var collection = await transaction.GetCollectionAsync(Collection, CancellationToken.None)
                ?? throw new InvalidOperationException("the collection is missing");
            return new ShapeSealDefinition(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                "h1-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Collection, collection.Id, RandomNumberGenerator.GetBytes(16),
                db.Store.Incarnation);
        }
    }

    /// <summary>The production shape metrics, with the mirror's completion reported to the test.</summary>
    private sealed class MirrorSignal(IShapeMetrics inner, SemaphoreSlim mirrored) : IShapeMetrics
    {
        public void RecordRead() => inner.RecordRead();

        public void ChangeWaiting(long delta) => inner.ChangeWaiting(delta);

        public void ChangeFlights(long delta) => inner.ChangeFlights(delta);

        public void RecordSeal(long bytes, TimeSpan elapsed) => inner.RecordSeal(bytes, elapsed);

        public void RecordMirrorLag(TimeSpan lag)
        {
            inner.RecordMirrorLag(lag);
            mirrored.Release();
        }

        public void RecordRedirect() => inner.RecordRedirect();

        public IMemoryReservation? TryReserve(long bytes, TenantContext? tenant) => inner.TryReserve(bytes, tenant);
    }

    /// <summary>The one lineage this server serves, set once the collection exists.</summary>
    private sealed class CurrentLineage : IShapeLineages
    {
        public ShapeSealDefinition? Shape { get; set; }

        public ValueTask<LineageResult> ResolveHandleAsync(TenantContext database, CollectionInfo collection, string handle, CancellationToken cancellationToken) =>
            Resolve();

        public ValueTask<LineageResult> ResolveAsync(ShapeScope scope, CancellationToken cancellationToken) => Resolve();

        private ValueTask<LineageResult> Resolve()
        {
            if (Shape is not { } shape)
            {
                return ValueTask.FromResult<LineageResult>(new Unavailable("the test lineage is not set"));
            }

            return ValueTask.FromResult<LineageResult>(new ShapeLineage(shape.ShapeId, shape.Handle));
        }
    }

    /// <summary>Serves the sealed bytes the real sealer stored for the offset.</summary>
    private sealed class StoredChunks(IServiceProvider services) : IShapeChunks
    {
        public async ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken)
        {
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(scope.Database, cancellationToken)
                ?? throw new InvalidOperationException("the database is missing");
            using var chunk = await Sealer(services).ReadChunkAsync(store, lineage.Handle, offset, cancellationToken);
            if (chunk is null)
            {
                return new MustRefetch("no closed chunk is sealed at this offset");
            }

            return new ShapeBody(chunk.Body.ToArray(), chunk.EndOffset.ToString(CultureInfo.InvariantCulture), false);
        }

        private static IShapeSealer Sealer(IServiceProvider services) => services.GetRequiredService<IShapeSealer>();
    }

    /// <summary>Serves the snapshot pages the production store wrote; a page that was never stored is refetched, not invented.</summary>
    private sealed class StoredSnapshots(IServiceProvider services) : IShapeSnapshots
    {
        private readonly ShapeSnapshots _snapshots = SnapshotStore(services);

        public ValueTask<SnapshotResult> NewestAsync(ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SnapshotResult>(new Unavailable("the test serves no newest snapshot"));

        public async ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, string? afterId, CancellationToken cancellationToken)
        {
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(scope.Database, cancellationToken)
                ?? throw new InvalidOperationException("the database is missing");
            using var page = await _snapshots.ReadPageAsync(store, lineage.Handle, snapshot, afterId, cancellationToken);
            if (page is null)
            {
                return new MustRefetch("no snapshot page is stored at this offset");
            }

            // The last page continues at the snapshot's own offset, as the stored body says.
            var next = page.NextId is { } id ? ShapeSnapshots.PageOffset(snapshot, id) : snapshot.ToString(CultureInfo.InvariantCulture);
            return new ShapeBody(page.Body.ToArray(), next, false);
        }
    }

    /// <summary>The production snapshot store over the server's memory budget, time and Turso engine.</summary>
    private static ShapeSnapshots SnapshotStore(IServiceProvider services) => (ShapeSnapshots)services.GetRequiredService<IShapeSnapshotSealer>();

    /// <summary>One sealed chunk, copied out of its reservation.</summary>
    private sealed record Sealed(long Offset, long EndOffset, long ValuesAt, byte[] Body);

    /// <summary>One database of a server: its production store, its Turso store for direct reads, and a sealer.</summary>
    private sealed class Database(TerraBaseTestServer server, IDatabaseStore store, TursoDatabaseStore turso)
    {
        public IDatabaseStore Store { get; } = store;

        public ShapeSealer Sealer => new(server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<ShapeSealer>.Instance,
            new TursoShapeStorage(server.Services.GetRequiredService<TursoStorageEngine>()), server.Services.GetRequiredService<IRemoteDurability>());

        public static async Task<Database> OpenAsync(TerraBaseTestServer server, TenantContext tenant)
        {
            var store = await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None)
                ?? throw new InvalidOperationException("the database is missing");
            var turso = (TursoDatabaseStore)(await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
            return new Database(server, store, turso);
        }

        public async Task<long> SealWithMirrorAsync(ShapeSealDefinition shape, long offset) =>
            await server.Services.GetRequiredService<IShapeSealer>().SealUpToHeadAsync(Store, shape, offset, CancellationToken.None);

        public async Task<long> SealAsync(ShapeSealDefinition shape, long offset) =>
            await Sealer.SealUpToHeadAsync(Store, shape, offset, CancellationToken.None);

        public async Task<Sealed> ChunkAsync(string handle, long offset)
        {
            using var chunk = await Sealer.ReadChunkAsync(Store, handle, offset, CancellationToken.None)
                ?? throw new InvalidOperationException($"no chunk is sealed at {offset}");
            return new Sealed(chunk.Offset, chunk.EndOffset, chunk.ValuesAt, chunk.Body.ToArray());
        }

        // The chain of closed chunks from `from` to `to`, each starting where the previous one ended.
        public async Task<Sealed[]> ChunksAsync(string handle, long from, long to)
        {
            List<Sealed> chunks = [];
            while (from < to)
            {
                var chunk = await ChunkAsync(handle, from);
                chunks.Add(chunk);
                from = chunk.EndOffset;
            }

            return [.. chunks];
        }

        // Commits one transaction through the production store and returns its commit boundary.
        public async Task<long> CommitAsync(Func<IStorageTransaction, Task> work)
        {
            await using (var transaction = await Store.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                await work(transaction);
                _ = await transaction.CommitAsync(CancellationToken.None);
            }

            return await HeadAsync();
        }

        public Task<long> HeadAsync() => turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT seq FROM _tb_feed_sequence WHERE id = 1;";
            return (long)command.ExecuteScalar()!;
        }, "a test reading the feed head", CancellationToken.None);
    }
}
