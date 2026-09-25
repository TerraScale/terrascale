# Proposed first packaged document caller: local-v1 HTTPS/JSON

Status: **source-backed engineering recommendation; local-v1 HTTPS/JSON is
implemented in the current source tree, while the public contract remains
provisional**. This resolves routine defaults left open in
[server-http-json-api-key-provisional.md](server-http-json-api-key-provisional.md).
The coordinator accepted HTTPS/JSON with scoped API keys as the planning
direction. The source snapshot and seam assessment below are historical; the
runtime and process gates there describe the proposed acceptance checklist, not
the current implementation status.

For a fresh, single-shard local exercise, see the
[local-v1 HTTPS quickstart](#local-v1-https-quickstart). Its packaged-process
path is runtime-verified; the literal shell sequence remains unexecuted.

## Local-v1 HTTPS quickstart

This source-backed example initializes one fresh, fixed-scope `local_durable`
shard with one collection, starts its loopback HTTPS/JSON listener, commits and
reads a typed document, then restarts against the same persisted data. It does
not exercise online distributed HTTP or claim `AppliedAck` behavior. The
literal Bash commands below have only been syntax-checked, not executed. Run
them from the repository root; they require `make`, OpenSSL, `jq`, and `curl`.

The r4b Firecracker Fast guest run passed all 21 tests (0 failed, 0 ignored;
guest 15,737 ms, controller 25,235 ms). It passed
`server_fast::client_process::packaged_initializer_bootstraps_fresh_local_v1_for_https_document_caller`,
which exercised fresh initialization, a scoped HTTPS commit/read, and restart
with a persisted read; the packaged typed-commit/retry-after-restart test also
passed. Existing distributed `point_read` tests in that Fast suite passed too.
This verifies those packaged test paths, not the literal commands above; the
isolated server document-apply integration remains unbuilt. The run used source
manifest SHA-256 `505a44b228d7a14dafcd8544b03fd7d38b5f4c1eb46d5eb1d4c6662c180b91a3`,
runner SHA-256 `bb61a354a9deb49e3d1ef09c74f459af85cb65034d835d0be805441cecd26d04`,
and packaged `client_process`, `tb-server`, `terrabase-init`, and
`terrabase-keygen` payloads. The packet is under
`../ts-benchmark/bench/firecracker-harness/terrabase-fast-20260925-r4b-env/`;
run-manifest SHA-256 is
`e6dbdc3a879ab09251565b4cc519116ecd136c442532e72c9e16ecd58ba70b2a`, and the
guest archive SHA-256 is
`d94cab5d58b4aa81d0cda63b33ef1ed95a5f03f9c32eb81b16e23198294eaf20`. The
guest cleanup receipt (SHA-256
`1fbf4d872c22db06b4a133f728c6c63f2eef697284e71424d8883ba3c522fa5d`) records
no timeout, OOM, or remaining child processes and confirms cgroup removal.

The earlier r3 guest baseline passed 20 tests with zero failures or ignored
tests (manifest SHA-256
`384c14d45f200cfb1289b2f6aded6b36d700d5b6fac919c44ca3f38c16fd6b70`), but its
frozen source inventory omitted `terrabase-init.rs` and `distributed_read.rs`;
it did not verify the initializer-to-HTTPS path.

Build through the Makefile, which lowers CPU priority for compilation:

```bash
make build
```

Run the remaining commands in one Bash shell so the temporary key stays only
in shell memory. The setup, TLS, registry, and shard files live under a protected
temporary directory outside the checkout. The two memory values below match
process-test fixtures; they are example values, not production sizing guidance.

```bash
set -euo pipefail
umask 077
BIN="$PWD/target/x86_64-unknown-linux-musl/debug"
TB_DEMO="$(mktemp -d)"
TB_DEMO="$(cd "$TB_DEMO" && pwd -P)"

TENANT=01ARZ3NDEKTSV4RRFFQ69G5FAV
DATABASE=01ARZ3NDEKTSV4RRFFQ69G5FAW
SHARD=7
COLLECTION=01ARZ3NDEKTSV4RRFFQ69G5FAX
DOCUMENT=01ARZ3NDEKTSV4RRFFQ69G5FAY

cat > "$TB_DEMO/setup.json" <<JSON
{
  "version": 1,
  "tenant": "$TENANT",
  "database": "$DATABASE",
  "shard": "$SHARD",
  "collections": [{
    "collection": "$COLLECTION",
    "fields": [{
      "field_id": 1,
      "parent_field_id": 0,
      "value_type": "Utf8",
      "element_type": null,
      "required": false,
      "nullable": false,
      "array_element_nullable": false,
      "name": "message"
    }]
  }]
}
JSON

"$BIN/terrabase-init" \
  --input "$TB_DEMO/setup.json" \
  --local-v1-config "$TB_DEMO/local-v1.json" \
  --data-dir "$TB_DEMO/data" \
  --memory-limit-bytes 268435456 \
  --memory-headroom-bytes 16777216

openssl req -x509 -newkey rsa:2048 -nodes -days 30 \
  -keyout "$TB_DEMO/server-key.pem" \
  -out "$TB_DEMO/server.pem" \
  -subj '/CN=127.0.0.1' \
  -addext 'subjectAltName=IP:127.0.0.1' \
  -addext 'basicConstraints=critical,CA:TRUE' \
  -addext 'keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign,cRLSign' \
  -addext 'extendedKeyUsage=serverAuth'
chmod 600 "$TB_DEMO/server-key.pem"

# Capture keygen output in shell memory; the registry receives only its verifier.
KEYGEN_OUTPUT="$("$BIN/terrabase-keygen")"
mapfile -t KEYGEN_LINES <<< "$KEYGEN_OUTPUT"
unset KEYGEN_OUTPUT
API_KEY="${KEYGEN_LINES[0]#API key (copy once): }"
KEY_ID="$(jq -r '.id' <<< "${KEYGEN_LINES[2]}")"
KEY_VERIFIER="$(jq -r '.verifier_sha256' <<< "${KEYGEN_LINES[2]}")"
printf '%s\n' "${KEYGEN_LINES[0]}"
unset KEYGEN_LINES

jq -n \
  --arg id "$KEY_ID" --arg verifier "$KEY_VERIFIER" \
  --arg tenant "$TENANT" --arg database "$DATABASE" \
  --arg shard "$SHARD" --arg collection "$COLLECTION" \
  '{version:1,keys:[{id:$id,verifier_sha256:$verifier,tenant:$tenant,
    database:$database,shard:$shard,collections:[$collection],
    permissions:["read","write"],revoked:false}]}' \
  > "$TB_DEMO/api-keys.json"

jq -n \
  --arg cert "$TB_DEMO/server.pem" \
  --arg key "$TB_DEMO/server-key.pem" \
  --arg registry "$TB_DEMO/api-keys.json" \
  '{version:1,listen_addr:"127.0.0.1:9465",certificate_chain:$cert,
    private_key:$key,api_key_registry:$registry,handshake_timeout_ms:5000}' \
  > "$TB_DEMO/client.json"
chmod 600 "$TB_DEMO/api-keys.json" "$TB_DEMO/client.json"

SCHEMA_GENERATION="$(jq -r '.collections[0].generation' "$TB_DEMO/local-v1.json")"
SCHEMA_DIGEST="$(jq -r '.collections[0].digest' "$TB_DEMO/local-v1.json")"
EXPIRES_AT="$(( $(date +%s) * 1000 + 3600000 ))"
NONCE="$(openssl rand -hex 16)"
jq -n \
  --arg collection "$COLLECTION" --arg document "$DOCUMENT" \
  --arg generation "$SCHEMA_GENERATION" --arg digest "$SCHEMA_DIGEST" \
  --arg expires "$EXPIRES_AT" --arg nonce "$NONCE" \
  '{version:1,transaction_id:{expires_at_unix_ms:$expires,nonce:$nonce},
    writes:[{collection:$collection,document:$document,
      schema_generation:$generation,schema_digest:$digest,observed:[],
      value:{kind:"live",fields:[{id:1,value:{type:"utf8",
        value:"hello from the local quickstart"}}]}}]}' \
  > "$TB_DEMO/commit.json"

TX_URL="https://127.0.0.1:9465/v1/tenants/$TENANT/databases/$DATABASE/shards/$SHARD/transactions"
READ_URL="https://127.0.0.1:9465/v1/tenants/$TENANT/databases/$DATABASE/shards/$SHARD/collections/$COLLECTION/documents/$DOCUMENT"

# curl reads the Authorization header from a pipe, not its process arguments.
curl_keyed() {
  curl --cacert "$TB_DEMO/server.pem" \
    --config <(printf 'header = "Authorization: Bearer %s"\n' "$API_KEY") \
    "$@"
}

start_server() {
  nice -n 19 "$BIN/terrabase-server" \
    --data-dir "$TB_DEMO/data" \
    --memory-limit-bytes 268435456 \
    --memory-headroom-bytes 16777216 \
    --port 9464 \
    --local-v1-config "$TB_DEMO/local-v1.json" \
    --client-config "$TB_DEMO/client.json" \
    >> "$TB_DEMO/server.log" 2>&1 &
  SERVER_PID=$!
  for ((attempt=0; attempt<100; attempt++)); do
    if curl --fail --silent http://127.0.0.1:9464/readyz >/dev/null; then
      return 0
    fi
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
      cat "$TB_DEMO/server.log"
      return 1
    fi
    sleep 0.1
  done
  cat "$TB_DEMO/server.log"
  return 1
}

start_server
curl_keyed --fail --silent --show-error \
  --header 'Content-Type: application/json' \
  --data-binary "@$TB_DEMO/commit.json" "$TX_URL" | jq .
curl_keyed --fail --silent --show-error "$READ_URL" | jq .

# Stop and relaunch with the same data directory, then read the persisted value.
kill -TERM "$SERVER_PID"
wait "$SERVER_PID" || true
start_server
curl_keyed --fail --silent --show-error "$READ_URL" | jq .
```

The registry stores only the key verifier and its one-collection `read`/`write`
grant. Keep the printed bearer key private. The initializer is fresh-only; keep
`$TB_DEMO/data` and the generated local-v1 config together for restarts. The
server remains running at the end of the sequence; stop it with
`kill -TERM "$SERVER_PID"` when finished. The commit response is
`local_durable`, which describes this one local shard only.

## Earlier source evidence (historical)

Repository HEAD is `352d9f61185d1b685e3032519579040d169ad47d`.
SHA-256 values identify the inspected uncommitted tree on 2026-09-24; refresh
moving source before implementation.

| Path | SHA-256 | Relevant evidence |
| --- | --- | --- |
| `crates/tb-server/src/document_service.rs` | `d077145e8177bfa3dadc150f58d56286960fc01c15711ea599030644fa5ab121` | crate-private `LocalV1DocumentService`, fixed `LocalV1Scope`, typed point/commit and local-only result accessors; no API-key grant |
| `crates/tb-server/src/main.rs` | `deceef23ccc86c47a9f070621a9e2d1ca7da0eb2c055f35cfbfc6893c6761c16` | binds admin/member sockets before shard open, then always opens raw `LocalServerState` |
| `crates/tb-server/src/state.rs` | `0735ff2833aa7049fcc29b56adb1e640651c751c75d2c167f9e1074c7f453227` | raw `Storage` owner and shared readiness/budget |
| `crates/tb-server/src/http.rs` | `14d4bcf9429bda3212228111e82191ccb9ca2d6c5ef32c464212656121d69334` | loopback admin-only routes, 32 sockets/16 handlers, 16-KiB/32-header/5-second header bounds |
| `crates/tb-core/src/types.rs` | `83e8e9a3322142bf8c604dda81ebfc440a4ce7d8186d0abc965e5318026b2a0d` | canonical uppercase ULIDs; `TransactionId` expiry and nonce; local receipt |
| `crates/tb-format/src/document.rs` | `f7fa01957ebd841a8adc3c86a5fb8f7cdf19f5cbce6a3d86cbb127e38279d84e` | field IDs/types, immutable binding, typed document decode |
| `crates/tb-replication/src/event.rs` | `56576aae259b07ca806428ab6da70412ba1549be5cdc7ecc4cf85a4dcad77667` | sorted bounded causal context and event limits |
| `crates/tb-storage/src/local.rs` | `22d5ec284fa8c1d579ca3185e268ccd11556dd39a2beb0297f6cc57e99d33e73` | default maximum retry window is 24 hours |
| `crates/tb-document/src/lib.rs` | `fec608bcaebb630273eddb05671044ebf460e8a3f21678422a1644fc55f254f6` | `Absent`/`Deleted`/`Value`/`Conflict`, complete siblings, explicit producer errors |
| `crates/tb-document/src/producer.rs` | `e9dc914a5979daf79402bdb78749c5b25d5855ca88cd9164f5d801a4bd04d9b3` | sorted writes, observed-prefix validation, local sync and uncertain commit |
| `crates/tb-server/tests/server_process.rs` | `b88e18255f3d9b642c3672062784a55274d05068af2a12c7d1d925f620719ad7` | packaged binary currently returns 404 for `/v1/documents` |
| `Cargo.toml` | `ce7d8778e5f690757d1cbff8829211b7268cc5630670ed937b2812ef2231d14a` | pinned Axum/Hyper/Tower, rustls, serde and reqwest stack |

The service is exported only within `tb-server` and called only by its own
tests. Making its constructor public would expose a premature library contract
without making the packaged server useful. The next accepted slice should wire
this same owner into a real local-v1 process mode and the authenticated client
listener, rather than suppress unused-code warnings or open a second shard.
The existing member replication process is not a certified document read/apply
mode; do not advertise document HTTP there.

## Exact first wire recommendation

Use direct TLS 1.3 with HTTP/1.1 and `Content-Type: application/json`; one
request per connection in this first bounded profile. Require a normally
verified server certificate even on loopback. Do not trust forwarded identity
headers, enable CORS, accept URL keys, or accept request compression. Configure
an explicit client mode to listen on `127.0.0.1:9465` by default; a concrete
nonloopback address additionally requires `--client-allow-nonloopback`. The
admin `:9464` and member gRPC/LZ4 listeners remain separate. Admit a socket
slot before TLS accept and enforce a five-second TLS handshake deadline,
configurable from one through ten seconds with ten seconds as the hard cap.
Release the slot on timeout, handshake error, rotation or shutdown; rotation
and drain close sockets that have not completed a handshake.

The only document routes are:

| Operation | Route | Result |
| --- | --- | --- |
| Point read | `GET /v1/tenants/{tenant}/databases/{database}/shards/{shard}/collections/{collection}/documents/{document}` | HTTP 200 typed point, including `absent`; no body |
| One-shard atomic commit | `POST /v1/tenants/{tenant}/databases/{database}/shards/{shard}/transactions` | HTTP 200 local-durable result, identical on exact token retry |

All ULIDs are uppercase canonical 26-character strings parsed with the core
conversion. `{shard}` and every JSON `u64` are canonical decimal strings
(`0` or a nonzero first digit, no sign); `field_id` is a JSON integer in
`1..=u32::MAX`. Digests and 16-byte incarnation/nonces are lowercase fixed-
width hex. Reject unknown/duplicate object members and noncanonical encodings
in typed request decoders. A valid missing document is `200` with
`{"version":1,"state":"absent","context":[],"siblings":[]}`; `404` means
unknown route only.

The commit body is exactly an object with `version:1`, `transaction_id` and
`writes`. `transaction_id` is
`{"expires_at_unix_ms":"<u64>","nonce":"<32 lowercase hex>"}`;
the client generates one random nonce and reuses the whole ID and request on
retry. Admit its expiry only within the storage profile's configured maximum
retry window (currently 24 hours). `writes` is a nonempty strictly increasing array by
`(CollectionId,DocumentId)`, each item
`{"collection":"<ULID>","document":"<ULID>","schema_generation":"<u64 nonzero>","schema_digest":"<64 lowercase hex>","observed":[],"value":{"kind":"tombstone"}}`
or the same item with `value:{"kind":"live","fields":[...]}`.
`observed` is an array of unique, sorted
`{"replica":"<ULID>","incarnation":"<32 lowercase hex>","sequence":"<u64 nonzero>"}`
entries. It is an explicit causal assertion, not an authorization token;
`DocumentStore` rejects prefixes beyond known history. Empty observed context
on an existing document intentionally retains unseen siblings as conflicts.

`fields` is a strictly field-ID-sorted array of
`{"id":<u32>,"value":<tagged value>}`. A tagged value is exactly one of
`{"type":"null"}`, `{"type":"bool","value":true|false}`,
`{"type":"i64","value":"<canonical signed decimal>"}`,
`{"type":"utf8","value":"<string>"}`,
`{"type":"bytes","value":"<unpadded base64url>"}`,
`{"type":"map","fields":[...]}`, or
`{"type":"array","items":[<tagged value>,...]}`. Canonical `i64` has no
leading zeros or negative zero and must fit signed 64-bit exactly. Missing is
an omitted field, while Null has the explicit `null` tag. The existing schema
and format decoder decide required fields, nullability, homogeneous arrays,
nesting and size; the adapter must not invent a second schema validator.
The format crate needs a bounded borrowed field/nested-value iterator for
encoding read JSON; `DocumentView::field(id)` alone does not enumerate fields.

Every read returns `version`, `state` (`absent`, `deleted`, `value`, or
`conflict`), `context`, and `siblings` in canonical event order. The latter
contains every visible sibling, including tombstones. Each sibling has
`event:{database,shard,replica,incarnation,sequence}`,
`schema_generation`, `schema_digest`, `kind` (`live` or `tombstone`), and
`fields` only for live values, using the same tagged-value grammar. `absent`
has zero siblings, `deleted`/`value` one, and `conflict` at least two. A
commit result is exactly `version`, `durability:"local_durable"`,
`transaction_id`, `event`, and `commit_sequence` (decimal string). Project
only these local-safe fields from `LocalDurableCommit`; never serialize the
generic `CommitReceipt` or its received/applied frontiers. Preserve the
original transaction ID, event and commit sequence on an exact retry.

Errors use `{"error":{"code":"<stable_code>"}}` with no engine path,
secret, sibling or other tenant detail. Use `401 invalid_key` for missing,
unknown, invalid and revoked credentials; `403 forbidden_scope` for a valid
key outside its grant (same result regardless of document existence);
`400 invalid_request` for malformed typed input, including unsupported or
out-of-range numbers, write order and causal context;
`409 schema_changed`, `idempotency_conflict` or `idempotency_expired` for
conflicts; `429 rate_limited`; `503 capacity` or `commit_uncertain` for
backpressure/uncertain write. `CommitUncertain` and `NeedsRecovery` map
to `commit_uncertain`, close data readiness, and instruct same-ID retry after
recovery. The service maps producer `InvalidWriteOrder` and
`InvalidCausalContext` to a typed internal `Validation` variant; keep those
causes available for tests/diagnostics while exposing only `invalid_request`.
Do not infer error kinds by parsing strings.
No raw `DocumentError::to_string()` becomes an HTTP message.

## Exact key, admission and rotation recommendation

Generate tokens with the operating-system CSPRNG in a Rust operator command
such as a dedicated `crates/tb-server/src/bin/terrabase-keygen.rs`; show the
raw token only once to the operator and emit a verifier-entry template:
`tbk1.<22 base64url chars for random 16-byte ID>.<43 base64url chars for
random 32-byte secret>`. The `Authorization` value is exactly `Bearer <token>`
under a 128-byte header cap. Store the public ID and
`SHA-256("TB-APIKEY-V1\0" || ID[16] || secret[32])` as lowercase hex, never
the secret. A registry v1 JSON file is
`{"version":1,"keys":[...]}` at top level. Each key contains `id`,
`verifier_sha256`, `tenant`, `database`, `shard`, sorted unique
`collections`, `permissions` (nonempty sorted subset of `read`,`write`),
`revoked` (explicit boolean), and optional `rate_per_second`, `burst`,
`max_in_flight` using the defaults below when absent. Reject unknown
fields, duplicate IDs, noncanonical values, foreign shard grants and any
allowlist collection outside the configured `LocalV1Scope` at startup/reload.
Cap the file at 256 KiB, 128 keys and 4,096 total collection grants. A
client-mode start requires the TLS chain, private key and verifier-registry
paths; no material is supplied in arguments or environment variables.

Use SHA-256 only because keys have 256 bits of generated secret entropy;
human passwords require a different design. Compare verifier bytes with
`subtle` in constant time, return the same `401` for all invalid keys, and
never log a token, verifier, or `Authorization` header. Resolve the key grant
before JSON body collection, then check exact tenant/database/shard,
operation and every collection before calling the service. Recheck the grant
immediately before a queued operation enters the document worker: keep only
the authenticated key ID and verifier digest in the queued context, look up
the current active entry, and re-evaluate its current scope and permission.
No raw bearer secret needs to remain in the queue.
The server's fixed scope is a second constraint, not a replacement for these
per-key checks.

Default each key to **5 requests/second, burst 10, two concurrent admitted
requests**, sharing the rate across reads and commits. Permit explicit
`rate_per_second` 1..=100, `burst` 1..=200 and `max_in_flight` 1..=4 in the
registry. These defaults are deliberately below the first listener's four
global handlers while permitting a short read/commit burst; operators can
raise them within the fixed caps after measuring the real workload. The client
listener additionally caps eight sockets, four handlers,
a 16-KiB/32-header block and five-second header read, a 4-MiB JSON request,
a 4-MiB encoded response, and finite body/write deadlines. Apply one global
20/second, burst-40 limiter to unknown-key attempts; the socket/handler caps
also bound pre-auth work. `governor 0.10.4` direct limiters are a reviewed
candidate: keep one direct limiter and semaphore *inside each validated
registry entry*, so state cardinality is bounded by 128 keys and no attacker-
keyed map is created. `Quota::per_second`, `allow_burst`,
`RateLimiter::direct` and `check` were confirmed in the downloaded crate
source (`quota.rs` SHA-256 `05802283dc59b1f32b479f276d4266b99c35e43f4c0323da3b9904f65c7085e1`;
`state/direct.rs` SHA-256 `80116d492a5d8780c2004241da74c265e8d50c3b4516292e9c9ec80413783e9e`).
Pin only after the workspace toolchain/features compile; no manifest edit is
part of this packet. The lockfile already contains `tokio-rustls 0.26.5`,
`subtle 2.6.1`, `base64 0.22.1` and `getrandom 0.4.3` transitively; promote
the exact needed versions/features to direct dependencies during integration.

An operator rotates a key by atomically replacing the registry with old and
new active entries, signaling `SIGHUP` reload, distributing the new token,
then atomically replacing it without the old entry and signaling reload again.
Removal or `revoked:true` denies **new admissions after the successful swap**;
already admitted calls can finish, including a commit past its sync boundary.
Require a current-generation check after any queue wait before starting work.
TLS chain/key rotate in the same validated reload generation. An invalid or
unreadable replacement closes client readiness and new client admissions;
the admin diagnostics remain up. Never continue accepting under an old grant
after a failed revocation reload. One request per connection and bounded drain
make the old TLS generation finite. Restart reads the persisted replacement
files. Keep `/readyz` false while configured client mode is unavailable.

## Runtime ownership and finite acceptance gates

1. **Mode composition and schema preflight — server integration owner.** Own
   `crates/tb-server/src/{config,state,main,lib}.rs`, server/workspace manifest
   edits, and proposed new `local_v1_runtime.rs`/worker module; coordinate
   changes to `document_service.rs` with
   its current owner. For the packaged first slice, require
   `--local-v1-config <path>` and `--client-config <path>` together, so the
   service has a real caller. The trusted local config pins tenant/database/
   shard, a stable origin/incarnation across restart, local epoch/configuration,
   and every allowed collection's expected active generation/digest. Default
   mode remains today's raw shard and admin
   behavior, including the existing member-replication path. Local-v1 mode
   owns exactly one `DocumentStore` via `LocalV1DocumentService` under the
   same process budget/lifecycle, with no second raw open. Reject combining
   local-v1 with current member-replication options. Do not enable a
   certified-distributed document client mode until source/apply/visible-read
   gates pass. Bind and validate client/admin sockets and auth/TLS material
   before opening a shard; mark readiness only after the configured local
   catalog's active immutable `(collection,generation,digest)` bindings are
   verified. The trusted bootstrap must leave a durable local-v1 mode/identity
   marker and the expected catalog; startup rejects missing/mismatched markers,
   populated raw directories and signed distributed directories before serving.
   Never treat absent document state as an implicit mode migration. For this
   first caller, use a preprovisioned local catalog from
   a trusted Rust fixture/operator step; schema creation is not an HTTP route.
   This depends on document/format ownership supplying a checked local-mode
   bootstrap/marker and read-only active-binding probe; the server must not
   inspect or write C0/C1 bytes independently.
   A bounded blocking worker or bounded blocking pool owns the synchronous
   service. Retain request/output permits through serialization and slow
   response write; never hold a shard lock across `await`. Expose a
   crate-private `DocumentWorkerHandle::execute(AuthorizedDocumentRequest)`
   returning a `PreparedClientResponse` that retains its output permit.
2. **HTTP/auth adapter — client-module owner after mode seam is ready.**
   Own proposed `client_auth.rs`, `client_http.rs` and the Rust key-generation
   command. Agree a crate-private interface with the integration owner:
   `AuthRegistry::{load_generation,authenticate,reauthorize}` yields a bounded
   `AuthenticatedKey` (ID and verifier digest); `client_http::serve` accepts a
   prebound listener, TLS generation, registry, worker handle and shutdown.
   The worker accepts only a currently authorized exact-scope read or commit
   with its memory permits. The integration owner alone wires the listener in
   `main.rs` and promotes dependencies in the manifests.
   Consume the crate-private service; leave `http.rs` admin-only and member
   `replication_server.rs` as gRPC/LZ4.
3. **Typed JSON and format walk — wire/format owner.** Own only proposed
   `crates/tb-server/src/client_json.rs`, `crates/tb-format/src/document.rs`
   and their focused unit tests. Add a bounded borrowed field/nested-value
   iterator rather than duplicating recursive `TBDOC001` parsing in the
   server. `client_json::decode_commit(&[u8]) -> ValidatedCommit` owns backing
   buffers and yields borrowed `DocumentWrite` inputs;
   `encode_point` preserves complete sibling/context output, and
   `encode_local_commit` consumes only `LocalDurableCommit`'s local-safe
   accessors. Keep typed validation causes internally; public validation is
   the single `400 invalid_request` code.
4. **Packaged HTTPS verification — test-file owner.** Own a new
   `crates/tb-server/tests/client_process.rs` with a preprovisioned local-v1
   fixture, verifying `reqwest` TLS client and actual binary. Prove raw mode
   still has document 404/admin metrics; local-v1 exact read, two-collection
   commit, restart/retry, typed Null/Missing/Bytes/I64 and complete conflicts;
   key scope/permission refusal before mutation; malformed/oversize/slow
   input and slow output bounded; rate and concurrency rejection; a stalled
   TLS handshake times out and releases its socket slot, while rotation/drain
   closes pre-handshake sockets; key/TLS
   rotation and revocation cut; invalid reload closes readiness; member mode
   still requires its own authority and admits no client document route.
   Check no token or unbounded identity labels in logs/Prometheus. Put any
   throughput benchmark only in `../ts-benchmark`.

The only genuine product/architecture items outside this first local caller
are an operator-facing schema create/migrate workflow, browser/proxy support
if wanted, and certified distributed client durability/read semantics. The
default direct HTTPS/JSON profile, key grammar, bounded rates and local-v1
error mapping are reversible engineering choices for coordinator acceptance;
the unanswered optional client preference need not stall this scoped slice.
