# Disposable native-export acceptance recipe

Start with [STATUS.md](STATUS.md). **All container/export/core HTTP commands below
are UNRUN.** Missing installed exporters, cached source images and an owned ready
core prevent this acceptance run. Active local builds also prevent admission.
Do not pull, install, restore, build, start CI, push or open a PR to satisfy it.

The historical `41a15eb3` commit, 127-check evidence, synthetic input/output fixtures
and `../PR.md` are preserved. The adapter's publication follow-up emits unnamed
constraints inline for the pinned core and rejects Unicode TIME digits. The current
golden is `../fixtures/expected-core.sql`; the original `expected.sql` remains a
historical artifact. This separate recipe checks the adapter against
real exporter bytes and core source at
`567bfd33fe1257a922c4f8df4e8217517fc06268`; it does not claim those checks passed.

## 1. Satisfy local prerequisites before starting anything

Allow about 5 minutes per source once all prerequisites exist. Run MySQL and MariaDB
**serially**, at low priority, only after local release/build contention has cleared.

| Candidate, not verified support | Server / exporter / client | Immutable local image prerequisite | Adapter declaration |
| --- | --- | --- | --- |
| MySQL Community **8.4.0** (GPL community build) | `mysqld` / `mysqldump` / `mysql`, all 8.4.0 | Already-cached community image matching `mysql:8.4.0`; record its full local sha256 ID and available RepoDigest | `mysql-8.4` |
| MariaDB **11.4.2** (GPL community build) | `mariadbd` / `mariadb-dump` / `mariadb`, all 11.4.2 | Already-cached community image matching `mariadb:11.4.2`; record its full local sha256 ID and available RepoDigest | `mariadb-11.4` |

These exact GA versions are bounded recipe candidates, not a claim that other
patches/versions work. No image ID/digest is available here; none is invented. An
image tag alone is insufficient. Reject a missing image, enterprise/proprietary
build, mismatched version, unknown architecture or unreviewed entrypoint.

Read-only preflight commands (rerun before a future admitted attempt):

```bash
command -v podman python3 curl mysqldump mariadb-dump mysql mariadb mysqld mariadbd
nice -n 19 podman images --format json
nice -n 19 podman ps --format '{{.ID}} {{.Image}} {{.Status}}'
ps -eo comm,pcpu --sort=-pcpu | head -15  # No argv, environment or credentials.
cat /proc/loadavg
```

Require an **already-running, explicitly owned, disposable local** TerraBase fixture
at the core commit above (or an independently reviewed compatible successor). It
must expose the real project database/import/document routes, local TLS trust and
an existing synthetic project identity. Its fixture owner must supply a private
0600 curl config with a test-issued project JWT, TLS CA and region pin, plus the
fixture URL/project. Scopes: `databases:read databases:write documents:read
documents:write`. No ts-auth accounts, source credentials, customer tokens, live
services, bypasses, `--insecure`, shared listeners or arbitrary discovered ports.

The source contains `ImportClient.StartAsync()` / `ManagementApi.StartAsync()` and
`TestIssuer`, not a ready standalone CLI launcher. No such ready harness/binary is
available here. Do not invent a bootstrap route or run `dotnet build/run/test`.
If a prebuilt harness cannot supply the required fixture, core acceptance stays
UNRUN. Source references:

