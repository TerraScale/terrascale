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
  `GET …/shape/keys` releases: authenticated, `documents:read`, `private, no-store`, revocation rechecked.
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
3. Give the bucket's S3 credentials Object Read & Write on that bucket only. They go to OCI Vault and reach nodes
   through ts-orch.

Buckets set up this way: `terrascale-gru-terrabase` (production gru), `ts-terrabase-ci` (Slow tests),
`ts-terrabase-bench` (benchmarks).

## Limits

- A Cloudflare cache can keep an object after retention deletes it from the bucket (7 days). The object is ciphertext.
  The manifest never lists it again, and nothing purges the cache.
- Anyone who knows an object's name can download its ciphertext. Object names come only from authenticated manifests.
  Opening the bytes still needs the epoch key from the authenticated key route.
- Live polls at the head stay on the origin (`private, no-store`). T2 serves only catch-up reads of sealed objects.

Proof: `tests/TerraBase.Slow/Shapes/S3ShapeMirrorTests.cs` checks on Tigris (T1) and on R2 (T1 plus a cached T2 read).
Measurements are in `benchmark/bench/2026-10-23-free-reads.md`.
