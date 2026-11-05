# Free reads T2: the shape CDN

Shape reads ([shape-reads.md](shape-reads.md), [free-reads.md](free-reads.md) 3.8 and 3.9) are served by three tiers.
Each one removes origin work for readers of the same shape.

| Tier | Serves | Origin cost per extra reader |
| --- | --- | --- |
| T0 | The origin: live polls join one flight, ETags and the closed-chunk cache | One HTTP response |
| T1 | The bucket. `GET …/shape/manifest` lists presigned URLs of the mirrored TBS1 objects | One manifest response, no storage read once the object is mirrored |
| T2 | Cloudflare's cache in front of an R2 bucket. The manifest lists each object's stable CDN path | One manifest response; the bucket sees about one read per object per Cloudflare cache |

## How T2 works

- The object store is S3-compatible and set by endpoint, bucket and keys only (`backup-*` settings). Tigris and R2 run
  the same code.
- When the endpoint is `https://<account>.r2.cloudflarestorage.com`, the manifest links each object as
  `https://shapes-<bucket>.terrascale.tech/v1/shapes/<handle>/<64 hex>` (`ShapeObjectStore.Cdn`). The URL has no
  query, so every reader and every epoch shares one cache key. Any other bucket keeps presigned URLs (T1).
- Each object is PUT once with `Cache-Control: public, max-age=31536000, immutable`. Its name is a SHA-256, and its
  bytes are TBS1 ciphertext (AES-GCM under an epoch key). A cached copy is useless without the key, which only
  `GET …/shape/keys` releases: authenticated, `sql:read`, `private, no-store`, revocation rechecked.
- `expires_at` in the manifest is still the start of the next epoch. A client reads the manifest again after that
  time. A CDN link itself does not expire.

## Cloudflare setup (zone `terrascale.tech`)

Every R2 bucket that TerraBase uses needs these steps before nodes point at it. Without them, T2 links do not resolve,
and clients fall back to origin reads.

1. Create the bucket and attach its custom domain:
   `cf r2 buckets domains custom create <bucket> --domain shapes-<bucket>.terrascale.tech --zone-id <zone> --enabled --min-tls 1.2`.
2. Zone rules that cover every `shapes-*` host. They are set once and already exist:
   - WAF custom rule (`http_request_firewall_custom`): block any request to a `shapes-*` host that is not a `GET` or
     `HEAD` of `/v1/shapes/…`, or that has a query, `..` or a percent-escape. The bucket also holds backups and logs,
     so this rule keeps the public custom domain limited to shape ciphertext.
   - Cache rule (`http_request_cache_settings`): `shapes-*` hosts are eligible for cache. Edge and browser TTLs follow
     the object's `Cache-Control`. Responses with status 400–599 are `no-store`, so a probe for a missing key cannot
     pin a 404 in the cache. Without this rule, Cloudflare does not cache extensionless keys (`cf-cache-status: DYNAMIC`).
   - Smart Tiered Cache is on for the zone, so a miss at one location is first served from an upper tier.
3. Give the bucket's S3 credentials Object Read & Write on that bucket only. Give `cloudflare-api-token` Zone Cache
   Purge and Zone Analytics Read on `terrascale.tech`. Both go to OCI Vault and reach nodes through ts-orch.

Buckets set up this way: `terrascale-gru-terrabase` (production gru), `ts-terrabase-ci` (Slow tests),
`ts-terrabase-bench` (benchmarks).

## Purge

Every shape-object deletion first admits its exact URL to a durable outbox. A worker verifies R2 absence, purges the
handle prefix through Cloudflare, checks both HTTP success and `success: true`, then requires a public GET of every
URL to return 404 before clearing the batch. Prefix purging removes Origin variants and every cache tier.

Database DELETE uses the private per-database ownership inventory, after joining regional publishers, so unrelated
objects and the backup erasure backlog do not delay admission. Uploads and adoption record ownership before exposing
an object in a manifest. The existing shared-inventory sweep recovers objects that predate this inventory.

The healthy-provider deadline is **420 seconds after successful database DELETE** for current-service URLs in all
three regions. The queue admits at most 2,000 URLs, oldest first, in batches of 100. Each pass has an 18-second budget
on a two-second timer. Failed purge requests use backoff and preserve durable work; provider failures can exceed the
deadline and are visible in queue age and failure metrics. Full admission refuses the object deletion and keeps its
ownership authority. See [F11](crypto-shredding-f11.md#cdn-deadline-after-delete) for the bound and failure contract.

The runtime token is `shapes-cdn-api-token` (`TERRABASE_SHAPES_CDN_API_TOKEN_FILE` from OCI Vault through ts-orch).
It uses the existing Zone Cache Purge and Zone Analytics Read permissions on `terrascale.tech`.

## Metrics

`terrabase.shape.cdn.requests{cache_status}` counts the requests Cloudflare answered on the node's
`shapes-<bucket>` host, taken from zone analytics (`httpRequestsAdaptiveGroups`) and never counted per request
(`ShapeCdnAnalytics`).

- `cache_status` is `hit` (served from cache), `miss` (read from the bucket) or `other` (anything else, such as a
  blocked request).
- Every 5 minutes the node reads the half-open window up to 10 minutes ago, so Cloudflare has ingested it. Windows are
  adjacent, so no request is counted twice. A failed read is retried with the next window.
- The hit ratio is `hit / (hit + miss)`. `miss` is the bucket's read count per cache location (FR-32).
- The token also needs Zone Analytics Read on `terrascale.tech`.

## Limits

- Provider failures retain the durable purge entry. The queue count, oldest age, per-attempt results and public
  verification failures are exposed as `terrabase.shape.cdn.purge_pending`, `purge_oldest_age`, `purge_attempts` and
  `purges`, respectively. A refused admission leaves the object for retry.
- Never-published orphan objects and objects predating the ownership inventory still use the shared recovery sweep;
  their admission time depends on the inventory and erasure backlog.
- Anyone who knows an object's name can download its ciphertext. Object names come only from authenticated manifests.
  Opening the bytes still needs the epoch key from the authenticated key route.
- Live polls at the head stay on the origin (`private, no-store`). T2 serves only catch-up reads of sealed objects.

Proof: `tests/TerraBase.Fast/Shapes/ShapeMirrorHttpTests.cs` checks that mirrored objects decrypt to the origin bytes.
Measurements are in `benchmark/bench/2026-10-23-free-reads.md`.
