# Bounded local MongoDB → TerraBase acceptance

**Stop at preflight today.** MongoDB tools are absent, no disposable core endpoint is attested, and local
compiler/build contention is high. This is a prepared procedure, not evidence of a successful full-path run.
The adapter, original synthetic fixtures, README and PR body at `34f616ea` remain unchanged.

## 1. Check prerequisites before starting anything

The checked state at **2026-10-13 02:17 UTC** was:

| Prerequisite | Verified state |
| --- | --- |
| `mongod`, `mongoexport` | Missing from PATH; no cached MongoDB image in Podman inventory |
| `mongoimport` | Missing from PATH, separately rechecked during the review corrections |
| Container/tool availability | Docker absent; Podman 5.8.8 available; no pull, install or container started |
| Core | No `terrabase` in PATH; core checkout has Debug apphost/test output, with no verified build revision/auth fixture readiness |
| Contention | 16 CPUs; load averages 14.15/17.01/14.92; multiple active csc/VBCSCompiler/MSBuild processes; busiest compiler processes about 445% and 436% CPU |

Python 3.15.0, curl 8.22.0, .NET SDK 11.0.100-rc.1.26425.128, systemd 260.5 and `timeout` are available.
Availability does not establish that a user cgroup or core fixture is ready. No release build, CI job or
shared-service action is part of this procedure. Recheck before any later run:

```sh
command -v mongod mongoimport mongoexport systemd-run systemctl timeout
nice -n 19 mongod --version
nice -n 19 mongoimport --version
nice -n 19 mongoexport --version
getconf _NPROCESSORS_ONLN
cat /proc/loadavg
ps -eo ni,pcpu,comm --sort=-pcpu | head -20
```

Stop if a required executable is missing, version differs, a release build is active, or load is at least
half the logical CPU count. Do not download tools/images, install packages, restore NuGet, or build a core
to resolve the stop. Do not wait-loop for other work or stop/renice another task's processes.

[`pins.json`](pins.json) fixes this acceptance matrix: Community **mongod 8.0.12**, Apache-2.0 Database Tools
**mongoimport/mongoexport 100.13.0**, canonical Extended JSON **v2 JSONL**, adapter
`34f616ea313ddd20e91c2cf957da5ac9e5fa80a1`, core source `567bfd33fe1257a922c4f8df4e8217517fc06268`,
contract v1 revision 3 at `54c0c4d9`. Their public source tags/commits and license files were inspected;
installed binary compatibility is **not** established. Record SHA-256 of each already installed executable
and its exact version output in the private run folder before proceeding. There is no `latest` tag.
Community server uses **SSPL v1**, not Apache or an OSI-approved license; no Enterprise/commercial components
or paid service are used. If that source-available license is outside the allowed local tool policy, stop.
No license was installed or accepted by this work.

A ready core must be an **already built, exclusively owned local integration fixture** running the real
TerraBase Program with import support. The source `tests/TerraBase.TestHarness/ImportClient.cs` shows the
supported fixture pattern: `ManagementApi.StartAsync()`, its ephemeral issuer, and project JWT scopes
`databases:read databases:write documents:read documents:write`. This package does not add or build that
fixture. A Debug apphost's existence is insufficient. Require local fixture ownership, binary/DLL hashes,
build revision matching the pin, its actual authenticated **Client** listener address, home region, fresh project
ULID, verified synthetic catalog, short-lived local JWT, and its teardown handle. Have the fixture owner
write its authentication header to a **mode-0600** curl config in the run folder, without printing it.
This pinned procedure uses the Program's plain HTTP Client listener at `http://127.0.0.1:<port>` only;
no reverse proxy, Internal listener or public endpoint is acceptable. No source credentials are needed. No production/auth-provider credentials,
skip-auth path, live customer data, remote infrastructure proxy/SSH, TLS bypass, or guessed listener is acceptable.

## 2. Start one isolated source and generate three documents

All commands below run from the repository root. The fixture supervisor must provide a fresh lowercase
ULID `ACCEPTANCE_RUN_ID` and an ephemeral unused TCP port `ACCEPTANCE_MONGO_PORT`.

