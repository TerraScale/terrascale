# ts-auth SDK clients: first receipts and optimization pass (2026-09-20)

The four official SDKs gained ts-auth management API clients (users, sessions,
API keys, organizations, connections, directories, JWT templates, webhooks,
audit, billing, instance, OAuth clients, OIDC, webhook-signature verifier).
This report records the first benchmark receipts for every client, the
optimization pass, and the before/after evidence. Everything runs against the
same canned-response in-process stub (see `ts-auth/README.md`): constant
service cost, so the numbers measure the official client path.

## Receipts

| Receipt | Subject | Phase |
| --- | --- | --- |
| `terrabase/receipts/20260920-tsauth-sdk-before/` | .NET `TerraScaleAuthClient` | before optimization |
| `terrabase/receipts/20260920-tsauth-sdk-after/` | .NET `TerraScaleAuthClient` | after optimization |
| `terrabase/receipts/20260920-terrabase-sdk-before/` + `-after/` | .NET `TerraScaleClient` (document lanes) | overall-SDK parity check |
| `ts-auth/receipts/20260920-go-tsauth/` | Go `TSAuthClient` | first receipt |
| `ts-auth/receipts/20260920-rust-tsauth/` | Rust `AuthIdpClient` | first receipt |
| `ts-auth/receipts/20260920-typescript-tsauth/` (+ `-after`, `-after2`) | TS `TsAuthClient` | before/after optimization |

Envelope for every run: `taskset -c 0-3 nice -n 19`, 4 cores, 5 samples × 5000
operations after 1000 warmup, seed 1337, dataset 1000 users × 256-byte payload,
every response verified. Medians of per-sample percentiles.

## .NET: ts-auth client optimization (measured)

Optimizations: cached immutable Accept/Content-Type header values (one
allocation, not one per call) and deserializing straight off the content
stream instead of `ReadFromJsonAsync`'s synchronizable-stream wrapper.

| Lane | Before | After | Delta |
| --- | ---: | ---: | ---: |
| read c1 | 29,089 ops/s · p50 27.3 µs | 35,885 ops/s · p50 24.2 µs | **+23% ops/s**, p99 67→48 µs |
| query c1 | 3,056 ops/s · p50 258.8 µs | 3,735 ops/s · p50 229.8 µs | **+22%** |
| mixed c1 | 14,497 ops/s · p50 14.7 µs | 16,564 ops/s · p50 11.6 µs | **+14%** |
| write c1 | 47,571 ops/s | 50,109 ops/s | +5% |
| read c16 | 224,497 ops/s | 244,544 ops/s | +9% |
| write c16 | 200,124 ops/s | 188,153 ops/s | −6% (noise) |

## .NET: overall SDK parity (measured)

`terrabase-sdk` document lanes before/after the whole change set. The
`TerraScaleClient` core was not modified; observed swings in both directions
(write c16 ASYNC p50 1103→288 µs, read c1 SYNC 4750→3380 ops/s) match the
documented run-to-run variance of this box — parity within noise, no
regression introduced.

## TypeScript: shared transport optimization (measured)

`composeAbortSignals` now returns a single live signal as-is instead of
forwarding it through a fresh controller — every request previously paid a
controller + listener + `addEventListener` hop even with only a deadline
signal. This benefits all SDK routes, not only ts-auth.

Isolated probe (identical stub, 20k gets after 3k warmup): 111.1 µs/op before
→ 94.2 / 91.6 / 85.8 µs/op after (**−15 to −23%**). Full harness receipts
(single-run lanes swing ±20% on this box):

| Lane | Before | After (2 runs) |
| --- | ---: | ---: |
| mixed c1 | 9,580 ops/s · 95.6 µs | 10,189 / 10,193 ops/s · 89.1 / 89.5 µs (**+6%**) |
| write c1 | 8,489 ops/s · 101.4 µs | 8,428 / 9,354 ops/s (noise to +10%) |
| read c1 | 9,400 ops/s · 96.2 µs | 7,744 / 11,498 ops/s (variance dominates) |
| read c16 | 13,528 ops/s | 14,182 / 14,289 ops/s (+5%) |
| write c16 | 11,552 ops/s | 12,356 / 13,687 ops/s (+7 to +18%) |

The probe isolates the same operation the read lane measures; the lane-level
spread is documented, not hidden.

## First receipts: Go and Rust

No before-state exists (the clients are new); these receipts are the baseline.

