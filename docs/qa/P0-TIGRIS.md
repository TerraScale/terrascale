# P0 2026-11-03: Tigris buckets deleted, TerraBase catalog gate closed

Impact: dash.terrascale.tech sign-in returned 503 "Sign-in through ts-auth did not finish" from
about 01:27Z until 03:29:37Z (about 2 h). gru TerraBase answered 503 `catalog_unverified` on every
authorized endpoint from 02:18Z, and sjc did the same from about 01:46Z. All production data was
reset. The old data directories are kept on each host (see Recovery).

## Root cause

1. Every Tigris bucket of the `terrascale` Fly organization was deleted around 01:38Z:
   `terrascale-gru-terrabase-backups-b12` (production backups and catalog anchors),
   `ts-terrabase-ci`, `ts-terrabase-bench` and `ts-terrabase-dev`. `fly storage list` showed no
   buckets in either organization (`terrascale`, `personal`).
2. TerraBase keeps each machine's catalog restore anchor (`catalog-anchor/<machine>`) in that bucket.
   A started machine opens its catalog only when the anchor matches its local `_system` incarnation
   and offset, or when the machine is fresh (no `_system`). A machine with local data and no anchor
   stays closed by design (anti-resurrection, `src/TerraBase/Features/Catalog/CatalogGate.cs`).
   No operator override exists: R10a removed `POST /admin/catalog-gate/open` (9a6062c9a).
3. Releases restarted sjc (01:46Z) and gru (02:18Z) into the closed gate. ts-orch then restarted them
   every 30 minutes for failing readiness. ts-auth in gru stores its data in gru TerraBase, so
   sign-in failed. fra stayed open only because its process had started 80 h earlier.
4. The missing bucket was silent. `ObjectStore.ReadAsync` treats every S3 404 as "object missing",
   including `NoSuchBucket`. The gate therefore logged no warning and kept waiting for an anchor that
   could not appear.