Use a dedicated Bash shell with `set -euo pipefail` and `umask 077`; an unexpected command failure stops
dependent phases. Preserve the owned-resource handles for section 5 cleanup even when a shell exits.

```sh
set -euo pipefail
umask 077
acceptance_run="tools/import-adapters/mongodb/acceptance/runs/$ACCEPTANCE_RUN_ID"
[[ "$ACCEPTANCE_RUN_ID" =~ ^[0-7][0-9a-hjkmnp-tv-z]{25}$ ]]
[[ "$ACCEPTANCE_MONGO_PORT" =~ ^[1-9][0-9]{3,4}$ && "$ACCEPTANCE_MONGO_PORT" -ge 1024 && "$ACCEPTANCE_MONGO_PORT" -le 65535 ]]
mkdir -m 700 -p tools/import-adapters/mongodb/acceptance/runs
mkdir -m 700 "$acceptance_run"
mkdir -m 700 "$acceptance_run/mongo-data"
acceptance_mongod="$(command -v mongod)"
acceptance_mongoimport="$(command -v mongoimport)"
acceptance_mongoexport="$(command -v mongoexport)"
sha256sum "$acceptance_mongod" "$acceptance_mongoimport" "$acceptance_mongoexport" \
  > "$acceptance_run/tool-sha256.txt"
nice -n 19 "$acceptance_mongod" --version > "$acceptance_run/mongod-version.txt"
nice -n 19 "$acceptance_mongoimport" --version > "$acceptance_run/mongoimport-version.txt"
nice -n 19 "$acceptance_mongoexport" --version > "$acceptance_run/mongoexport-version.txt"
```

The run folder must be new and inside this adapter; never reuse a previous source or core database. A port
allocated from a temporary socket has a release/start race: if the owned mongod cannot bind, stop instead
of connecting to whoever took that port. Do not use an existing MongoDB service or container.

The source process must have enforced **1 GiB memory, 0.5 CPU, 128 tasks, and a 180-second lifetime**.
The following creates one temporary user service, not global configuration. Stop if user cgroup limits
cannot be enforced. Keep it loopback-only; no other source writers exist:

```sh
nice -n 19 systemd-run --user --collect --unit="mongodb-adapter-$ACCEPTANCE_RUN_ID" \
  -p MemoryMax=1G -p CPUQuota=50% -p TasksMax=128 -p RuntimeMaxSec=180 -p TimeoutStopSec=5 \
  nice -n 19 "$acceptance_mongod" --bind_ip=127.0.0.1 --port="$ACCEPTANCE_MONGO_PORT" \
  --dbpath="$PWD/$acceptance_run/mongo-data" --wiredTigerCacheSizeGB=0.25 \
  --logpath="$PWD/$acceptance_run/mongod.log" --nounixsocket
nice -n 19 timeout --kill-after=1s 10s bash -c '
  for acceptance_attempt in {1..10}; do
    if rg --quiet "\"id\"\\s*:\\s*23016\\b.*\"port\"\\s*:\\s*$2\\b" "$1/mongod.log" 2>/dev/null \
        && systemctl --user is-active --quiet "$3"; then
      exit 0
    fi
    sleep 1
  done
  exit 1
' bash "$acceptance_run" "$ACCEPTANCE_MONGO_PORT" "mongodb-adapter-$ACCEPTANCE_RUN_ID"
acceptance_source_pid="$(systemctl --user show "mongodb-adapter-$ACCEPTANCE_RUN_ID" -p MainPID --value)"
[[ "$acceptance_source_pid" =~ ^[1-9][0-9]*$ ]]
[[ "$(readlink "/proc/$acceptance_source_pid/exe")" == "$(readlink -f "$acceptance_mongod")" ]]
systemctl --user show "mongodb-adapter-$ACCEPTANCE_RUN_ID" \
  -p MainPID -p MemoryMax -p CPUQuotaPerSecUSec -p TasksMax -p RuntimeMaxUSec \
  > "$acceptance_run/source-unit.txt"
```