| Lane | Go p50 | Rust p50 | .NET p50 (after) |
| --- | ---: | ---: | ---: |
| read c1 | 13.5 µs · 57.7k ops/s | **4.4 µs · 219k ops/s** | 24.2 µs · 35.9k ops/s |
| read c16 | 28.0 µs · 63.4k | **8.8 µs · 431k** | 13.4 µs · 244k |
| query c1 (~40 KB page) | 400.8 µs | **189.2 µs** | 229.8 µs |
| write c1 | 14.1 µs | **5.2 µs** | 19.0 µs |
| write c16 | 26.0 µs · 108.7k | **9.7 µs · 407k** | 16.3 µs · 188k |

Directional only across runtimes (different JSON stacks, allocators, and HTTP
machinery — the standing within-run-ratio rule applies). Findings worth
carrying forward:

- Rust's typed decode path (serde) is the fastest here on every lane.
- Go's `map[string]any` metadata decode costs 289 KB/op on the 37 KB query
  page (~7.3× payload) — inherent to the friendly `map[string]any` API shape;
  switching to `json.RawMessage` would cut it but changes the public API.
- .NET and Go pay a per-request deadline timer; the .NET lane after
  optimization sits at 24.2 µs p50 for a ~800-byte typed user decode.
- The TS client's remaining cost is dominated by undici Request/Response
  construction (~20% of profile) — platform machinery, not SDK fat. The
  SDK-side `buildUrl` origin-escape double-URL was deliberately left alone:
  it is a security check, and removing it buys ~4%.

## Edge-case suite and 10-reviewer panel (same day)

A 60-item edge-case matrix (/tmp spec) landed as 173 tests across the four
SDKs (.NET 58, Rust 48, TS 59, Go 8 grouped suites), plus a 10-reviewer panel
(4× contract fidelity, security, concurrency/async, error handling,
conventions/cross-SDK consistency, test quality, performance).

Defects found and fixed (all suites green after):
- All four webhook verifiers accepted correctly-signed non-numeric timestamps
  in the no-tolerance variant — now false everywhere.
- ts-typescript: missing `apiBaseUrl` silently defaulted to the TerraBase
  product origin (the instance `sk_` key would be sent to the wrong service);
  `.`/`..` ids/slug escaped the route root; `listAll` crashed on
  `{"data":null}`; **every unix timestamp was typed `string` while the wire
  carries numbers** (22 fields + fixtures).
- ts-rust: `revoke`/`introspect` omitted client credentials (always 401
  against the real service); a client-level TerraBase idempotency key made
  ts-auth mutations retryable (double-write class); no 30 s deadline; slug
  dot segments collapsed routes; empty verifier secret verified.
- ts-go: slug/ids accepted `.`/`..`; oversized-response errors lost the HTTP
  status; empty 2xx bodies silently decoded to zero values; create enforced a
  password rule the server does not have.
- ts-dotnet: error bodies captured unbounded and unredacted (now capped 64 KiB
  + redacted like the main SDK); decode failures leaked raw `JsonException`;
  the 30 s deadline ended at headers (now covers the body read);
  `EnumerateAsync` could loop on a hostile server and NRE on `{"data":null}`;
  slug accepted `/` and `..`.
- Cross-SDK alignment now: dot segments rejected preflight in all four;
  Retry-After surfaced on the error types (Go `APIError.RetryAfter`, Rust
  `Error.retry_after`, TS `TerraScaleError.retryAfter`, .NET
  `RetryAfter`); pagination walks stop on short pages and repeated cursors;
  DELETE requires 204 in .NET/Go/Rust; verifier empty-secret false everywhere.
- Service fix: the dead-letter projection now emits `last_error` (it was
  serialized as `lastError` while every sibling field is snake_case) — all
  four SDKs decode it, pinned by tests and the E2E.

## 50-reviewer panel (final round)

The panel ran as 4 repos x 10 lenses + 10 cross-cutting lenses (contract
fidelity per family, verifier security, resilience, error hygiene, pagination,
configuration, test quality per SDK; cross-SDK consistency, secrets lifecycle,
injection/path integrity, concurrency, docs accuracy, API-surface design,
benchmark methodology, service-side changes). New findings and fixes:

- Regression caught and fixed: the 10-reviewer DELETE-204 fix had swept the
  OAuth revoke route in Go (the service answers 200-empty per RFC 7009) —
  revoke now has its own any-2xx path, pinned by a corrected fixture.
