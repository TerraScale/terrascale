# Production R2 and CDN for free shape reads

W0-7 design, 2026-10-29. F6 executes this design; **nothing is provisioned by
this document**. G-R2 is approved for F6, including usage above the free tier.
The authoritative pivot decisions are D8, D10, D19 and D24 in `PIVOT.md`
on delta (`/home/mariogk/Projects/TerraScale/PIVOT.md`).

This supersedes the production setup recommendations in
[free-reads-cdn.md](../free-reads-cdn.md), retaining its TBS1 transport. R2
holds shape ciphertext; Tigris holds backups and PITR. Shape/CDN reads are
not billed to customers. Manifests, keys and live tails still consume origin
resources; this is not an unlimited free ad-hoc SQL promise.

## Evidence and read-only live inspection

[The 2026-10-23 benchmark](../../../benchmark/bench/2026-10-23-free-reads.md)
measured three repeats: 11 R2 GETs for 11 objects at each of 1, 10, 100 and
1,000 readers; 0.999 cache hit ratio at 1,000 readers and zero origin payload
reads. Every response came from CWB. Cross-location Tiered Cache savings
were **not measured**; eviction, retries and revalidation can add bucket reads.

Live checks on 2026-10-29 used `cf` on delta, read-only. Local OAuth refresh
failed; delta's existing authentication worked. No credentials were displayed.

| Surface | Observed state |
| --- | --- |
| `ts-terrabase-bench` | ENAM, Standard, default jurisdiction; created 2026-10-23 |
| Benchmark domain | `shapes-ts-terrabase-bench.terrascale.tech`, enabled, ownership and SSL active, minimum TLS 1.2 |
| Benchmark managed `r2.dev` access | Disabled |
| Benchmark lifecycle | Only an incomplete-multipart abort after 604800 seconds; no object expiry rule |
| Zone WAF | Enabled shape-only GET/HEAD rule; rejects queries, non-shape paths, `..` and raw percent escapes |
| Zone cache ruleset | Listed: `c2e48e2abf5b4dca89cb6d5009a3839d`, version 2; body GET returned 403 twice, so current actions are unverified |
| Smart Tiered Cache | `tiered_cache_smart_topology_enable`: `on` |
| Existing production bucket | `terrascale-gru-terrabase` exists, with active `shapes-terrascale-gru-terrabase.terrascale.tech` domain; contents and consumers were not inspected |

Reproduce without mutating resources:

```sh
ssh delta 'cf r2 buckets list --name-contains terrabase'
ssh delta 'cf r2 buckets get ts-terrabase-bench'
ssh delta 'cf r2 buckets domains custom list --bucket-name ts-terrabase-bench'
ssh delta 'cf r2 buckets domains managed list --bucket-name ts-terrabase-bench'
ssh delta 'cf r2 buckets lifecycle get ts-terrabase-bench'
ssh delta 'cf rulesets list --zone terrascale.tech'
ssh delta 'cf rulesets get e63dd3e752a547f48bbb0ee49b76767c --zone terrascale.tech'
ssh delta 'cf rulesets get c2e48e2abf5b4dca89cb6d5009a3839d --zone terrascale.tech'
ssh delta 'cf cache settings smart-tiered-cache get --zone terrascale.tech'
```

The benchmark used interim S3 keys derived from the CLI token. Its live key
policy was not inspected and must not be copied to production.

## F6 resource contract

| Resource | Decision |
| --- | --- |
| Bucket | `ts-terrabase-shapes-prod`, dedicated to TBS1 shape ciphertext |
| Storage class | Standard; no Infrequent Access transition |
| Location hint | `enam` (ENAM via S3), matching the measured bucket |
| Jurisdiction | Default; no residency guarantee |
| S3 endpoint / signing region | `https://<account-id>.r2.cloudflarestorage.com` / `auto` |
| Public custom domain | `shapes-ts-terrabase-shapes-prod.terrascale.tech` in zone `terrascale.tech` |
| Object URL | `https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/<handle>/<sha256>` |
| TLS | Active certificate, minimum TLS 1.2; HTTPS in every emitted URL |
| Managed public access | `r2.dev` disabled; no alternate public route |