Check the unit's main PID, executable identity, limits and successful bind against its **own** local log.
Log event 23016 is MongoDB's `Waiting for connections` with this run's exact port; the run log must be new.
No credentials/auth configuration are attached to this disposable, synthetic-only loopback source.
Wait at most 10 seconds for its readiness; never probe a foreign listener. Require an already resource-capped
core fixture (at most one CPU/1 GiB, 128 tasks, 180-second remaining lifetime); do not change another
fixture's limits. Bound each seed/export command
to 20 seconds and all artifacts in this run to **16 MiB excluding source/core data directories**, which
must each stay below **256 MiB allocated space**, measured with `du -sk` (262144 KiB).
WiredTiger journal preallocation can have a larger apparent size; do not use `du --apparent-size`.
Check allocated sizes and the 16 MiB artifact cap after each phase; terminate only owned processes on excess.

```sh
nice -n 19 python3 -I -B tools/import-adapters/mongodb/acceptance/check.py seed "$acceptance_run"
nice -n 19 timeout --kill-after=1s 20s "$acceptance_mongoimport" \
  --host=127.0.0.1 --port="$ACCEPTANCE_MONGO_PORT" \
  --db=mongodb_adapter_acceptance --collection=records --type=json --mode=insert \
  --maintainInsertionOrder --stopOnError --file="$acceptance_run/seed-full.jsonl" \
  > "$acceptance_run/seed.log" 2>&1
nice -n 19 timeout --kill-after=1s 20s "$acceptance_mongoexport" --host=127.0.0.1 --port="$ACCEPTANCE_MONGO_PORT" \
  --db=mongodb_adapter_acceptance --collection=records --type=json --jsonFormat=canonical \
  --sort='{"_id":1}' --out="$acceptance_run/export.jsonl" > "$acceptance_run/export.log" 2>&1
chmod 400 "$acceptance_run/export.jsonl"
nice -n 19 timeout --kill-after=1s 20s python3 -I -B tools/import-adapters/mongodb/convert.py \
  "$acceptance_run/export.jsonl" "$acceptance_run/converted.sql" \
  --database mongodb_adapter_acceptance --collection records > "$acceptance_run/converter.log" 2>&1
nice -n 19 timeout --kill-after=1s 20s python3 -I -B tools/import-adapters/mongodb/convert.py \
  "$acceptance_run/export.jsonl" "$acceptance_run/converted-repeat.sql" \
  --database mongodb_adapter_acceptance --collection records > "$acceptance_run/converter-repeat.log" 2>&1
cmp "$acceptance_run/converted.sql" "$acceptance_run/converted-repeat.sql"
nice -n 19 python3 -I -B tools/import-adapters/mongodb/acceptance/check.py prepare "$acceptance_run"
```

The generator uses only the Python standard library; mongoimport reads this trusted canonical EJSON as data,
with no JavaScript shell/profile or telemetry configuration. The pinned Tools source `mongoimport/options.go`
defines these flags, and `mongoimport/json.go` uses `bson.UnmarshalExtJSON` (no `--legacy`). Exports are parsed
only as data and never executed. The seed's three explicit
IDs are ObjectId, int64 and string. It adds a **700,000-character** ASCII field to one record, crossing many
I/O buffer boundaries while remaining below the core's 1 MiB document limit. No TTL indexes or background
writers are created; no mutations are allowed after seeding. mongoexport still has **no snapshot guarantee**:
this procedure relies on the exclusively owned source remaining quiescent throughout export.

The seed includes BSON int64-max Date, UTC `1699162200123` ms and `-1` ms as canonical date wrappers;
`America/New_York` is a separate application string,
not a recovered BSON timezone. Wrapper checks include Decimal128 34 digits, `123.4500` scale, minimum
subnormal `1E-6176`, maximum finite Decimal128, int64 extrema, int32 minimum, signed double zero/subnormal/NaN,
binary subtypes `80`, `00`, `04`, uint32 BSON timestamp components, nested arrays, null/missing and Unicode.
Go's mongoexport may change a double's lexical spelling; `check.py` compares IEEE binary64 bits against
the seed, then requires the converter/core to retain the **actual exported** wrapper spelling exactly.

## 3. Upload, inspect preview, and confirm exactly that preview

