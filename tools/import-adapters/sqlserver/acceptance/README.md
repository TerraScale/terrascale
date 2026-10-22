# SQL Server → converter → TerraBase acceptance preparation

**Status: real-source export and core acceptance are UNRUN.** This directory
prepares the next approved disposable run. It does not authorize one. Original
commit `18d7877e`, its 74/74 parallel CLI evidence, and the ignored local
`../DRAFT_PR.md` are preserved. The new golden captures are synthetic expectations,
not captured SQL Server output. Offline checks never connect to a server.

## Current preflight and exact missing prerequisites

The read-only inspection found `sqlcmd`, `bcp`, Docker and PowerShell absent.
Podman exists, but its image/container inventory contains no SQL Server. Four
TerraBase binaries exist in other checkouts; none of those checkouts has import
feature sources, so none is a ready acceptance target. No binary was launched.
TerraBase Fast, VBCSCompiler and dashboard work were consuming CPU. Core feature
refs are accessible as source, but the user's integration readiness approval is
pending. No install, image pull, license acceptance, account creation, credentials,
shared CI, source connection, core build, upload, publication or deployment ran.

The next real run requires all five prerequisites:

1. An **already provisioned, owner-approved disposable SQL Server 2022 (major 16)**
   instance bound to loopback, with an existing integrated-auth identity, trusted
   TLS certificate, and a fresh empty database `ts_sqlserver_acceptance_<run ULID>`.
   `ALLOW_SNAPSHOT_ISOLATION` must already be available (`state=1`). These scripts
   do not create databases, users or change database/server options. If any part
   is missing, stop; do not install, pull images, accept a license, use a source
   login or enable snapshot isolation to fill the gap.
2. An existing **ODBC sqlcmd** with `-E -N -I -f 65001 -h -1 -y 0 -w 65535 -b -r1`.
   Check its local `-?` help first. The Go sqlcmd/BCP formats are not substituted.
   Do not use `-C`, `-W`, `-u`, a password argument or a connection string.
3. An **approved, already built and running local import-core** revision supplied
   by its owner, including passing readiness evidence and the contract v1/r3
   operations. Do not launch a binary found by filename, rebuild the feature
   branch, message its owner, or weaken auth. Record its revision/binary SHA-256
   and OpenAPI readiness evidence privately before the run. There is intentionally
   no invented server launch command in this procedure.
4. A fresh synthetic TerraBase project, a scoped **test-only project JWT** with
   `databases:read/write` and `documents:read/write`, and a private curl config
   containing its Authorization header and trusted local CA. The origin must be
   `https://127.0.0.1:<port>` with a matching certificate and no proxy/redirects.
   No production token or source credential belongs in logs, argv or this repo.
5. A release-safe acceptance window with no competing core/Fast/compiler work,
   plus explicit approval for the source fixture writes and core preview/start.
   Cleanup is destructive and requires separate approval for the exact recorded
   resources. Allow about 10 minutes once these prerequisites exist. Keep every
   local command at `nice -n 19`; no shared CI is part of this procedure.

## Fixed fixture and expected results

[fixture.sql](fixture.sql) creates only `dbo.sqlserver_acceptance` in the guarded
empty disposable database, inserts four rows, and marks the table with its exact
database name. It uses no user functions, triggers, extensions or downloaded SQL.
It uses fixed, reviewed built-in casts/string operations solely for synthetic
fixture data. [export.sql](export.sql) uses one read-only snapshot transaction and
rolls it back. It checks version, row count, column types, nullability, identity,
computed/persisted metadata, ownership marker, primary key and declared
length/precision/scale before exporting.
No DDL or writes run inside the export transaction; freeze DDL and all fixture
writers across setup/export. Capture files stay unchanged after sealing.

There are 17 explicitly declared columns, four identity keys `1`, `2`, `3`, `4`,
and one collection `dbo_sqlserver_acceptance`. The full exact typed bodies are in
[expected-documents.json](expected-documents.json). Key observations:

| Row | Required observations |
| --- | --- |
| 1 | Decimal `"123456789012345678901234567890.12345678"`, money `"922337203685477.5807"`, smallmoney `"-214748.3648"`, bigint edge **number** `9223372036854775807`; Unicode/tab/CR/LF and literal `\N`/`\.`; binary `00ff800102` → `"AP+AAQI="`; bit **true**; computed `"1234.00"` |
| 2 | Decimal `"0.00000000"`, minimum money and maximum smallmoney; bigint edge **number** `-9223372036854775808`; seven zero fraction digits and `+14:00`; empty text `""`, empty binary base64 `""`, bit **false**; computed `"0.30"` |
| 3 | Explicit JSON **null** for Unicode, binary and bit (fields remain present); negative decimal digits, year 0001 and `+00:00`; computed `"-3.00"` |
| 4 | Text is 3998 `x`, one emoji, then 45152 `x`: 49152 UTF-16 units / 98304 source bytes, emoji across units 3999–4000; `.0000001` and `-14:00`; computed `"0.00"` |

