# 21. Slice plan: FR-24 cache headers

**FR-24, origin part** (2026-10-14, user decision; no storage format change). This file supersedes the origin rows of
[`shape-reads.md`](../shape-reads.md) section 11 and the T0 row of [`free-reads.md`](../free-reads.md) section 3.9.

## Rule

| Where | `Cache-Control` |
| --- | --- |
| Every answer of `GET …/collections/{collection}/shape` (origin, T0): `200` (snapshot page, chunk, fresh, live), `304`, both `307`s, every error including `401` and `403` | `private, no-store` |
| Shape key and manifest endpoints | `private, no-store` (key errors included) |
| TBS1 ciphertext objects in Tigris (T1) | `public, max-age=31536000, immutable` (FR-22, `ShapeObjectStore.CacheControl`) |

Only TBS1 ciphertext is ever shared-cacheable. Origin bodies are tenant plaintext, and every origin answer depends on
the caller's credential, so no cache, private or shared, may store one. `Vary: Authorization`, `ETag` and `304` stay:
a client that keeps its own copy can still send `If-None-Match`. Conditional requests remain supported, but clients must obey the new no-store policy.

## As built

- `OriginNoStore` endpoint metadata marks `getShape` and `getShapeKey`. `UseTerraBaseShapeNoStore`, registered right after routing, adds a
  `Response.OnStarting` callback for marked routes that sets `private, no-store` last. It therefore also covers `401`,
  `403`, `429` and the errors that `BadRequests` and `ErrorResponse` write as `no-store`, and survives
  `Response.Clear`. Other routes keep their own headers; the global error writers are unchanged.
- `GetShapeEndpoint` no longer sets `Cache-Control` itself.
- This addendum supersedes the legacy `shape-vectors.json` origin cache headers. The checked HTTP resume vectors
  retain their exact status and body assertions and now assert the FR-24 origin header. Keys, manifest and Tigris
  vector policies are unchanged.
- Fast: `ShapeApiTests.Every_origin_answer_including_401_and_403_is_private_no_store_and_never_shared_cacheable`, plus
  the existing exact assertions in `ShapeApiTests` and `ShapeEndToEndTests`.

## T1 (Tigris) requirements, from V4 (#301)

FR-22 (#283, open) sets the Tigris header at the one-time conditional PUT. After #283 merges, these must hold and be
tested against `FakeS3` and live Tigris before FR-24 qualification completes:

1. `public, max-age=31536000, immutable` only on TBS1 objects; PUT is `If-None-Match: *`, so a key never maps to
   other bytes.
2. `Content-Type: application/octet-stream`, no `Content-Encoding`, no `Content-Disposition`.
3. Object metadata holds no tenant name, handle, offset, collection or filter. #283 stores an owner hash, object hash,
   SHA-256, epoch, key id, seal time and floor offset; the floor offset and seal time are retention positions, not
   identifiers, but they are visible to anyone holding the presigned URL (HEAD).
4. The object key is `v1/shapes/<handle>/<hash>` in #283, so the handle is in the URL path. `shape-reads.md` section 14
   specifies `v1/shapes/<hash>` with no handle. FR-22 explicitly selects the handle-prefixed path; the handle is not tenant-identifying
   but links objects of one shape. Object hashes bind tenant identity, database incarnation, collection, filter, handle and offset.
5. Presigned URLs expire at `(E_now + 1) × 600` (#283 does). Tigris sets the headers of its own `403` and `404`
   answers; TerraBase cannot. The FR-24 isolated proof records them; no error-cache behavior is claimed before that evidence exists.

Enabled only after A1 (#271) enforces the peer credential, so a forged peer write cannot be sealed and shared.

TBS1 exposes exact sizes, epoch (seal time to ten minutes), and the project-wide KID (including its ULID creation
time). These link objects until key rotation, as measured in V4 finding F2. Shared caching does not hide this metadata.