DECISION: one shared bucket for all serving regions. Reason: immutable,
home-confirmed objects have one canonical URL and cache key; extra buckets
would fragment the cache. The authoritative publisher owns each handle;
regional readers never publish provisional overlays to R2. Handle identity
includes a new incarnation after restore or failover; never reuse an old URL
for different bytes. SHA-256 covers the exact ciphertext.

ENAM is a best-effort placement hint, not a region or latency guarantee.
[R2's supported hints](https://developers.cloudflare.com/r2/reference/data-location/)
do not include South America. A hint is only honored on first creation of a
bucket name. Record actual placement in F6's evidence; do not delete/recreate
a bucket to try to move it. All gru/sjc/fra nodes serve automatically.

Use a dedicated shape store boundary instead of redirecting `BackupSettings`
to R2: the existing shape mirror shares backup storage plumbing. F6 must
separate those clients and identities before switching shapes; backup/PITR
stays on Tigris. Derive the endpoint/account and bucket from production
identity in code, not new operator tuning switches. Leave existing serving
resources untouched until consumers move and the cutover proves healthy
(D24); obsolete-resource deletion belongs to C6.

## Zone delivery and cache policy

Inspect the existing rules with a suitably scoped deployment identity before
applying changes. Preserve unrelated rules. The 403 above is an F6 inspection
prerequisite, not evidence that the cache actions are correct.

1. Attach the dedicated bucket's custom domain through R2; wait for active
   ownership and SSL. Reuse the zone's WAF guard: for `shapes-*` hosts allow
   only GET/HEAD of `/v1/shapes/…`, no query, `..`, or raw `%` in the path.
   Reject other methods and paths before cache lookup. Never store plaintext,
   keys, manifests, catalog records, backups or logs in this bucket.
2. In phase `http_request_cache_settings`, make the shape GET/HEAD paths
   cache-eligible, including extensionless SHA-256 names. Match hosts ending
   `.terrascale.tech` and starting `shapes-`, path starting `/v1/shapes/`,
   empty query, methods GET/HEAD. Edge and browser TTL respect origin
   `Cache-Control`; PUT uses `public, max-age=31536000, immutable`.
   Explicitly prevent storage of 400–599 responses (status TTL `-1`,
   meaning no-store). Verify that no later rule overrides this policy.
3. Keep the standard URL cache key; no cookies, authorization, epoch or
   per-reader query variants. Public ciphertext GETs carry no auth header.
   Configure bucket CORS for browser GET/HEAD from `*`, without credentials;
   SDKs use simple GETs so the WAF need not admit OPTIONS. Verify browser
   downloads from two different browser origins, including cached responses,
   before claiming SDK support; the cached allow-origin value must be `*`.
4. Keep Smart Tiered Cache on for the zone. Use Cloudflare's native custom
   domain cache, not a Worker Cache API implementation. No Worker, Cache
   Reserve or paid Argo add-on is required by this design.
5. Manifests, key release and all live/provisional responses stay at the
   authenticated service, `private, no-store`. F4 supplies table-level grants;
   every key release rechecks current authorization and deletion fences.

A one-year cache TTL is not a retention or access grant. Ciphertext URLs do
not expire. `expires_at` on manifests is the next 10-minute epoch boundary;
clients refresh manifests then. Old epoch keys remain releasable only while
their objects are retained and the database, handle and grant are current.
Check size and hash, authenticate AES-GCM, then apply; failure refetches from
the authenticated origin. A stale handle or offset below the floor gets
`409 must_refetch`, never a silently incomplete history.

## Credentials and OCI Vault delivery

F6 creates an **account-owned R2 API token**, Object Read & Write, scoped only
to `ts-terrabase-shapes-prod`. Its policy resource is
`com.cloudflare.edge.r2.bucket.<account-id>_default_ts-terrabase-shapes-prod`.
Resolve permission-group IDs from the live API; no account-wide storage
admin, other buckets, token-management or zone-edit rights. The S3 key must
support PUT/GET/HEAD/LIST/DELETE in that bucket and be denied on CI, bench,
backup and customer-file buckets. Do not derive it from an operator CLI token.

Store the issued material once in **OCI Vault `gru-vault`**, under these names:

| Vault secret name | Purpose |
| --- | --- |
| `gru-1-terrabase-shapes-access-key-id` | Bucket-scoped S3 access key ID |
| `gru-1-terrabase-shapes-secret-access-key` | Corresponding S3 secret |
| `gru-1-terrabase-shapes-cdn-api-token` | Separate zone-scoped Cache Purge and Analytics Read API token |

The CDN token is scoped to `terrascale.tech`, cannot administer R2, and never
reaches clients. Deployment uses separate privileged credentials to create
buckets/domains/rules and inspect cache settings; those credentials are not
runtime credentials. The CDN token's zone-wide purge authority is a
Cloudflare permission boundary; application validation restricts queued URLs
to this production host. Neither token is a data encryption key.

ts-orch delivers these same Vault references to each serving region using
instance principals and its private runtime secret mount. No secret copies
in source, shell history, logs, PRs, env files or a second secret store.
F6 records exact ts-orch delivery bindings without adding feature flags,
including explicit permission to read the regional `gru-vault` from sjc/fra;
prove delivery there rather than assuming Vault is globally replicated.
Missing/invalid shape credentials fail closed and report unavailable; never
silently use backup keys or presigned direct-bucket links.

Set token expiry to 90 days, rotate by day 60. Write new Vault versions,
roll through all regions, verify new-key operations and purge/analytics,
then revoke old tokens. Alert at 30 and 7 days before expiry. Record token
IDs and expiry as non-secret operational metadata. Failures do not justify
temporarily broadening scope or reusing the CLI identity.

## Retention, purge and deletion

Retain sealed objects in manifests for at most seven days; trim sooner below the shape
floor or after handle invalidation. The hourly sweeper removes orphans and
queues purge; the operational cleanup target is age seven days plus one
sweep interval, subject to service availability. Use an eight-day R2 object
lifecycle expiry on `v1/shapes/` as a backstop, allowing the normal sweep to
persist purge work first, and abort incomplete multipart uploads after
one day. Lifecycle expiry is asynchronous and does not purge CDN copies;
the application must persist purge work before expiry can remove the object.
Align F5's CDC floor with shape replay and backup positions. Keep keys for
retained, live objects; never resurrect an expired object under its old URL.

The existing `ShapeCdnPurge` has a volatile 4,096-URL queue, batches of 100,
five attempts and metrics for purged/failed/dropped. Queue overflow or restart
can lose work. F6 must make purge work durable rather than asserting that
turning on this worker guarantees cleanup:

1. Persist an idempotent outbox record (object key, canonical URL, database
   incarnation, reason, attempt and next-attempt time) in TerraBase's internal
   SQL catalog before issuing R2 DELETE. No Cloudflare Queue or local file
   store. Keep records outside the database being deleted.
2. Fence publication, remove the object from retained manifests, DELETE R2
   idempotently, then purge its full URL through
   `POST /zones/4cc466e19478c116fae7dc445a6d0911/purge_cache` with `files`.
   Persist progress at each stage; crash recovery replays unfinished work.
3. Bound each worker's in-memory window to 4,096 URLs / 1 MiB and batch at
   most 100 URLs. Deduplicate by URL; use a leased consumer and account-wide
   pacing of at most 100 URLs/second. Honor `Retry-After`; transient failures
   use jittered backoff up to one minute, five attempts per drain cycle.
4. On exhaustion leave work pending for the next cycle. On non-429 4xx
   park it durably and alert for credential/policy repair; never drop it.
   Acknowledge completion only after API `success: true`. Enforce catalog
   storage admission; saturation blocks new mirror publication with a visible
   error, while deletion fencing/key denial can proceed. Track pending count,
   oldest age, retry/parked failures and existing purge outcomes. Alert on
   parked work or oldest age over one hour; use bounded metric labels.

Deletion is not secured by a purge acknowledgement. F11 implements D19:

- Commit a database/tenant deletion fence and deny new manifests, key release,
  writes and publication on every node before acknowledging deletion. A node
  unable to establish fresh deletion state must fail closed; a partitioned
  node must not keep releasing keys from its last catalog copy.
- Destroy every affected shape epoch key **and every ancestor that can derive
  it**, and the database at-rest keys, including cached/unwrapped copies.
  Current `ShapeCrypto` derives epoch keys from project data keys: deleting
  epoch records alone is insufficient, and destroying a shared project key
  would break surviving databases. F11 needs independently erasable
  per-database key custody; no shared-master derivation can recreate a deleted
  database's secret.
- Historical wrapped keys in snapshots, key sidecars, PITR, replicas and Vault
  versions must not be recoverable under a surviving shared master. E8/F11
  must provide an independently destroyed per-database wrapping root and a
  non-resurrecting deletion ledger checked by restore. A tombstone alone is
  access denial, not cryptographic erasure. Establish this custody before
  claiming D19; OCI secret deletion scheduling alone is not immediate erasure.
  Deletion has no undo grace: the affected database's backup/PITR decryption
  roots must also be erased, without affecting any surviving database.
- Enumerate the deleted scope's objects into the outbox, then remove R2
  objects and purge asynchronously. Retain the deletion ledger across restore
  and failover. Crypto destruction and cleanup failures remain visible until
  repaired; no success response while required key destruction is incomplete.

Security boundary: neither erasure nor purge can revoke plaintext or epoch
keys already downloaded by an authorized client. D19 prevents **new** recovery
using service-held keys after successful deletion. Cached ciphertext may
outlive seven days and even an R2 delete, up to the cache TTL or eviction;
seven days is an object-retention policy, not a maximum CDN lifetime.
F6 may provision before F11, but customer rollout must not claim or depend on
immediate crypto-shredding until F11's integration proof passes.

## Monthly cost model (USD, estimates)

[Cloudflare pricing](https://developers.cloudflare.com/r2/pricing/), checked
2026-10-29: Standard $0.015/GB-month; Class A $4.50/million; Class B
$0.36/million; egress $0. Free per account/month: 10 GB-month, 1M Class A,
10M Class B. Round billable storage up to GB-month and operations up to
million-request units. Standard has no minimum duration or retrieval charge;
Infrequent Access has a 30-day minimum and retrieval charges, unsuitable here.

Here TB means 1,000 GB, held at the stated average daily peak for a full
30-day month. Reads are ciphertext object GETs, not rows or shape sessions.
Assume the free allowance is available and exclude Class A from totals below.
Let `R` be client reads, `B` actual R2 Class B operations, `A` Class A operations:

```text
storage = 0.015 * ceil(max(GB_month - 10, 0))
class_B = 0.36 * ceil(max(B - 10,000,000, 0) / 1,000,000)
class_A = 4.50 * ceil(max(A - 1,000,000, 0) / 1,000,000)
total_R2 = storage + class_B + class_A
```

For a read-only illustration, `B = R` means no cache savings; at the measured
0.999 hit ratio `B = 0.001 * R`. These are workload assumptions, not a promise.

| Stored | Storage | 1M reads, no cache savings | 100M reads, no cache savings | 1M reads, 0.999 hit ratio | 100M reads, 0.999 hit ratio |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 TB | $14.85 | $14.85 | $47.25 | $14.85 | $14.85 |
| 10 TB | $149.85 | $149.85 | $182.25 | $149.85 | $149.85 |
| 100 TB | $1,499.85 | $1,499.85 | $1,532.25 | $1,499.85 | $1,499.85 |

1M and 100M reads at 0.999 hits mean 1,000 and 100,000 bucket GETs;
100M uncached reads cost $32.40 in Class B after the allowance. HEADs,
metadata probes and revalidation also consume Class B. Do not bill CDN hits
as R2 GETs. Tiered Cache may reduce misses further, but budget from actual
R2 counters, not just edge MISS counts.

Storage size alone cannot determine Class A cost. At a steady seven-day
retention with 1 MB ciphertext objects and full turnover, 1/10/100 TB implies
about 4.286M/42.857M/428.571M PUTs per 30 days: $18/$189/$1,926 Class A
after rounding/free allowance, before LISTs or multipart overhead. Add these
to the table for that workload. Object size and turnover must be measured;
smaller objects can dominate costs. An hourly full sweep at 100 TB / 1 MB
objects adds roughly 72M LIST pages/month at 1,000 objects/page (about $324
before shared free allowance/rounding). Key lookup must use catalog indexes,
not per-database bucket listings. DELETE is free; sweep LISTs are Class A.

Free allowances are shared with CI, bench and other buckets. With none left,
storage is $15/$150/$1,500 and 1M/100M uncached GETs add $0.36/$36;
1,000–100,000 GETs round to $0.36. Excluded: origin compute and control-path
traffic, Tigris backup/PITR, OCI/Vault, tax, existing zone plan and any paid
Cloudflare add-ons. Native R2 delivery and Smart Tiered Cache introduce no
Worker request charge in this design. P6 owns pricing and spend enforcement;
Q6 alerts on actual storage, Class A/B, cache hit ratio and purge backlog.

## F6 execution order and proof

1. Re-read live configuration with an identity allowed to inspect cache
   actions; record rule IDs, precedence and actual placement. Discover exact
   mutation syntax with `cf cli search` and `cf schema`; this document is
   not a copy/paste provisioning script.
2. Separate shape storage from backup storage; create the dedicated bucket,
   lifecycle, CORS, scoped identities and Vault references. Attach domain;
   validate the zone WAF/cache rules and Smart Tiered Cache before publishing.
   Prove Vault delivery and credential rotation in gru/sjc/fra, and host-filtered
   CDN analytics access with the runtime token. Confirm current account purge
   limits; CI/bench share that account and must share the rate budget.
3. Verify from real serving code: ciphertext PUT metadata, canonical manifest
   URL, browser GET, cold MISS then HIT and TLS. Verify denied non-shape paths,
   query variants, unsupported methods and a missing object that does not
   become a pinned cached 404. Validate cross-bucket credential denial.
4. Fast integration remains a required CI gate on the exact PR head. Run the
   independent Slow real-R2 scenario for CDN HIT, durable purge through
   restart/429/saturation/credential failure and eventual deletion, reporting
   failures through the existing automatic issue path. Slow remains
   non-blocking for merges; failed proof is still unfinished task evidence.
5. Repeat the N = 1/10/100/1000 scenario three times on SQL shapes (F10),
   compare client cache headers with R2 `r2OperationsAdaptiveGroups` GET
   counts over the same windows, and publish under `benchmark/bench/`.
   Reproduce one GET per object per location for a controlled cold fill;
   measure multiple locations separately before claiming a global bound.
6. F11 proves cached ciphertext cannot be newly decrypted after database or
   tenant deletion, including partitions, restart, historical key versions
   and backup restore, while a surviving database remains readable. Until
   then D19 is a launch prerequisite, not an implemented guarantee.

W0-7 changes no API, storage format, credentials or infrastructure. F6/F11
own the implementation and security qualification above. No visual change.