The fixture supplies **one** `ACCEPTANCE_CORE_BASE`: its attested authenticated **Client** listener,
`http://127.0.0.1:<port>`, plus `ACCEPTANCE_PROJECT` and `ACCEPTANCE_HOME_REGION`. All management, import,
readback and cleanup calls use this same base. Internal/operator access is prohibited. The fresh project
must belong to the exclusively owned synthetic fixture. `core-auth.conf` contains only the short-lived
local `Authorization` header, is a nonsymlink mode-0600 file in the run folder, and contains no URL, proxy,
redirect, output, TLS or other curl options. Never echo it or put the token on the command line. The fixture
supervisor writes exactly `header = "Authorization: Bearer <local-fixture-JWT>"` plus LF.
The helper validates that one line and its file permissions without printing the token. `curl --disable` is always the first curl
option, preventing an ambient `.curlrc` from adding credentials, destinations or side effects.

Use the run ULID to make a fresh database name. Omit replicas. Define bounded helpers in the same Bash
shell; every response stays in the private run directory. No curl redirect/retry or proxy is permitted:

```sh
[[ "$ACCEPTANCE_CORE_BASE" =~ ^http://127\.0\.0\.1:[0-9]+$ ]]
[[ "$ACCEPTANCE_PROJECT" =~ ^[0-7][0-9A-HJKMNP-TV-Z]{25}$ ]]
[[ "$ACCEPTANCE_HOME_REGION" =~ ^[a-z]{3}$ ]]
nice -n 19 python3 -I -B tools/import-adapters/mongodb/acceptance/check.py auth-config "$acceptance_run"
acceptance_database="import-$ACCEPTANCE_RUN_ID"
acceptance_databases="$ACCEPTANCE_CORE_BASE/v1/projects/$ACCEPTANCE_PROJECT/databases"
acceptance_client="$acceptance_databases/$acceptance_database"
acceptance_import="$acceptance_client/import"

acceptance_curl() {
  nice -n 19 curl --disable --config "$acceptance_run/core-auth.conf" \
    --proto '=http' --noproxy '*' --proxy '' --connect-timeout 2 \
    --max-time "${acceptance_request_cap:-10}" --max-redirs 0 --max-filesize 4194304 \
    --silent --show-error -H "X-TerraScale-Region-Pin: $ACCEPTANCE_HOME_REGION" "$@"
}
acceptance_call() {
  local acceptance_code="$1" acceptance_capture="$2"
  shift 2
  acceptance_curl -o "$acceptance_run/$acceptance_capture.json" -w '%{http_code}\n' "$@" \
    > "$acceptance_run/$acceptance_capture.status"
  [[ "$(<"$acceptance_run/$acceptance_capture.status")" == "$acceptance_code" ]]
}
```

**Before creating any destination**, prove unauthenticated access is refused. This request intentionally
omits `core-auth.conf`; the previously attested Client address must return **401**, never operator access:

```sh
nice -n 19 curl --disable --proto '=http' --noproxy '*' --proxy '' \
  --connect-timeout 2 --max-time 10 --max-redirs 0 --max-filesize 4194304 --silent --show-error \
  -H "X-TerraScale-Region-Pin: $ACCEPTANCE_HOME_REGION" "$acceptance_import" \
  -o "$acceptance_run/unauthenticated.json" -w '%{http_code}\n' \
  > "$acceptance_run/unauthenticated.status"
[[ "$(<"$acceptance_run/unauthenticated.status")" == 401 ]]
printf '{"name":"%s"}\n' "$acceptance_database" > "$acceptance_run/create-database.json"
acceptance_call 201 created-database -H 'Content-Type: application/json' \
  --data-binary "@$acceptance_run/create-database.json" "$acceptance_databases"
acceptance_call 201 created-import -H 'Content-Type: application/json' \
  --data-binary "@$acceptance_run/create-import.json" "$acceptance_import"
acceptance_call 200 upload -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$acceptance_run/converted.sql" "$acceptance_import/source?offset=0"
```

Require HTTP **401, 201, 201, 200**, respectively. An unexpected code stops dependent steps. Track the
successful 201 database creation for cleanup; never delete a name after a failed create. The artifact is
capped by `check.py` at 4 MiB, so one upload part suffices (core maximum 4 MiB). No stale job replay or existing
destination is accepted. Recheck resource sizes between phases.