TerraBase did not delete the bucket. Its only DeleteBucket call removes a Tigris snapshot by version,
and the old bucket had no snapshots enabled ("The backup bucket has no Tigris snapshots enabled" in
fra's 2026-10-30 log), so the call never ran.

## Who deleted the buckets

Investigated to completion on 2026-11-03 (TS-1379). **No local actor deleted them.** Every automated
system that held a Fly credential was examined transcript-by-transcript for the window 00:55Z–01:50Z
(and, for bucket-destructive commands, every session ever recorded). Nothing deleted a bucket, and
nothing could have: the deletions span two Fly organizations at once, which requires an
account-scope credential, and no account-scope credential was ever used destructively from any host
we control.

What was checked, beyond the hosts and GitHub Actions already cleared above:

- **Hermes bot VM** (`ssh alpha` → `incus exec mariogk-hermes`), missed by the first pass: eight
  profiles (`aide`, `chief`, `terrascale`, `ops`, `bryta`, `news`, `lab`, `plana`), every message of
  every session in 00:55Z–01:50Z plus a full-history sweep of all `state.db` transcript stores for
  `fly storage`, `s3 rb`, `DeleteBucket` and `mcp__fly__*` calls. All activity in the window is
  read-only diagnostics, kanban routing, mail/DNS/TOTP work and a Facebook Marketplace session. The
  only `mcp__fly__*` call ever recorded anywhere is one read-only `fly_apps_list` at 04:04Z, hours
  after the incident. The `empty_and_delete_bucket*.sh` scripts in `files/cloud-cleanup/` are the
  Linode closure of 2026-11-01/02, pointed at Linode Object Storage endpoints, not Tigris.
- **Cron jobs** of every profile: mail, market and board watchdogs only; none touches Fly.
- **The Fly MCP servers** (flyctl `mcp server`, registered for `aide` and `terrascale` profiles):
  they expose destroy tools, but the sweep above shows no destructive call was ever made through
  them.
- **Claude Code / Codex / opencode transcripts on mariogk-dev** for the window: the last TerraScale
  project session ended 18:41Z on 2026-11-02; none intersects the deletion window.
- **Whole-VM text sweep** of `/srv/hermes/home`, `/root` and `/home` for bucket-destructive command
  text: three hits, all documentation sentences about the destroyed `vw-mariogk` Fly app.

The only credential on our hosts that can reach both organizations is the Fly **web-session token**
(`FLY_ACCESS_TOKEN`, 1925 chars, `admin@terrascale.tech`, from Infisical `Personal/personal` /
`Personal/hermes-personal`, exported into the Hermes profiles' shared env). The org-scoped tokens
(`ts-terrabase-ci` CI key, backup keys, the `hermes-terrascale-fly-20261102` org token) are each
bound to one organization and cannot explain both orgs losing buckets in the same minute. The same
account's `flyctl` SSO state (Infisical `Personal/hermes-state` `/fly-cli` `FLY_CONFIG_YAML`) is a
browser-session token from 2026-08-21, also account-scope, also never used destructively in any
transcript.

**Conclusion (proven for every host we control):** the deletion did not come from TerraBase, CI, or
any bot or interactive session on delta, beta, mariogk-dev or the Hermes VM. It was performed with
an account-scope identity of `admin@terrascale.tech` — the Fly web console/API with the account's
own login, or the account's web-session token held by a system outside our audit trails. The
provider-side audit log named below is the only remaining evidence source; short of it, treat the
account's admin login itself as the deletion vector and rotate the web-session tokens
(`Personal/personal`, `Personal/hermes-personal`, `Personal/hermes-state /fly-cli`) so no automation
holds an account-scope Fly credential again.

Provider-side evidence is thin: Fly GraphQL has no organization audit log, and the org's add-on
list shows only the three buckets recreated at 03:23Z. `ts-terrabase-dev` cannot be recreated yet
("bucket name is not available") even though its old key gets `NoSuchBucket`; the name may still be
reserved after a deletion. Remaining step for the owner: the Tigris console audit log
(`fly storage dashboard`, browser SSO as admin@terrascale.tech) — look for DeleteBucket events
between ~01:27Z and 01:38Z and their access key — or Fly/Tigris support.

## Timeline (UTC, 2026-11-03)

| Time | Event |
| --- | --- |
| 01:17:59 | CI TerraBase release starts (run 41557992752, cancelled 01:33) |
| ~01:27 | COS sees dash `/login` 503 |
| ~01:38 | Investigator finds `terrascale-gru-terrabase-backups-b12` → `NoSuchBucket` |
| 01:44:34 | Next TerraBase release (run 41560973563). sjc restarts at 01:46 into a closed gate |
| 02:18:23 | gru TerraBase restarts and stays closed: `catalog_unverified`, then a 30-minute readiness loop |
| 03:08:54 | Another release starts. sjc bakes, fra is not reached (the job hits its 15-minute timeout at 03:23) |
| 03:23:35–47 | Buckets recreated in `terrascale`. Keys go straight into OCI Vault, GitHub Actions and the dev Vault |
| 03:27:22 | `/var/lib/ts-data/terrabase` moved to `terrabase.p0tigris-20261103` on ts-gru-1, ts-sjc-1, ts-fra-1 |
| 03:27:31–34 | TerraBase tasks stopped (fra, sjc, gru). Fresh starts anchor to the new bucket, and gru seeds platform databases at 03:27:39 |
| 03:29:21 | ts-auth and ts-dashboard restarted (stale pre-reset database IDs) |
| 03:29:37 | auth and dash `/readyz` 200, dash `/login` 302 to ts-auth |
| 03:35–03:41 | Real sign-in and authorized reads proven in every region |
| 03:47 | mariogk@ Demo re-seeded (shop, blog, iot, sql-demo) |
| 03:49:55 | sjc restarted on purpose. It reopens from its anchor in about 1 minute |

## Recovery

- Buckets: `fly storage create -n <name> -o terrascale -y` for the three names that were free.
  Output went to a parser that wrote keys into new versions of these, with no value printed:
  - `gru-1-terrabase-backup-access-key-id` and `gru-1-terrabase-backup-secret-key` (gru Vault, v2)
  - `dev-tigris-ci-env` and `dev-tigris-bench-env` (dev Vault, v2)
  - the `TIGRIS_CI_CREDENTIALS` GitHub secret
  Put, list and delete were verified with each new key.
- Re-anchor: none is sanctioned (see Root cause 2). Following the user's standing reset authority,
  every region's TerraBase data was reset at once. A partial reset would let fra's old catalog flow
  into fresh peers, and fra would close on its next restart anyway, since its old keys died with the
  bucket. The old directories remain at
  `/var/lib/ts-data/terrabase.p0tigris-20261103` (gru 339 MB, sjc 16 MB, fra 23 MB) for later
  inspection. Their files stay encrypted under `gru-1-terrabase-master-key`. The serving
  infrastructure was not touched.
- Every account, organization, project, database and API key created before 03:27Z is gone.
  Users must sign up again.

## Proof

- `fly storage list -o terrascale`: `terrascale-gru-terrabase-backups-b12`, `ts-terrabase-ci`,
  `ts-terrabase-bench`.
- The new backup bucket holds `catalog-anchor/ts-gru-2`, `catalog-anchor/ts-sjc-1` and
  `catalog-anchor/ts-fra-1`, plus `v1/_system`, `v1/keys` and platform database segments.
- `https://dash.terrascale.tech/login` → 302 to `auth.terrascale.tech/terrascale/oauth/authorize`.
- mariogk+glm-p0@terrascale.tech: hosted sign-up, email code (IMAP), sign-in code, onboarding, then
  dashboard home `GET /` 200 "Overview" (organization Personal, project P0).
- Project key (client credentials): databases `p0-gru`, `p0-sjc`, `p0-fra` created in their home
  regions. A document write (201 `local_durable`) and read (200) succeed on each regional host,
  answered with `x-terrabase-region` gru, sjc and fra. The documents read back after sjc's restart.
- mariogk@terrascale.tech: organization Personal on the Starter plan (Stripe sandbox checkout),
  project Demo (`01M6FQYZ334XHFM77Z2GHBN9WN`). seed-demo created shop, blog, iot and sql-demo and
  ran 600 traffic requests.

## Follow-ups

1. **Silent missing bucket** (TerraBase): in `ObjectStore.ReadAsync`, rethrow `NoSuchBucket`
   instead of returning "not found". That gives a gate warning and a failing restore instead of an
   empty answer. Alert on `terrabase.catalog.verified == 0` for more than 5 minutes, and on any
   backup PUT failure.
2. **Sanctioned re-anchor** (TerraBase/COS decision): an operator command, with a recorded reason,
   that re-anchors an intact local catalog after a verified bucket loss. With it, local data survives
   the next bucket loss instead of needing a reset.
3. **Bucket deletion guard** (done in code, TS-1379): `S3ObjectStore.DeleteSnapshotAsync` — the
   only DeleteBucket call — now refuses any snapshot version that is not a plain number with a typed
   `BucketDeleteForbiddenException` and an error log, before a request leaves the process, so the
   header can never be dropped and turn snapshot expiry into a bucket delete. Covered by
   `tests/TerraBase.Fast/Backup/BucketDeleteGuardTests.cs`. Still open on the credential side:
   prefer read/write-only keys if Tigris supports them, keep an independent copy (Tigris shadow
   bucket or R2 mirror), make the org's admin login the only identity with bucket-admin rights, and
   check daily that the expected buckets exist (`fly storage list -o terrascale`).
4. **ts-status canaries** stay "Canary credential invalid". `scripts/recover-canaries.py` creates an
   `allRegions` database, which TerraBase now refuses (`replicas_unavailable`). It also needs the
   internal `terrascale` organization (business plan) and its internal billing. Owner: ts-status lane.
5. **Dashboard database creation is blocked**: the create form requires at least two regions, while
   TerraBase refuses replicas, so the dashboard cannot create any database (422 or 400). Databases
   for this proof were made through the project API.
6. **Version skew**: sjc runs the release from 03:08 (`…c397092f68`), gru and fra the earlier image
   (`…b14d88c560a5`). The next TerraBase release converges them. Restarts are safe now that anchors
   exist.
7. **Reservation leak** (reported by COS: 75.5 GB of disk reservations for 351 MB of data, plus the
   memory reservation cap) did not block recovery. Owner: ts-orch lane.
8. `ts-terrabase-dev` could not be recreated (name unavailable). Local
   `~/.config/terrascale/tigris-dev.env` still points at it. `tigris-ci.env` and `tigris-bench.env`
   on delta were refreshed from the dev Vault.
9. `main-auth` releases have failed since 01:04Z (readiness 503 within 1 m). This incident made it
   worse, but it is not proven to be the cause. Check the next ts-auth release.
10. The sign-in 503 from 01:27Z to 02:18Z happened while gru's gate was still open. The ts-auth and
    TerraBase logs for that window have rotated, so the exact first failure is unknown.
