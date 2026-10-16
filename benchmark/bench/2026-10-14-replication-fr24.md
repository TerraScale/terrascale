# FR-24 live shared-cache header proof: 2026-10-14 (ts-vtest-fr24-proof, deleted)

**Isolated result:** run2 passed 86 of 86 checks for objects published by gru, sjc and fra. A separate live 429 capture also passed. This is a composite test image, not production qualification; all object downloads originated from one client location.

## What ran

- **Commit:** composite `920629bb19f0137dfe0611956cc7238091bc1c8b` on branch `feat/repl-fr24-proof-composite`. This is a proof branch only, with no PR. It contains:
  - origin/main `1b36dd17`
  - #271 (peer auth, `feat/repl-a1-peer-auth-enforce`)
  - #283 (Tigris mirror, `feat/repl-fr22-tigris-mirror`)
  - FR-24 `d75630fa` and `0a40deac`. These are equivalent to PR #315 head `a4979bfa` after its rebase.
  - One composite-only line, listed under "Composite-only delta" below.
- **Image:** built on mariogk-dev through `ts-heavy-any` (podman, `--target terrabase`, `TERRABASE_REVISION` set to the commit). Digest `sha256:237255951ae07d53be298daa8ecc5ab1f024d71c02bf37add98990302faeaba3`.
- **Fly:** org `terrascale-previews-515`, 3 × shared-cpu-2x 512 MB machines:
  - gru `857655da474298`
  - sjc `2861e54f145038`
  - fra `28744119f50048`
- **Storage:** a 1 GB volume per machine. The app had its own Tigris bucket, `ts-vtest-fr24-mirror`.
- **Tenants:** two synthetic tenants, A and B. Each had a database `fr24` with home gru and replicas sjc and fra.
- **Secrets:** a test-only peer secret and master key were generated locally into 0600 files. They were staged with `fly secrets import`, never printed, and shredded after the run.
- **Window:** 19:43–20:06Z. Billing was not measured.

## Results (run2: `run2/fr24-log.json`, `run2/stdout.log`)

| Check | gru | sjc | fra |
|---|---|---|---|
| Sealed TBS1 objects (snapshot `snap.63.0` and sealed chunk `63`), presigned GET: `Cache-Control: public, max-age=31536000, immutable`, size, sha256 and decrypt equal origin bytes | ✅ | ✅ | ✅ |
| Second reads: 6 Tigris GETs per region (same URL twice, plus a re-presigned URL with a different query) gave the same bytes and ETag. Origin `terrabase_storage_reads_total{path="shape_body"}` moved by **0** | ✅ Δ0 | ✅ Δ0 | ✅ Δ0 |
| Origin 200 bodies (snapshot page, chunk, tail), bootstrap **307**, **304**, live tail woken by a write, manifest 200, keys 200: all `private, no-store` | ✅ | ✅ | ✅ |
| Origin errors: 401 shape, 401 manifest and 401 keys with no auth; 400 bad offset; 400 manifest `-1`; 400 future epoch; 409 stale handle; 409 unretained key; 404 unknown collection: all `private, no-store` | ✅ | ✅ | ✅ |
| **429 `shape_busy`** (live, fra at default memory budget): `private, no-store` (`run2/busy-429-fra.txt`) | | | ✅ |
| Tenant isolation: B's key on A's shape, manifest and keys gives 404 `private, no-store` | ✅ | ✅ | ✅ |

Tenant isolation, object-level checks:
- B's handle differs from A's.
- B's objects live under `v1/shapes/<B handle>/`.
- None of A's 3 epoch keys opens B's object.
- A's signature on B's object path gives Tigris 403.
- The owner hashes differ.

Tigris error answers (`tigris:*` checks) carry **no `Cache-Control` at all**: no explicit lifetime, `public`, or `immutable`. This does not prove errors are uncacheable: HTTP 404 can be cached heuristically. Error-cache lifetime remains unqualified. Each answer was `application/xml` from `server: Tigris OS`:
- 403 for a tampered signature
- 403 for an unsigned GET
- 403 for an expired URL
- 404 for a validly signed GET of a missing key

Write-once: a conditional overwrite (`If-None-Match: *`, same bytes, `Cache-Control: no-store`) of all 6 listed objects with the bucket's scoped credentials gave **412** every time. HEAD ETag, Last-Modified and Cache-Control were unchanged afterwards.

V4 metadata: I inspected all 38 objects in the bucket.
- User metadata keys are only `epoch`, `floor-offset`, `key-id`, `object-hash`, `owner`, `sealed-at` and `sha256`.
- No literal org, project or database id, collection name or document id appears in keys or metadata. The owner hash can confirm a guessed tenant identity; timing and retention metadata remain visible.
- Every object's Cache-Control is `public, max-age=31536000, immutable`.