Each poll phase has a **30-second wall deadline**, at most **15 requests**, and waits at most two seconds
between requests. Request timeouts shrink to the remaining deadline. Checker exit 0 means ready/complete,
2 means an allowed pending state, and 1 means stop. These are job-state polls, never a wait for build slots:

```sh
acceptance_poll() {
  local acceptance_phase="$1" acceptance_capture="$2"
  local acceptance_deadline=$((SECONDS + 30)) acceptance_poll_index acceptance_remaining acceptance_rc
  for ((acceptance_poll_index=0; acceptance_poll_index<15; acceptance_poll_index++)); do
    acceptance_remaining=$((acceptance_deadline - SECONDS))
    ((acceptance_remaining > 0)) || return 1
    acceptance_request_cap=$((acceptance_remaining < 10 ? acceptance_remaining : 10))
    acceptance_call 200 "$acceptance_capture" "$acceptance_import" || return 1
    acceptance_remaining=$((acceptance_deadline - SECONDS))
    ((acceptance_remaining > 0)) || return 1
    if nice -n 19 timeout --kill-after=1s "${acceptance_remaining}s" python3 -I -B \
        tools/import-adapters/mongodb/acceptance/check.py "$acceptance_phase" "$acceptance_run"; then
      ((SECONDS < acceptance_deadline)) || return 1
      acceptance_request_cap=10
      return 0
    else
      acceptance_rc=$?
      [[ "$acceptance_rc" == 2 ]] || return 1
    fi
    acceptance_remaining=$((acceptance_deadline - SECONDS))
    ((acceptance_remaining > 0)) || return 1
    nice -n 19 sleep "$((acceptance_remaining < 2 ? acceptance_remaining : 2))"
  done
  return 1
}
acceptance_poll preview-state preview
acceptance_request_cap=10
acceptance_curl "$acceptance_client/collections/mongodb_documents/documents?limit=1000" \
  -o "$acceptance_run/before-start.json" -w '%{http_code}\n' > "$acceptance_run/before-start.status"
nice -n 19 python3 -I -B tools/import-adapters/mongodb/acceptance/check.py start-body "$acceptance_run"
acceptance_call 202 started -H 'Content-Type: application/json' \
  --data-binary "@$acceptance_run/start.json" "$acceptance_import/start"
acceptance_poll completion-state completed
acceptance_request_cap=10
acceptance_call 200 documents "$acceptance_client/collections/mongodb_documents/documents?limit=1000"
```

Preview polling permits `analyzing`, then requires `ready`. `start-body` requires zero blocking/warnings,
one table `public.mongodb_documents`, three rows, collection `mongodb_documents`, primary key `id`, mappings
`id:string`, `document:json`, `provenance:json`, all nonnullable and included. Before start, only **200 with
zero items and no cursor**, or **404 with `error.code=unknown_collection`**, is accepted. An auth/database
404 does not count as an empty collection. The generated start body confirms exactly the captured preview ID.

Completion polling permits `queued`, `running`, `validating`; success requires `completed` **and**
`source.staged == false`, within the same cap (staging deletion can lag the completed state briefly).
Stop on `rejected`, `failed`, `cancelled`, unknown state, checksum mismatch, destination mutation, timeout,
auth/region error or unexpected HTTP code. Do not retry, resume, change options, exclude fields or weaken
checks. The final readback must return 200; section 4 independently checks its captured contents.

## 4. Check readback and checksums, preserving honest evidence

After section 3 captured `documents.json` (HTTP 200), run:

```sh
nice -n 19 python3 -I -B tools/import-adapters/mongodb/acceptance/check.py verify "$acceptance_run"
```