Decimals/money stay strings, datetime2/time/datetimeoffset stay exact strings,
UUIDs lowercase, binary becomes standard base64, integer/bit stay number/boolean.
Identity and computed behavior is not recreated. Source collation is not carried
over. Negative-zero literals are intentionally absent from this real-source
fixture; the original converter's exact-string test remains unchanged.

Two canonical chunks contain **3 + 1 rows**, respectively. Each source/display
line is bounded to 128 KiB and the whole raw export/receipt to 256 KiB; the known
49 KiB record fits sqlcmd's 65535-character display width. This sqlcmd display
recipe is only for this bounded fixture, not a general exporter for large customer
tables. Any wrapping, truncation or encoding drift is refused by exact value and
canonical byte comparisons. The first `CONCAT` operand is `nvarchar(max)`, nullable
slots use explicit `CASE ... null`, and `REPLICATE` uses a max operand. Temporal
digits are assembled from date/time plus nanosecond parts; money uses style 2.

## Source commands — UNRUN; only after prerequisites and approval

Run the blocks in order in one Bash session from the repository root; fail-fast
settings stop the session after a failed setup, export, check or HTTP request.
The source owner supplies non-secret disposable
ports and a **fresh lowercase ULID**; no default target or account is provided:

```bash
set -euo pipefail
fixture_adapter=$PWD/tools/import-adapters/sqlserver
fixture_acceptance=$fixture_adapter/acceptance
: "${FIXTURE_RUN_ULID:?owner supplies a fresh lowercase ULID}"
: "${FIXTURE_SQL_PORT:?owner supplies the disposable loopback port}"
[[ $FIXTURE_RUN_ULID =~ ^[0-7][0-9a-hjkmnp-tv-z]{25}$ ]] || exit 1
[[ $FIXTURE_SQL_PORT =~ ^[0-9]{1,5}$ ]] || exit 1
(( 10#$FIXTURE_SQL_PORT >= 1 && 10#$FIXTURE_SQL_PORT <= 65535 )) || exit 1
fixture_sql_database=ts_sqlserver_acceptance_$FIXTURE_RUN_ULID
fixture_run=$fixture_acceptance/runs/$FIXTURE_RUN_ULID
umask 077
mkdir -p "$fixture_acceptance/runs"
mkdir "$fixture_run"  # exclusive; never overwrite a prior run
```

Load **only the reviewed repository fixture**, not supplied export SQL:

```bash
nice -n 19 sqlcmd -S "tcp:127.0.0.1,$FIXTURE_SQL_PORT" -E -N -I \
  -d "$fixture_sql_database" -v "FixtureDatabase=$fixture_sql_database" \
  -f 65001 -b -r1 -l 5 -t 30 -i "$fixture_acceptance/fixture.sql" \
  >"$fixture_run/source-setup.stdout" 2>"$fixture_run/source-setup.stderr"
```

Export that immutable fixture snapshot. Stop on any nonzero command exit; do not
run the next command after a failed export. A TLS/auth/snapshot error is a missing
prerequisite, not a reason to add bypasses or database options:

```bash
nice -n 19 sqlcmd -S "tcp:127.0.0.1,$FIXTURE_SQL_PORT" -E -N -I \
  -d "$fixture_sql_database" -v "FixtureDatabase=$fixture_sql_database" \
  -f 65001 -h -1 -y 0 -w 65535 -b -r1 -l 5 -t 30 \
  -i "$fixture_acceptance/export.sql" \
  >"$fixture_run/source-rows.raw" 2>"$fixture_run/source-export.stderr"
nice -n 19 python3 -I -B "$fixture_acceptance/check.py" seal \
  "$fixture_run/source-rows.raw" "$fixture_run/capture"
nice -n 19 python3 -I -B "$fixture_adapter/convert.py" \
  "$fixture_run/capture/manifest.json" "$fixture_run/output"
nice -n 19 python3 -I -B "$fixture_acceptance/check.py" artifact "$fixture_run/output"
```

