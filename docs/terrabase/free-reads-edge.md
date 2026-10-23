# Free reads: tiers and the shape edge (T2)

Shape reads ([shape-reads.md](shape-reads.md), [free-reads.md](free-reads.md) 3.8) are served by three tiers. Each one
removes origin work for readers of the same shape.

| Tier | Serves | Origin cost per extra reader |
| --- | --- | --- |
| T0 | The origin. Live polls join one flight per `(handle, offset, live)`, whatever their cursor; collections are read from memory; closed chunks are kept in a bounded memory cache (512 chunks, 16 MiB) | One HTTP response and a few metadata reads; one payload read per chunk |
| T1 | The bucket (Tigris or R2, any S3-compatible store: endpoint, bucket and keys only). The manifest lists presigned TBS1 objects from the mirror's metadata | One manifest response, no storage read once the object is mirrored |
| T2 | Cloudflare, through the shape edge Worker (`src/TerraBase.ShapeEdge`) at `https://shapes.terrascale.tech` | None for a cached object; one bucket read per Cloudflare location |

## The edge

- The manifest links an object in an HTTPS bucket as `https://shapes.terrascale.tech/<bucket host>/<bucket>/<key>?<SigV4 query>`
  (`ShapeObjectStore.Edge`). A plain-HTTP bucket, which only local fakes use, is linked directly.
- The Worker accepts only `GET`/`HEAD` of `v1/shapes/<handle>/<64 hex>` keys on Tigris or R2 bucket hosts, and only while
  the URL's signature has not expired, so it is no open proxy and keeps the bucket's link expiry.
- Its cache key is the path without the query. Every reader at one Cloudflare location shares one copy, although each
  manifest signs its URLs separately. The bytes are TBS1 ciphertext, so a cached object is useless without the epoch key,
  which only `GET …/shape/keys` releases under `documents:read` (free-reads.md 3.9).
- A miss forwards the signed request unchanged to the bucket, which checks the signature, and caches a `200` that carries
  the object's `public, max-age=31536000, immutable` policy. Responses carry `tb-edge-cache: HIT` or `MISS`.
- Deploy from `src/TerraBase.ShapeEdge`: `npx -y wrangler@4.152.0 deploy` (custom domain `shapes.terrascale.tech`).
- The Cache API is per Cloudflare location and is not tiered, so the bucket sees about one read per object per location
  that has readers.