- .NET: webhook verifier threw `ArgumentOutOfRangeException` on
  out-of-representable-range timestamps with tolerance (now false); decode
  failure messages now run through the same redaction as every other message;
  audit integrity fields modeled nullable (the service nulls them without a
  signing key); ToString on all eight one-time-secret records redacts;
  limit pre-flight 1..100; pagination walk gained a monotonic cursor guard;
  redaction keys extended (signing_secret, jwt, credentials, authentication).
- Go: slug is now escaped in the route prefix (`?`/`#` slugs previously
  mutated the URL); the default-origin guard is case-insensitive; audit
  integrity fields pointers; `TSAuthUser` email/username/name are pointers
  (the service emits nulls); instance PATCH models the absent `created_at`;
  `Without*` patch builders renamed `Unset*` (the cross-SDK vocabulary);
  String() redaction on the three one-time-secret structs; deterministic
  cancellation test, wider timing margins, uncapped Retry-After pin.
- Rust: TerraBase tenant/project/region headers no longer leak to ts-auth
  requests; Retry-After >30s surfaces instead of retrying (matching .NET/TS)
  and is exposed uncapped; the 30s deadline now has a paused-clock test;
  ClientConfig/builder/Request/Response Debug redact keys, authorization
  headers, and bodies; minted-JWT Debug redacted; the tolerant verifier is
  re-exported at the crate root.
- TypeScript: `email` nullable; audit integrity fields nullable; instance
  update request no longer suggests null-clearing semantics the service lacks;
  `jwt` added to the result-level sanitizer; inline request types hoisted.
- Service: none this round (the `last_error` fix landed in the 10-reviewer
  round).
- E2E hardened: dead-letter `LastError == "HTTP 500"` exact pin, reconcile
  `ClaimsExamined == 0` deterministic pin, revoked-key error code pinned,
  webhook delivery body type verified before signature check, disjoint sink
  port range.
- Recorded divergences (accepted, platform-idiom or documented):
  public-client OIDC secret optionality (the service ignores secrets for
  public clients; all four SDKs require the parameter), Rust Retry-After
  seconds-form-only parsing, missing-id decode strictness (Rust errors, the
  others tolerate per idiom), Go's zero-value response fields on routes that
  omit them, Rust sub-client `Client` suffix (renaming collides with the
  response record types), TS DELETE 204 leniency in its Result-based layer.

Final receipts `20260920-tsauth-sdk-final` (same envelope): every lane inside
the observed box envelope; single-lane swings remain +-30% run to run as
documented above.

Post-review receipts `20260920-tsauth-sdk-postreview{,2}` (same envelope):
read c1 23.9k/31.2k, query 3.4k/3.9k, write c1 48.6k/34.8k, write c16
213.9k/203.5k — every lane inside the before/after envelope; single-lane
swings of ±30% across runs are this box's documented variance (see the
terrabase-sdk parity section), not a systematic regression from the
correctness fixes.

## Verification (final)

- ts-dotnet: build 0 warnings, format clean, **271/271 unit tests**, 9/9
  real-service E2E (dead-letter lifecycle with exact `HTTP 500` pin).
- ts-go: gofmt/vet clean, `go test ./...` and `go test -race` green.
- ts-rust: fmt clean, clippy `-D warnings` clean, all 18 test suites green
  on the pinned 1.97.1 toolchain.
- ts-typescript: typecheck/build/forbidden-surface green; 517 tests pass
  (the 5 producer-corpus failures reproduce identically on a clean tree —
  contract bundles not staged locally).
- Benchmark runner `--self-check` passes; the `tsauth-sdk` lane and the three
  language harnesses run green.


- ts-dotnet: build 0 warnings, `dotnet format style` clean, 207/207 unit
  tests, 9/9 real-service E2E (ts-auth suite over the embedded TerraBase
  engine) after the optimization.
- ts-go: `gofmt` clean, `go vet` clean, `go test ./...` and `go test -race`
  green.
- ts-rust: `cargo fmt --check`, `cargo clippy -D warnings`, `cargo test`
  green on the pinned 1.97.1 toolchain.
- ts-typescript: `typecheck`, `build`, forbidden-surface and public-boundary
  gates green; 11/11 ts-auth tests (the 5 suite failures that need staged
  contract bundles reproduce identically on a clean tree).
- Benchmark runner `--self-check` passes with the new `tsauth-sdk` lane.