#271, observed live: an operator call to the internal listener without `X-TerraBase-Peer-Credential` gave 401. The same call with the credential reached validation (400).

## Before and after (dry run on interim image `106c3542`, without the FR-24 guard: `dry/`)

Without the guard:
- 401, 400 and 404 errors and the 429 answer were sent with plain `no-store`, not `private, no-store`.
- Isolation 404s were also plain `no-store`.

With `920629bb`, every sampled origin answer is `private, no-store`.

## Measured but NOT proved

- **Tigris shared-cache hits are not proved.** No `Age` or cache-status header is exposed (`age` was absent on every GET). The only claim is that repeated object reads never touched origin storage (Δ shape_body = 0). All downloads came from one client location (mariogk-dev). `x-tigris-regions` equalled the writer region.
- Origin re-reads of a sealed chunk cost 1 `shape_body` read each (3 reads gave Δ3 in every region). The origin has no in-process body cache, so offload to Tigris depends on clients using the manifest.
- One manifest call cost 1 `shape_body` read in every region (Δ1).

## Findings for the parent / follow-ups

1. **F1 (FR-22 finding, still present on this composite):** bootstrap answers `429 shape_busy` on 512 MB machines at the default memory budget.
   - This time it failed at the `offset=-1` bootstrap itself.
   - Workaround for this proof only: the `--memory-budget=268435456` CLI argument (no capacity change).
   - The 429 answer has **no `Retry-After`**.
2. **Epoch-boundary URL lifetime (FR-22 design):** presigned URLs expire at the next 10-minute epoch start.
   - A manifest issued 32–36 s before the boundary returned URLs with `X-Amz-Expires=32..36`.
   - This explains why run1 failed: re-reading those URLs after 20:00:00Z got non-200, and run2 (clean) passed.
   - Clients must treat a Tigris 403 as "re-fetch the manifest". The FR-22 fallback already covers this.
3. **Error-cache limit:** Tigris emits no explicit no-store policy on its errors. TerraBase controls origin error headers, not the object store responses; no downstream error-cache lifetime guarantee was demonstrated.
4. **V4 note:** `owner` = sha256(org‖project‖database ids), unsalted. Anyone holding a URL plus candidate ULIDs can confirm ownership.
   - `sealed-at`, `epoch` and `floor-offset` reveal timing and volume.
   - None of this is plaintext, but it is linkable. Raise it in the V4 review.

## Composite-only delta (for the FR-24 rebase after #283 lands)

`composite-only-delta.rangediff` and `composite-only-delta-0a40deac.rangediff` hold the changes:
- `GetShapeManifestEndpoint.cs`: `Metadata(new ListenerMetadata(TerraBaseListener.Client), OriginNoStore.Instance);`.
- The manifest's inline `CacheControl = NoStore` is still there. It is redundant once the guard owns it.
- Conflict resolutions:
  - Keep #283's `mirror.Warm(...)` and drop the `CachePrivate` assignment in `GetShapeEndpoint`.
  - Use `DocumentNoStore(response)` in `ShapesExtensions` in place of #283's inline OpenAPI `Cache-Control` header block.

FR-22 already tests public/365-day/immutable headers in `A_presigned_url_serves_the_ciphertext_and_refuses_tampering_and_expiry`. FR-24 will extend that regression with ciphertext content/metadata assertions after #283 lands.

## Cleanup (verified 20:06Z)

- App destroyed: 3 machines, 3 volumes, IPs, secrets and the registry repo.
- Bucket `ts-vtest-fr24-mirror` destroyed. An earlier unused bucket `ts-vtest-fr24-proof` was destroyed before use.
- Deploy token `fr24-vtest-push` revoked.
- 20 of 20 minted API keys revoked; none failed.
- Proxies stopped.
- Local secret files shredded.
- Podman images and `~/remote-builds/feat-repl-fr24-proof-composite` removed.
- `flyctl apps list` and `storage list` show no fr24 resources. `status` and `volumes` return "app not found", and the hostname is unreachable.
- No production app, secret or bucket was touched.

The branch `feat/repl-fr24-proof-composite` and the worktree `~/.t3/worktrees/terrascale/fr24-proof-composite` remain for reference.

## Evidence (redacted: no key values; presigned URLs reduced to scheme/host/path and parameter names)

- `run2/` is the clean run, plus `busy-429-fra.txt`.
- `run1/` is the run that crossed the epoch boundary.
- `dry/` is the pre-guard baseline.
- `ledger.md` lists every resource and key with create, revoke and delete times.
- `scripts/` holds `fr24.py`, `proof.py` (FR-22 helpers), `with-keys.sh`, `tenant.sh`, `run.sh` and `busy.sh`.

Parent verification: separate app and bucket listings after the delegated run returned zero names containing `fr24`.