`seal` allows only blank sqlcmd display separators outside records. It strictly
decodes UTF-8, requires exactly four complete positional arrays in PK order,
validates every typed value, normalizes hex case only, and matches the committed
canonical capture bytes. It fills only chunk hashes/counts/sizes in the fixed
manifest template. `artifact` requires exact golden COPY bytes, hash and count.
Never relabel the committed synthetic golden files as a real source run.

## Core HTTP commands — UNRUN; only after core readiness approval

The endpoints come from `docs/terrabase/import.md@54c0c4d9` and the repository
Documents API. Get/list document responses have `{id,version,state,document}`;
the list has `{items,next_cursor}`. No alternate IDs, auth or server format.

**Known readiness gap:** contract §6 quotes non-bare preview column names
(`"note.名"`); accessible core `70ec9570` returns raw `note.名`. The checker follows
that core source and refuses changed names. Resolve this discrepancy during the
future approved core-readiness preflight before running acceptance; this task
does not edit the contract/core or add a compatibility fallback. Document body
keys and the supported COPY artifact remain exactly as declared in the manifest.

Supply the existing local test context; keep its curl config private. Verify the
ready build's OpenAPI includes `createImport`, `uploadImportSource`, `startImport`
and `getImport` before creating anything. Do not route this to remote infrastructure or a live app:

```bash
set -euo pipefail
: "${FIXTURE_CORE_PORT:?approved running local core port}"
: "${FIXTURE_CORE_PROJECT:?approved synthetic project ULID}"
: "${FIXTURE_CORE_REGION:?approved local core region}"
: "${FIXTURE_CORE_CURL_CONFIG:?private test JWT and trusted local CA config}"
[[ $FIXTURE_CORE_PORT =~ ^[0-9]{1,5}$ ]] || exit 1
(( 10#$FIXTURE_CORE_PORT >= 1 && 10#$FIXTURE_CORE_PORT <= 65535 )) || exit 1
[[ $FIXTURE_CORE_PROJECT =~ ^[0-7][0-9A-HJKMNP-TV-Z]{25}$ ]] || exit 1
[[ $FIXTURE_CORE_REGION =~ ^[a-z]{3}$ ]] || exit 1
fixture_origin=https://127.0.0.1:$FIXTURE_CORE_PORT
fixture_project=$fixture_origin/v1/projects/$FIXTURE_CORE_PROJECT
fixture_database_name=import-$FIXTURE_RUN_ULID
fixture_database=$fixture_project/databases/$fixture_database_name

request() {
  local expected=$1 method=$2 url=$3 receipt=$4 status
  shift 4
  status=$(nice -n 19 curl --disable --config "$FIXTURE_CORE_CURL_CONFIG" \
    --silent --show-error --proto '=https' --noproxy '*' --max-redirs 0 \
    --connect-timeout 5 --max-time 30 --max-filesize 262144 \
    -H "X-TerraScale-Region-Pin: $FIXTURE_CORE_REGION" \
    -X "$method" --url "$url" -o "$fixture_run/$receipt" -w '%{http_code}' "$@") || return 1
  [[ $status == "$expected" ]] || { printf 'unexpected HTTP status: %s\n' "$status" >&2; return 1; }
}

nice -n 19 jq -n --arg name "$fixture_database_name" --arg region "$FIXTURE_CORE_REGION" \
  '{name:$name,homeRegion:$region}' >"$fixture_run/create-database.json"
request 201 POST "$fixture_project/databases" database-created.json \
  -H 'Content-Type: application/json' --data-binary "@$fixture_run/create-database.json"
nice -n 19 jq -e --arg name "$fixture_database_name" --arg project "$FIXTURE_CORE_PROJECT" \
  --arg region "$FIXTURE_CORE_REGION" \
  '.name == $name and .projectId == $project and .homeRegion == $region and .region == $region
   and .replicaRegions == [] and (.id | test("^[0-7][0-9A-HJKMNP-TV-Z]{25}$"))' \
  "$fixture_run/database-created.json" >/dev/null
```

A `201` receipt is the ownership record; stop on any replay/conflict. Create the
import and upload the exact sealed artifact in **one part under 4 MiB**:

```bash
nice -n 19 jq '{format:"postgres_plain",sourceBytes:.output.bytes,sourceSha256:.output.sha256,
  options:{missingPrimaryKey:"reject",exclude:[]}}' \
  "$fixture_run/output/provenance.json" >"$fixture_run/create-import.json"
request 201 POST "$fixture_database/import" import-created.json \
  -H 'Content-Type: application/json' --data-binary "@$fixture_run/create-import.json"
request 200 PUT "$fixture_database/import/source?offset=0" source-uploaded.json \
  -H 'Content-Type: application/octet-stream' --data-binary "@$fixture_run/output/import.sql"

wait_state() {
  local wanted=$1 receipt=$2 state attempt
  for ((attempt=0; attempt<90; attempt++)); do
    request 200 GET "$fixture_database/import" "$receipt" || return 1
    state=$(nice -n 19 jq -er '.state' "$fixture_run/$receipt") || return 1
    [[ $state == "$wanted" ]] && return 0
    case $state in analyzing|queued|running|validating) ;; *) return 1 ;; esac
    sleep 2
  done
  return 1
}
wait_state ready preview.json
nice -n 19 python3 -I -B "$fixture_acceptance/check.py" preview \
  "$fixture_run/preview.json" "$fixture_run/output"
```

Inspect the local preview and provenance before confirmation: one non-excluded
table, rows 4, correct collection/PK/mappings, blocking 0, exact artifact SHA-256.
Disclose that failed/cancelled core sources can be retained seven days. A stale
preview, unsupported route, 421, nonzero blocking count or unexpected HTTP status
stops acceptance; preserve receipts privately without printing their bodies.

**Separate confirmation step:** obtain approval for the exact local preview,
then execute the following. The runbook never auto-confirms or auto-deletes:

```bash
nice -n 19 jq '{previewId:.preview.id}' "$fixture_run/preview.json" >"$fixture_run/start.json"
request 202 POST "$fixture_database/import/start" started.json \
  -H 'Content-Type: application/json' --data-binary "@$fixture_run/start.json"
wait_state completed completed.json
nice -n 19 python3 -I -B "$fixture_acceptance/check.py" completed \
  "$fixture_run/completed.json" "$fixture_run/output"
nice -n 19 jq -e --slurpfile ready "$fixture_run/preview.json" \
  '.confirmedPreviewId == $ready[0].preview.id' "$fixture_run/completed.json" >/dev/null
request 200 GET "$fixture_database/collections/dbo_sqlserver_acceptance/documents?limit=5" documents.json
nice -n 19 python3 -I -B "$fixture_acceptance/check.py" documents "$fixture_run/documents.json"
```

Acceptance requires `completed`, validation passed, sourceRows=documents=4, matching
server checksums, exactly four IDs and no next cursor, and exact JSON values/types
including the signed bigint edges. Python parses integers exactly and rejects
floating-point receipt numbers; `true` cannot pass as integer `1`. Do not claim
success based only on upload status or a synthetic/golden receipt.

## Cleanup — UNRUN, manual and destructive

Keep the fixture/run directory and private evidence on failure. Never clean up on
an error path. No shell removal command or database drop is provided.

1. For TerraBase, first obtain explicit approval to delete the exact fresh
   `import-<run ULID>` recorded by the successful `201`. Read back `GET D` and
   `GET D/import`; require matching database id/name/project/home region and the
   recorded source hash. Read `GET D/collections?limit=5`; require only
   `dbo_sqlserver_acceptance` and `terrascale-import`, with no next cursor. If
   anything differs, stop. Only then run `request 204 DELETE "$fixture_database"
   deleted.json`. Verify `GET D` returns the documented missing-database error;
   do not report a preexisting 404 as deletion by this run. Core owns staged-source
   cleanup; never remove its files directly.
2. For SQL Server, confirm the exact disposable database and owned table marker,
   then run `nice -n 19 sqlcmd -S "tcp:127.0.0.1,$FIXTURE_SQL_PORT" -E -N -I
   -d "$fixture_sql_database" -v "FixtureDatabase=$fixture_sql_database" -f 65001
   -b -r1 -l 5 -t 30 -i "$fixture_acceptance/cleanup.sql"`, with stdout/stderr
   redirected to private evidence. This guarded script drops only the marked
   synthetic table. It does not drop the database, source snapshots or containers.
3. Record the actual source/tool/core revisions, raw/canonical/artifact hashes,
   four source and destination counts, preview id, result and cleanup outcome.
   Preserve the original 74-test evidence separately. Any future failure report
   must exclude tokens, accounts and private source data. No CI/publication work
   is authorized by this preparation.

## Offline preparation check

```sh
nice -n 19 python3 -I -B tools/import-adapters/sqlserver/acceptance/test.py
```

This test runs only new local CLI preparation checks with synthetic data and
receipts, in parallel, using exclusive temporary directories under ignored
`acceptance/runs/`. It does not execute the SQL scripts, curl commands or a
core binary and does not repeat/build the shared engine or the original 74 suite.
Python commands use isolated mode (`-I`) to ignore `PYTHONPATH`, user site packages
and working-directory import injection; `-B` prevents bytecode writes.