Expect exactly **three** returned IDs, no next cursor, exact `{id, document, provenance}` bodies, null present
only where seeded, missing members still absent, all wrappers and array positions intact. Derived zero-time
ULIDs must match the adapter's pinned typed namespace/_id rule independently. The 700,000-character field
must be whole. Provenance uses the **real export's** SHA-256 and its actual row ordinals.
The exact ID algorithm and wrapper/loss semantics remain in the preserved [README](../README.md#output-and-ids):
compact ASCII JSON of `["mongodb-ejson-v1", "mongodb_adapter_acceptance", "records", typedKey]`, SHA-256,
first ten digest bytes as the payload of a zero-time ULID. The target retains BSON values as explicit
wrapper objects under `document`; MongoDB indexes, query semantics and native TerraBase BSON types are not inferred.

Require core `completed`, matching confirmed preview ID, `error:null`, `validation.passed:true`,
`rowsWritten=rowsTotal=3`, one validation table with `sourceRows=documents=3`, `match:true`, and identical
64-digit lowercase `sourceChecksum` and `documentChecksum`. Source staging must be cleared on completion.

`expected.json` contains the export SHA-256, output bytes/SHA-256, derived IDs and an independent body
checksum: SHA-256 of the ID-sorted body array encoded as sorted-key compact ASCII JSON. `verified.json`
records `capturedEvidencePassed`, the core checksum and readback body checksum. That independent checksum is **not** the core's
different canonical numeric/checksum algorithm. No actual export/output/core checksum is predetermined
before real tools run: tool serialization, row order and provenance affect bytes. Do not substitute the
original hand-written fixture's `4e756e76…` golden hash or declare a guessed hash as actual export evidence.

The offline checker validates captured files, not process identity or binary origin. Full-path evidence
requires the actual commands above, recorded binary/version/fixture attestation, successful real responses,
and cleanup. Simulated checker self-tests are a separate category and never qualify as actual acceptance.

## 5. Cleanup owned resources on success or failure

On failure after a job starts, send the documented `POST $acceptance_import/cancel` only to this owned
destination if its fixture is still reachable. Record the failure and do not resume. Delete **only** the
fresh database created in this run, through `DELETE $acceptance_client` using the
same Client listener/auth/region controls (expect 204). No preexisting database may be adopted or deleted.
The destructive cleanup below is authorized only for the run's successfully created, exclusively owned
synthetic database and service; first check the recorded 201 and ownership handles. Do not run it for a
failed/uncertain create, even if the name happens to exist:

```sh
# Failure after start only; omit this line on success. Terminal jobs may answer 409:
acceptance_call 202 cancelled -X POST "$acceptance_import/cancel" || :
# Success or failure after a confirmed owned create:
acceptance_call 204 deleted-database -X DELETE "$acceptance_client" || :
nice -n 19 systemctl --user stop "mongodb-adapter-$ACCEPTANCE_RUN_ID"
```

If cancellation fails or the local endpoint has expired, the owner still tears down the exclusively
owned fixture; a failure must not skip teardown. Record database-delete status and process exit evidence.
`|| :` continues teardown, never acceptance: a non-204 DELETE or missing cleanup evidence leaves the run failed.
Use the fixture's teardown handle to stop its owned server/ephemeral issuer and remove its own temp data.

Stop only `mongodb-adapter-$ACCEPTANCE_RUN_ID` through `systemctl --user stop` and verify it has exited before
removing its source data. The 180-second service lifetime is a final bound, not a cleanup substitute. Remove
the owned `mongo-data` folder and private auth config using the run supervisor's guarded cleanup after
checking the paths are nonsymlinks inside this exact run folder. Never issue broad `rm -rf`, stop all
containers, remove shared caches, kill a foreign process, or edit global settings. Leave small redacted
version/hash/status/evidence files and synthetic export/COPY artifacts within the adapter directory; do
not commit raw process logs, auth material, run databases or runtime-generated files.

Run the helper's cheap parallel self-check without a MongoDB/core service:

```sh
nice -n 19 python3 -I -B tools/import-adapters/mongodb/acceptance/self_check.py
```

Current evidence is **83/83 preserved synthetic adapter CLI tests at `34f616ea`** and **10/10 offline helper
self-tests with simulated core responses**. Real mongoexport→core acceptance is **NOT RUN**. No service,
upload, customer connection, build, CI or deployment was started for this procedure. The local branch/PR
body remain unpublished: existing Alpha PR workflows compete with release CI, so no publish preflight passed.