- [ImportClient.cs at the pinned core](https://github.com/TerraScale/terrascale/blob/567bfd33fe1257a922c4f8df4e8217517fc06268/tests/TerraBase.TestHarness/ImportClient.cs)
- [actual import route tests](https://github.com/TerraScale/terrascale/blob/567bfd33fe1257a922c4f8df4e8217517fc06268/tests/TerraBase.Fast/Import/ImportApiTests.cs)
- [format and routing contract](https://github.com/TerraScale/terrascale/blob/567bfd33fe1257a922c4f8df4e8217517fc06268/docs/terrabase/import.md)

## 2. Seed one disposable, network-isolated source

**UNRUN.** From the repository root, use Bash. The required image ID must already
exist locally. Pick exactly one profile; keep outputs under this assigned directory.

```bash
cd tools/import-adapters/mysql
set -euo pipefail
umask 077
MYSQL_ACCEPT_RUN=$(mktemp -d "$PWD/acceptance/.run-XXXXXXXX")
MYSQL_ACCEPT_TAG=${MYSQL_ACCEPT_RUN##*/}
MYSQL_ACCEPT_MODE='STRICT_ALL_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION,NO_AUTO_VALUE_ON_ZERO'

# MySQL candidate; supply an existing immutable local image ID before this block.
: "${MYSQL_ACCEPT_IMAGE:?existing reviewed MySQL Community 8.4.0 sha256 image ID required}"
MYSQL_ACCEPT_VERSION=8.4.0
MYSQL_ACCEPT_SERVER=mysqld
MYSQL_ACCEPT_CLIENT=mysql
MYSQL_ACCEPT_DUMP=mysqldump
MYSQL_ACCEPT_FLAVOR=mysql-8.4
MYSQL_ACCEPT_ENV=(--env MYSQL_ALLOW_EMPTY_PASSWORD=yes --env MYSQL_DATABASE=adapter_fixture)
MYSQL_ACCEPT_VENDOR=(--set-gtid-purged=OFF --column-statistics=0)

# For a SEPARATE MariaDB run, replace the profile assignments above with:
# MYSQL_ACCEPT_VERSION=11.4.2
# MYSQL_ACCEPT_SERVER=mariadbd
# MYSQL_ACCEPT_CLIENT=mariadb
# MYSQL_ACCEPT_DUMP=mariadb-dump
# MYSQL_ACCEPT_FLAVOR=mariadb-11.4
# MYSQL_ACCEPT_ENV=(--env MARIADB_ALLOW_EMPTY_ROOT_PASSWORD=1 --env MARIADB_DATABASE=adapter_fixture)
# MYSQL_ACCEPT_VENDOR=()
# MYSQL_ACCEPT_IMAGE must identify the existing reviewed MariaDB 11.4.2 image.

case "$MYSQL_ACCEPT_IMAGE" in sha256:*) ;; *) exit 1 ;; esac
nice -n 19 podman image inspect "$MYSQL_ACCEPT_IMAGE" > "$MYSQL_ACCEPT_RUN/image.json"
nice -n 19 podman run --rm --detach --pull=never --network=none \
  --cpus=0.5 --memory=512m --pids-limit=128 \
  --label "terrascale.mysql-adapter-fixture=$MYSQL_ACCEPT_TAG" \
  --cidfile "$MYSQL_ACCEPT_RUN/container.id" "${MYSQL_ACCEPT_ENV[@]}" \
  "$MYSQL_ACCEPT_IMAGE" "$MYSQL_ACCEPT_SERVER" --skip-networking \
  --character-set-server=utf8mb4 --collation-server=utf8mb4_bin \
  --default-time-zone=+00:00 --sql-mode="$MYSQL_ACCEPT_MODE"
MYSQL_ACCEPT_CID=$(cat "$MYSQL_ACCEPT_RUN/container.id")

# Bounded socket-only readiness check. Never expose a port or add a password.
MYSQL_ACCEPT_READY=0
for MYSQL_ACCEPT_ATTEMPT in {1..60}; do
  if nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_CLIENT" \
      --no-defaults --protocol=socket -uroot --batch --skip-column-names \
      -e 'SELECT 1' > "$MYSQL_ACCEPT_RUN/ready.txt" 2>/dev/null; then
    MYSQL_ACCEPT_READY=1
    break
  fi
  sleep 1
done
test "$MYSQL_ACCEPT_READY" = 1
for MYSQL_ACCEPT_TOOL in "$MYSQL_ACCEPT_SERVER" "$MYSQL_ACCEPT_DUMP" "$MYSQL_ACCEPT_CLIENT"; do
  nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_TOOL" --version \
    > "$MYSQL_ACCEPT_RUN/$MYSQL_ACCEPT_TOOL.version.txt"
done
# Inspect those three LOCAL version files: each must be the exact matrix version.
# Stop here on mismatch; don't relabel the export as another version.

nice -n 19 podman exec -i "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_CLIENT" \
  --no-defaults --protocol=socket -uroot --default-character-set=utf8mb4 \
  adapter_fixture < acceptance/seed.sql
nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_CLIENT" \
  --no-defaults --protocol=socket -uroot --batch --skip-column-names \
  --default-character-set=utf8mb4 adapter_fixture \
  -e "SELECT @@version,@@global.sql_mode,@@global.time_zone,@@character_set_connection; SELECT 'orders',COUNT(*) FROM orders; SELECT 'Line Items',COUNT(*) FROM \`Line \"Items\"\`; SELECT 'chunked',COUNT(*),MAX(OCTET_LENGTH(payload)) FROM chunked; SELECT 'empty_table',COUNT(*) FROM empty_table;" \
  > "$MYSQL_ACCEPT_RUN/source-inventory.tsv"
```

The only executed SQL is the **trusted authored** [seed.sql](seed.sql) and fixed
inventory queries, inside this disposable server. Its `REPEAT` expression creates
synthetic text; exported expressions/functions remain forbidden. No export SQL is
ever executed. Socket-only root access is the disposable image's initialization
mechanism; there is no account provisioning, password or credential discovery.
The entrypoint initializes only its new anonymous volume; `--rm` owns its cleanup.
Require the runtime to enforce the stated CPU/memory/PID limits. If limits are
unavailable or initialization exceeds them, stop this run; do not widen resources
or borrow a running database to obtain a pass.

Require source inventory: `orders=2`, `Line Items=1`, `chunked=1` with payload
**75,000 UTF-8 bytes**, `empty_table=0`; all tables InnoDB/utf8mb4_bin. Server mode
must equal the declared strict set (ordering may differ), timezone `+00:00`,
connection charset `utf8mb4`. No writer, DDL, routines, triggers or events follows
the seed. One single-transaction dump covers all tables; these counts remain stable
through that snapshot. General live-database consistency is outside this recipe.

## 3. Export once, retain immutable bytes, convert and check

**UNRUN on real exports.** `--no-defaults` must be first. `--compact` suppresses
replay DROP/LOCK/key-toggle/charset boilerplate; `--complete-insert` names all
columns; `--hex-blob` prevents raw binary strings; `--order-by-primary` fixes row
order; `--tz-utc` pins TIMESTAMP rendering; `--single-transaction --quick` gives one
streamed InnoDB snapshot. Explicitly omit routines/triggers/events: no such source
objects exist in this fixture. Export status must be zero before conversion.

```bash
MYSQL_ACCEPT_DUMP_BASE=(--no-defaults --protocol=socket -uroot --compact \
  --single-transaction --quick --skip-lock-tables --extended-insert --hex-blob --order-by-primary \
  --default-character-set=utf8mb4 --tz-utc --skip-triggers --skip-routines \
  --skip-events --no-tablespaces)
nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_DUMP" \
  "${MYSQL_ACCEPT_DUMP_BASE[@]}" "${MYSQL_ACCEPT_VENDOR[@]}" --complete-insert \
  adapter_fixture > "$MYSQL_ACCEPT_RUN/snapshot.raw.sql"
test -s "$MYSQL_ACCEPT_RUN/snapshot.raw.sql"
chmod 0400 "$MYSQL_ACCEPT_RUN/snapshot.raw.sql"
printf '%s\n' 'SET NAMES utf8mb4;' "SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO';" \
  "SET TIME_ZONE='+00:00';" > "$MYSQL_ACCEPT_RUN/declarations.sql"
cat "$MYSQL_ACCEPT_RUN/declarations.sql" "$MYSQL_ACCEPT_RUN/snapshot.raw.sql" \
  > "$MYSQL_ACCEPT_RUN/snapshot.profile.sql"
chmod 0400 "$MYSQL_ACCEPT_RUN/declarations.sql" "$MYSQL_ACCEPT_RUN/snapshot.profile.sql"
nice -n 19 sha256sum "$MYSQL_ACCEPT_RUN/snapshot.raw.sql" "$MYSQL_ACCEPT_RUN/snapshot.profile.sql" \
  > "$MYSQL_ACCEPT_RUN/source-sha256.txt"
nice -n 19 python3 -I -B mysql_import.py dry-run \
  "$MYSQL_ACCEPT_RUN/snapshot.profile.sql" "$MYSQL_ACCEPT_RUN/dry-run.json" \
  --source-version "$MYSQL_ACCEPT_FLAVOR"
nice -n 19 python3 -I -B mysql_import.py convert \
  "$MYSQL_ACCEPT_RUN/snapshot.profile.sql" "$MYSQL_ACCEPT_RUN/converted" \
  --source-version "$MYSQL_ACCEPT_FLAVOR"
nice -n 19 python3 -I -B acceptance/verify.py bundle \
  "$MYSQL_ACCEPT_RUN/converted" "$MYSQL_ACCEPT_RUN/snapshot.profile.sql" \
  "$MYSQL_ACCEPT_RUN/dry-run.json"
nice -n 19 sha256sum --check "$MYSQL_ACCEPT_RUN/source-sha256.txt"
```

The preamble describes the export's ordinary backslash-escaped literal grammar,
not the server's stricter insert-validation mode. Accepted exporter SET wrappers
may explicitly set `NO_AUTO_VALUE_ON_ZERO` again. No mode/charset is inferred from
values. MariaDB's exact sandbox safety marker is recognized only for MariaDB.
Any unknown gate/SET, schema syntax or expression fails the whole conversion.
Unnamed constraints stay inside CREATE TABLE; named constraints use ADD CONSTRAINT,
the two constraint forms the pinned core recognizes. Foreign targets are checked
against all tables in the artifact, including later declarations.
Retain the original raw export; never delete statements, rewrite escaped values or
drop rejected rows to get a pass. Retry requires another complete export/new run.

The native seed uses positive decimal zero and nonempty binary literals. Native
servers may normalize negative zero; real exporters may render **empty binary as
`''` despite `--hex-blob`**, which this adapter rejects. This recipe does not certify
empty-binary native exports. The preserved synthetic fixture separately tests hex
empty binary `0x`, NULL binary and negative decimal zero. A rejected exporter spelling
is an unresolved compatibility limit, not permission to alter snapshot bytes.

### Negative export profiles — also UNRUN

Use the same quiescent disposable source. Do not upload any negative file. The
following produce fresh raw files rather than modifying the positive snapshot:

```bash
nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_DUMP" \
  "${MYSQL_ACCEPT_DUMP_BASE[@]}" "${MYSQL_ACCEPT_VENDOR[@]}" \
  adapter_fixture > "$MYSQL_ACCEPT_RUN/negative-implicit.raw.sql"
nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_DUMP" \
  "${MYSQL_ACCEPT_DUMP_BASE[@]}" "${MYSQL_ACCEPT_VENDOR[@]}" --complete-insert \
  --add-drop-table --add-locks --disable-keys \
  adapter_fixture > "$MYSQL_ACCEPT_RUN/negative-replay.raw.sql"
nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_DUMP" \
  "${MYSQL_ACCEPT_DUMP_BASE[@]}" "${MYSQL_ACCEPT_VENDOR[@]}" --complete-insert \
  --databases adapter_fixture > "$MYSQL_ACCEPT_RUN/negative-database.raw.sql"
nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_DUMP" \
  "${MYSQL_ACCEPT_DUMP_BASE[@]}" "${MYSQL_ACCEPT_VENDOR[@]}" --complete-insert \
  --skip-hex-blob adapter_fixture > "$MYSQL_ACCEPT_RUN/negative-binary.raw.sql"
nice -n 19 podman exec "$MYSQL_ACCEPT_CID" "$MYSQL_ACCEPT_DUMP" \
  "${MYSQL_ACCEPT_DUMP_BASE[@]}" "${MYSQL_ACCEPT_VENDOR[@]}" --complete-insert \
  --xml adapter_fixture > "$MYSQL_ACCEPT_RUN/negative-xml.raw.sql"
for MYSQL_ACCEPT_NEGATIVE in implicit replay database binary xml; do
  MYSQL_ACCEPT_RAW="$MYSQL_ACCEPT_RUN/negative-$MYSQL_ACCEPT_NEGATIVE.raw.sql"
  MYSQL_ACCEPT_PROFILE="$MYSQL_ACCEPT_RUN/negative-$MYSQL_ACCEPT_NEGATIVE.profile.sql"
  cat "$MYSQL_ACCEPT_RUN/declarations.sql" "$MYSQL_ACCEPT_RAW" > "$MYSQL_ACCEPT_PROFILE"
  chmod 0400 "$MYSQL_ACCEPT_RAW" "$MYSQL_ACCEPT_PROFILE"
  if nice -n 19 python3 -I -B mysql_import.py dry-run "$MYSQL_ACCEPT_PROFILE" \
      "$MYSQL_ACCEPT_RUN/negative-$MYSQL_ACCEPT_NEGATIVE.report.json" \
      --source-version "$MYSQL_ACCEPT_FLAVOR" \
      > "$MYSQL_ACCEPT_RUN/negative-$MYSQL_ACCEPT_NEGATIVE.stdout" \
      2> "$MYSQL_ACCEPT_RUN/negative-$MYSQL_ACCEPT_NEGATIVE.stderr"; then
    exit 1  # Unexpected acceptance: retain private evidence; stop before core.
  else
    MYSQL_ACCEPT_RESULT=$?
    test "$MYSQL_ACCEPT_RESULT" = 1
    test ! -e "$MYSQL_ACCEPT_RUN/negative-$MYSQL_ACCEPT_NEGATIVE.report.json"
  fi
done
```

| Profile | Expected refusal |
| --- | --- |
| Implicit columns | Missing complete column list; never fill defaults/missing values |
| Replay boilerplate | DROP/LOCK/index-toggle statements outside the allowed subset |
| Database wrapper | CREATE DATABASE / USE / qualified database scope unsupported |
| Nonhex binary | Quoted/raw binary unsupported; no lossy UTF-8 interpretation |
| XML | Unsupported source grammar; no alternate server format invented |

The first refusal code/line depends on real exporter ordering; inspect only the
private stderr files. Existing 127 synthetic checks additionally cover compressed/
malformed encoding, zero dates, unsupported SQL modes/types/routines, expression
INSERTs, missing columns, conditional SQL, precision and chunk limits.

## 4. Verify the real core, then compare typed documents

**UNRUN.** No endpoint is available. These commands are for the owned synthetic TLS
fixture only, after source conversion and all negative checks succeed. Supply
`MYSQL_ACCEPT_CORE_URL=https://localhost:<fixture-port>`, its private
`MYSQL_ACCEPT_CORE_CONFIG`, and synthetic `MYSQL_ACCEPT_PROJECT`. The URL/port and
project must come from that already-running fixture, not network/process discovery.
The config may contain only test authorization, trusted CA and
`X-TerraScale-Region-Pin`; no URLs, redirects, proxies, command execution or output
options. Never print it, set shell tracing, or use production credentials.

```bash
: "${MYSQL_ACCEPT_CORE_URL:?owned ready loopback TLS fixture required}"
: "${MYSQL_ACCEPT_CORE_CONFIG:?fixture-owned private curl config required}"
: "${MYSQL_ACCEPT_PROJECT:?synthetic project ULID required}"
: "${MYSQL_ACCEPT_REGION:?owned fixture home region required}"
MYSQL_ACCEPT_PORT=$(nice -n 19 python3 -I -B - "$MYSQL_ACCEPT_CORE_URL" <<'PY'
import sys
from urllib.parse import urlsplit
u = urlsplit(sys.argv[1])
assert u.scheme == 'https' and u.hostname == 'localhost' and u.port
assert not u.username and not u.password and u.path in ('', '/') and not u.query and not u.fragment
print(u.port)
PY
)
MYSQL_ACCEPT_CORE_URL=${MYSQL_ACCEPT_CORE_URL%/}
core_request() {
  local wanted=$1 output=$2 actual
  shift 2
  actual=$(nice -n 19 curl --config "$MYSQL_ACCEPT_CORE_CONFIG" \
    --silent --show-error --max-time 10 --max-redirs 0 --noproxy '*' --proto '=https' \
    --resolve "localhost:$MYSQL_ACCEPT_PORT:127.0.0.1" \
    --output "$output" --write-out '%{http_code}' "$@")
  test "$actual" = "$wanted"
}
MYSQL_ACCEPT_DATABASE=$(nice -n 19 python3 -I -B - <<'PY'
import secrets, time
n = (int(time.time() * 1000) << 80) | secrets.randbits(80)
a = '0123456789abcdefghjkmnpqrstvwxyz'
print('import-' + ''.join(a[(n >> s) & 31] for s in range(125, -1, -5)))
PY
)
MYSQL_ACCEPT_D="$MYSQL_ACCEPT_CORE_URL/v1/projects/$MYSQL_ACCEPT_PROJECT/databases/$MYSQL_ACCEPT_DATABASE"
core_request 201 "$MYSQL_ACCEPT_RUN/database.json" --request POST \
  --header 'Content-Type: application/json' --data "{\"name\":\"$MYSQL_ACCEPT_DATABASE\"}" \
  "$MYSQL_ACCEPT_CORE_URL/v1/projects/$MYSQL_ACCEPT_PROJECT/databases"
nice -n 19 python3 -I -B - "$MYSQL_ACCEPT_RUN/database.json" \
  "$MYSQL_ACCEPT_PROJECT" "$MYSQL_ACCEPT_DATABASE" "$MYSQL_ACCEPT_REGION" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
assert d['projectId'] == sys.argv[2] and d['name'] == sys.argv[3]
assert d['homeRegion'] == sys.argv[4] and d['replicaRegions'] == []
PY
# The private curl config's region pin must equal MYSQL_ACCEPT_REGION.
# Never reuse an existing destination on 409.
nice -n 19 python3 -I -B - "$MYSQL_ACCEPT_RUN" <<'PY'
import json, pathlib, sys
p = pathlib.Path(sys.argv[1])
r = json.loads((p/'converted/report.json').read_text())
(p/'create-import.json').write_text(json.dumps({'format':'postgres_plain',
    'sourceBytes':r['outputBytes'], 'sourceSha256':r['outputSha256'],
    'options':{'missingPrimaryKey':'reject','exclude':[]}}))
PY
check_job_source() {
  nice -n 19 python3 -I -B - "$1" "$2" "$MYSQL_ACCEPT_RUN/create-import.json" \
    "$MYSQL_ACCEPT_PROJECT" "$MYSQL_ACCEPT_DATABASE" "$MYSQL_ACCEPT_REGION" <<'PY'
import json, sys
j, r = json.load(open(sys.argv[1])), json.load(open(sys.argv[3]))
assert (j['project'], j['database'], j['region']) == tuple(sys.argv[4:7])
s = j['source']
assert s['format'] == r['format'] and s['bytes'] == r['sourceBytes'] and s['sha256'] == r['sourceSha256']
assert s['receivedBytes'] == (0 if sys.argv[2] == 'new' else r['sourceBytes'])
PY
}
core_request 201 "$MYSQL_ACCEPT_RUN/created-import.json" --request POST \
  --header 'Content-Type: application/json' --data-binary "@$MYSQL_ACCEPT_RUN/create-import.json" \
  "$MYSQL_ACCEPT_D/import"
check_job_source "$MYSQL_ACCEPT_RUN/created-import.json" new
# The fixture artifact is <4 MiB: one ordered part, offset 0. No raw MySQL upload.
test "$(wc -c < "$MYSQL_ACCEPT_RUN/converted/import.sql")" -le 4194304
core_request 200 "$MYSQL_ACCEPT_RUN/uploaded.json" --request PUT \
  --header 'Content-Type: application/octet-stream' \
  --data-binary "@$MYSQL_ACCEPT_RUN/converted/import.sql" "$MYSQL_ACCEPT_D/import/source?offset=0"
check_job_source "$MYSQL_ACCEPT_RUN/uploaded.json" full
wait_import() {
  local desired=$1 state
  for MYSQL_ACCEPT_ATTEMPT in {1..60}; do
    core_request 200 "$MYSQL_ACCEPT_RUN/job.json" "$MYSQL_ACCEPT_D/import"
    state=$(nice -n 19 python3 -I -B - "$MYSQL_ACCEPT_RUN/job.json" <<'PY'
import json, sys
print(json.load(open(sys.argv[1]))['state'])
PY
)
    if test "$state" = "$desired"; then return 0; fi
    case "$state" in rejected|failed|cancelled|awaiting_source) return 1 ;; esac
    sleep 2
  done
  return 1
}
wait_import ready
cp "$MYSQL_ACCEPT_RUN/job.json" "$MYSQL_ACCEPT_RUN/ready.json"
check_job_source "$MYSQL_ACCEPT_RUN/ready.json" full
nice -n 19 python3 -I -B acceptance/verify.py preview "$MYSQL_ACCEPT_RUN/ready.json"
# Review this local preview's warnings and adapter report before confirming.
nice -n 19 python3 -I -B - "$MYSQL_ACCEPT_RUN" <<'PY'
import json, pathlib, sys
p = pathlib.Path(sys.argv[1])
j = json.loads((p/'ready.json').read_text())
(p/'start.json').write_text(json.dumps({'previewId':j['preview']['id']}))
PY
core_request 202 "$MYSQL_ACCEPT_RUN/started.json" --request POST \
  --header 'Content-Type: application/json' --data-binary "@$MYSQL_ACCEPT_RUN/start.json" \
  "$MYSQL_ACCEPT_D/import/start"
wait_import completed
cp "$MYSQL_ACCEPT_RUN/job.json" "$MYSQL_ACCEPT_RUN/completed.json"
check_job_source "$MYSQL_ACCEPT_RUN/completed.json" full
mkdir "$MYSQL_ACCEPT_RUN/readback"
for MYSQL_ACCEPT_COLLECTION in orders line__items_ chunked empty_table; do
  core_request 200 "$MYSQL_ACCEPT_RUN/readback/$MYSQL_ACCEPT_COLLECTION.json" \
    "$MYSQL_ACCEPT_D/collections/$MYSQL_ACCEPT_COLLECTION/documents?limit=1000"
done
nice -n 19 python3 -I -B acceptance/verify.py completed \
  "$MYSQL_ACCEPT_RUN/completed.json" "$MYSQL_ACCEPT_RUN/readback"
nice -n 19 sha256sum --check "$MYSQL_ACCEPT_RUN/source-sha256.txt"
```

Also require created/uploaded/ready/completed source size/SHA-256 to equal the
**converted artifact**, with exact received byte count; project/database/region
must equal the recorded destination. Keep these responses locally as evidence.
`sourceVersionVerified=false` remains truthful even after this external recipe.

### Exact expected values, counts and checksums

[expected.json](expected.json) is the oracle: column tuples are
`[original name, PostgreSQL type, core mapping, nullable]`; document values retain
their JSON types. Its sole `$repeat` notation means the literal string `漢` repeated
25,000 times, not an expression sent to the core. No columns are excluded or missing.

| Collection | Count / exact document ids | Core checksum (both source and readback) |
| --- | --- | --- |
| `orders` | 2: `0`, `18446744073709551615` | `3d55db667a1e8c18b490792bc247197ce519fd10bf2b3c512326048540fd681d` |
| `line__items_` | 1: `18446744073709551615:x%3A%25` | `f765268918899d7fa33a0cef22ee900ea734b1c388633f027cbc9e4383e06f30` |
| `chunked` | 1: `100` | `97931feea9aea377947ea0ef79f7b2ac499ec765e2b5328c3e62c48cfd8e0070` |
| `empty_table` | 0 | `0000000000000000000000000000000000000000000000000000000000000000` |

Unsigned BIGINT and decimal are strings; signed `a\`b=1` and `chunked.id=100` are
numbers, not booleans or strings. `orders.note` is literal `\N` versus JSON null;
empty label/SET are empty strings. Binary `00 0a 5c ff`, `ff ff ff ff`, `01` become
`AApc/w==`, `/////w==`, `AQ==`; nullable binary stays null. DATE stays a date string,
DATETIME gets `T` with no timezone, TIMESTAMP gets `T` and `Z`, and TIME remains a
signed duration string. Fractional spelling is exact, including `.000000`.

The checksum oracle follows pinned `ImportMapping.Add`/`ExactNumber`: remove a
top-level string `id` only if it equals the document key; sort object names with
ordinal order; normalize numbers to mantissa/exponent (`100` → `1e2`); SHA-256 over
UTF-8 id + NUL + compact canonical body; sum terms modulo 2^256. All fixture keys
are ASCII and text uses ordinary BMP letters/C0 escapes, avoiding differing Unicode
encoder edge cases. These locally calculated expected hashes are **not yet core
acceptance evidence**, and are distinct from report `copyDataSha256` row-byte hashes
and artifact `outputSha256`. Export byte hashes are recorded per immutable export;
they are not pinned across different exporter headers/spellings.

The preview must show all four exact table names, primary keys, columns/mappings,
counts, no exclusions/skips/blocking issues and non-enforced constraints. Completion
must have 4 tables done, 4 rows written, validation passed and all table matches;
readback must contain exactly the expected ids and typed bodies, including the empty
collection. Constraints, defaults, ON UPDATE, AUTO_INCREMENT, collation and ordinary
indexes are not enforced/transferred as source behavior; ENUM/SET membership becomes
text. Review the adapter loss warnings rather than infer relational enforcement.

## 5. Clean only this run's disposable resources

**UNRUN.** Keep exports, checksums, image/version inventory and private acceptance
responses for review. Do not delete the run directory or original fixtures.

```bash
# Verify recorded database.json belongs to this synthetic project and fresh name
# before deletion. Deleting any other database is outside this recipe.
nice -n 19 python3 -I -B - "$MYSQL_ACCEPT_RUN/database.json" \
  "$MYSQL_ACCEPT_PROJECT" "$MYSQL_ACCEPT_DATABASE" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
assert d['projectId'] == sys.argv[2] and d['name'] == sys.argv[3]
assert d['replicaRegions'] == [] and sys.argv[3].startswith('import-')
PY
core_request 200 "$MYSQL_ACCEPT_RUN/cleanup-state.json" "$MYSQL_ACCEPT_D/import"
MYSQL_ACCEPT_STATE=$(nice -n 19 python3 -I -B - "$MYSQL_ACCEPT_RUN/cleanup-state.json" <<'PY'
import json, sys
print(json.load(open(sys.argv[1]))['state'])
PY
)
case "$MYSQL_ACCEPT_STATE" in
  analyzing|queued|running|validating)
    core_request 202 "$MYSQL_ACCEPT_RUN/cancelled.json" --request POST "$MYSQL_ACCEPT_D/import/cancel"
    wait_import cancelled
    ;;
esac
core_request 204 "$MYSQL_ACCEPT_RUN/deleted.json" --request DELETE "$MYSQL_ACCEPT_D"
# If only the fresh database exists and no import was created, omit GET/cancel and
# delete that recorded database only. Before database creation, omit core cleanup.

test "$(nice -n 19 podman inspect --format \
  '{{index .Config.Labels "terrascale.mysql-adapter-fixture"}}' "$MYSQL_ACCEPT_CID")" = "$MYSQL_ACCEPT_TAG"
nice -n 19 podman stop --time 10 "$MYSQL_ACCEPT_CID"
# --rm removes only this container and its newly allocated anonymous volume.
# If it already auto-removed after an error, inspect its recorded ID; never prune.
nice -n 19 sha256sum --check "$MYSQL_ACCEPT_RUN/source-sha256.txt"
```

Core database deletion removes this job/staged source; it does not merge/deploy.
On a queued/running failure, use only this run's `POST .../import/cancel`, wait for
`cancelled` before deleting, and retain failure evidence. On interrupted source
startup, inspect the exact cidfile/label and stop that container only. Never stop
other listeners/builds, remove shared worktrees/volumes, or run global pruning.

The cheap read-only oracle self-check is available now:

```bash
nice -n 19 python3 -I -B tools/import-adapters/mysql/acceptance/verify.py self-check
```

It substitutes a known literal into the trusted seed to make **synthetic** export
text, checks inline-primary-key/core ALTER grammar, dry-run/COPY values/counts/checksums,
and exercises constructed API
responses plus malformed typed/missing values and a checksum mismatch. It starts
no server and proves no real-export or TerraBase acceptance. Publication remains
queued behind separate serial admission; no push or PR is authorized here.
