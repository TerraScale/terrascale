# Fly greenfield rebuild — 2026-10-13

Status: T0 PR #206 merged into main `de244e66`; this combined candidate is rebased onto that exact main. One new full required gate follows. No rebuild deployment yet.

## Authorization and ownership

User authorized deletion of every current machine and volume of `ts-terrabase`, `ts-auth` and `ts-dashboard`, the unused builder and its resources, old snapshots and all Tigris buckets in TerraScale. Personal Tigris resources are excluded.

`flyctl auth whoami` returned `admin@terrascale.tech`. `flyctl apps list --json` identified all four inventoried apps as organization `terrascale` (name `TerraScale`). Inventory was captured before deletion.

## Deleted machines

| App | Region | Machine ID | Name |
|---|---|---|---|
| `ts-terrabase` | gru | `48e7454a407068` | hidden-hill-7544 |
| `ts-terrabase` | sjc | `d8929e4b3ded08` | divine-silence-1711 |
| `ts-terrabase` | fra | `8747ed7f619628` | white-moon-3382 |
| `ts-auth` | gru | `80e953eb6d6468` | dawn-night-4867 |
| `ts-auth` | sjc | `853064c4699508` | autumn-fire-3831 |
| `ts-auth` | fra | `d8d99dd2a19668` | dawn-pine-2181 |
| `ts-dashboard` | gru | `7817963c643298` | restless-fire-3190 |
| `fly-builder-late-star-2225` | gru | `e827941c4e1378` | patient-surf-574 |

Each was destroyed with `flyctl machine destroy <id> -a <app> --force`; Fly returned `<id> has been destroyed`.

## Deleted volumes

| App | Region | Volume ID | Name | Size |
|---|---|---|---|---|
| `ts-terrabase` | gru | `vol_rnzgjwgg509lg8er` | `terrabase_data` | 1 GB |
| `ts-terrabase` | fra | `vol_4m3mw8lpjg9lpgdv` | `terrabase_data` | 1 GB |
| `ts-terrabase` | sjc | `vol_rnzgjzlkzmdjw7er` | `terrabase_data` | 1 GB |
| `ts-terrabase` | gru | `vol_rkgo6l5875md5924` | `keep_gru_0247` | 1 GB |
| `ts-terrabase` | sjc | `vol_4ql79o02wokk5qdr` | `keep_sjc_0302` | 1 GB |
| `ts-terrabase` | gru | `vol_v8e0joze0kqqn7kv` | `inv_gru_now` | 1 GB |
| `ts-terrabase` | sjc | `vol_v3gpe9z2xpjy3em4` | `inv_sjc_now` | 1 GB |
| `ts-terrabase` | fra | `vol_4ql79oz1eywd9zdr` | `inv_fra_now` | 1 GB |
| `ts-terrabase` | gru | `vol_458o67o1ldo6x994` | `terrabase_data` | 1 GB |
| `fly-builder-late-star-2225` | gru | `vol_vden6n7ompzl0914` | `machine_data` | 50 GB |

Each was destroyed with `flyctl volumes destroy <id> -a <app> --yes`; Fly returned `Destroyed volume ID: <id> name: <name>`. Auth and dashboard had no volumes.

The builder application was also deleted: `flyctl apps destroy fly-builder-late-star-2225 --yes` → `Destroyed app fly-builder-late-star-2225`. The three product app identities were retained.

## Deletion proof

After deletion, each command below returned `[]`:

```text
flyctl machine list -a ts-terrabase --json => []
flyctl volumes list -a ts-terrabase --json => []
flyctl machine list -a ts-auth --json => []
flyctl volumes list -a ts-auth --json => []
flyctl machine list -a ts-dashboard --json => []
flyctl volumes list -a ts-dashboard --json => []
flyctl apps list --json => fly-builder-late-star-2225 absent
```

## Retained Fly snapshots — not deleted

The 17 inventoried snapshots remain recoverable after volume deletion. Fly exposes snapshot list/create, but no supported delete command or documented delete endpoint. [Fly snapshot documentation](https://fly.io/docs/volumes/snapshots/) states: “snapshots are only removed at the end of their retention periods.” Changing volume retention would affect only new snapshots. No unsupported API was invoked.

| Deleted source volume | Snapshot ID | Created (UTC) | Retention |
|---|---|---|---|
| `vol_458o67o1ldo6x994` | `vs_DgxAo7GNVoZ3CJz9OD7ebk7` | 2026-10-12T12:33:31Z | 60 days |
| `vol_458o67o1ldo6x994` | `vs_xy18qlKRAqe7Fa5gxBkXJDRA` | 2026-10-13T03:33:49Z | 60 days |
| `vol_4m3mw8lpjg9lpgdv` | `vs_o0VvQNDLJp7ULqqbDg8b` | 2026-10-12T03:20:04Z | 60 days |
| `vol_4m3mw8lpjg9lpgdv` | `vs_aLoMjYn75z9UmqLbMyxP` | 2026-10-13T02:24:12Z | 60 days |
| `vol_4ql79o02wokk5qdr` | `vs_G4R3v6Z7459cgkYAeb0meY` | 2026-10-12T03:27:39Z | 60 days |
| `vol_4ql79o02wokk5qdr` | `vs_4Q0kP3DKQJATZMK90m3BR8` | 2026-10-13T03:23:27Z | 60 days |
| `vol_4ql79oz1eywd9zdr` | `vs_1MJ7XbxZwA1pUZ4lB38GNXRj` | 2026-10-13T03:24:56Z | 60 days |
| `vol_rkgo6l5875md5924` | `vs_gk7Gv4nYle1kToQYG7KyXvJX` | 2026-10-12T03:27:39Z | 60 days |
| `vol_rkgo6l5875md5924` | `vs_vYAgom6a3PQYi2Rq5Y3xqw0o` | 2026-10-13T03:22:46Z | 60 days |
| `vol_rnzgjwgg509lg8er` | `vs_mJKXazlDZqkLcyplVmo3b` | 2026-10-12T02:09:35Z | 30 days |
| `vol_rnzgjwgg509lg8er` | `vs_vyV4pRADnvYNs2yXy8Ooq` | 2026-10-12T02:14:52Z | 30 days |
| `vol_rnzgjwgg509lg8er` | `vs_wo5A91JvymnLc0zxBjP4Z` | 2026-10-12T03:20:12Z | 60 days |
| `vol_rnzgjwgg509lg8er` | `vs_gg5xX27DJKkZfxD3wwwG` | 2026-10-13T01:49:12Z | 60 days |
| `vol_rnzgjzlkzmdjw7er` | `vs_aZ7LYwk1QkafmZpvYBXQVZm` | 2026-10-12T03:19:54Z | 60 days |
| `vol_rnzgjzlkzmdjw7er` | `vs_mGlpXBVNqV8SXgZwlPNlA1` | 2026-10-13T03:02:42Z | 60 days |
| `vol_v3gpe9z2xpjy3em4` | `vs_5zzA4k0bjVPetNlv7LwB7abg` | 2026-10-13T03:24:09Z | 60 days |
| `vol_v8e0joze0kqqn7kv` | `vs_avDlpVJYZ15qh6oYkg6z` | 2026-10-13T03:24:15Z | 60 days |

Proof after deletion: `flyctl volumes snapshots list vol_rnzgjwgg509lg8er -a ts-terrabase --json` still lists its four snapshots with `status: created`. COS accepted retention expiry; early deletion is no longer a blocker.

## Completed Fly-managed Tigris cleanup

Deleted all 20 inventoried old buckets with `flyctl storage destroy <name> -y` (all exit0). Both production and preview storage inventories were empty before any fresh creation. No personal organization access. The preview org remains empty. OAuth stopped; pending question answered and no pending question remains. There was no refreshed Tigris config to propagate.

Final fresh private bucket: `ts-terrabase-backups-20261013-v2`, organization `terrascale`, app `ts-terrabase`. Five newly issued bucket secret values were captured in memory at creation and delivered directly to `flyctl secrets import --stage -a ts-terrabase` stdin. No values were printed or persisted. AWS access-key digest `ab9683204b18cf01`, secret-key digest `de16049033c5a312`, bucket-name digest `14e1a3e92e095afa`; endpoint and region metadata remain the vendor constants. No machines created/deployed. TerraBase master-key rotation remains for the rebuild phase. Auth master digest `736542cfbf684a4c` preserved.

## Every old bucket deleted

| Name | Organization |
|---|---|
| `ts-terrabase-dev` | `terrascale` |
| `ts-terrabase` | `terrascale` |
| `terrascale-ts-pr-118` | `terrascale-previews-515` |
| `terrascale-ts-pr-119` | `terrascale-previews-515` |
| `terrascale-ts-terrabase-e2e` | `terrascale-previews-515` |
| `terrascale-ts-pr-106` | `terrascale-previews-515` |
| `terrascale-ts-pr-79` | `terrascale-previews-515` |
| `terrascale-ts-pr-120` | `terrascale-previews-515` |
| `terrascale-ts-pr-121` | `terrascale-previews-515` |
| `terrascale-ts-pr-108` | `terrascale-previews-515` |
| `terrascale-ts-pr-111` | `terrascale-previews-515` |
| `terrascale-ts-pr-88` | `terrascale-previews-515` |
| `terrascale-ts-pr-112` | `terrascale-previews-515` |
| `terrascale-ts-pr-94` | `terrascale-previews-515` |
| `terrascale-ts-pr-113` | `terrascale-previews-515` |
| `terrascale-ts-pr-97` | `terrascale-previews-515` |
| `terrascale-ts-pr-115` | `terrascale-previews-515` |
| `terrascale-ts-pr-114` | `terrascale-previews-515` |
| `terrascale-ts-pr-116` | `terrascale-previews-515` |
| `terrascale-ts-pr-117` | `terrascale-previews-515` |

## Fresh resources created during preparation

| Bucket | State |
|---|---|
| `ts-terrabase-greenfield-20261013` | Created empty, then deleted |
| `ts-terrabase-backups-20261013` | Created empty, then deleted |
| `ts-terrabase-backups-20261013-v2` | Created private; final production bucket |

Two empty preparatory buckets were removed while fixing Fly credential delivery. Fly initially skipped old secret names; clearing those names alone still left installation incomplete. The final creation-time handoff explicitly stages all five credentials and is verified by destination metadata. Total bucket deletions:22 (20 old +2 preparatory). Total successful bucket creations:3 (only final bucket retained). Same-name ts-terrabase initial create exited1 and created nothing.

Proof: `fly-storage-cleanup-20261013.log` records UTC inventory/mutation commands and exit codes; `fly-storage-final-proof-20261013.log` records final storage/app binding/secret metadata command outputs. Credential-bearing creation output was suppressed.

## Completed preview resource cleanup

COS extended the delete-everything approval to all18 inventoried preview apps and their volumes. The preview organization is terrascale-previews-515; personal resources are excluded. Inventory was saved before mutation. The17 PR apps below were already absent at this new inventory; this thread did not delete or recover their resources.

## Apps already absent

| App | State at new inventory |
|---|---|
| `ts-pr-106` | Already absent |
| `ts-pr-108` | Already absent |
| `ts-pr-111` | Already absent |
| `ts-pr-112` | Already absent |
| `ts-pr-113` | Already absent |
| `ts-pr-114` | Already absent |
| `ts-pr-115` | Already absent |
| `ts-pr-116` | Already absent |
| `ts-pr-117` | Already absent |
| `ts-pr-118` | Already absent |
| `ts-pr-119` | Already absent |
| `ts-pr-120` | Already absent |
| `ts-pr-121` | Already absent |
| `ts-pr-79` | Already absent |
| `ts-pr-88` | Already absent |
| `ts-pr-94` | Already absent |
| `ts-pr-97` | Already absent |

## Resources deleted by this thread

App: `ts-terrabase-e2e` (organization terrascale-previews-515).

| Machine ID | Region | Name |
|---|---|---|
| `82d1d00a7954d8` | gru | floral-mountain-5937 |
| `2872653f603148` | fra | red-brook-2011 |
| `82237ec7e20648` | sjc | still-lake-6617 |

| Volume ID | Region | Name | Size |
|---|---|---|---|
| `vol_rnzg5kw6wz9ney0r` | gru | terrabase_data | 1GB |
| `vol_4y8znojygg575k3r` | sjc | terrabase_data | 1GB |
| `vol_40oj6zk22gowxmk4` | fra | terrabase_data | 1GB |
| `vol_42k9wd5gxo10zx34` | gru | terrabase_data | 1GB |
| `vol_4y8zng27xxwkej3r` | sjc | terrabase_data | 1GB |
| `vol_vxmz6q02700g2184` | fra | terrabase_data | 1GB |

## Proof

```text
flyctl machine destroy <inventoried-id> -a ts-terrabase-e2e --force => <id> has been destroyed (all3)
flyctl volumes destroy <inventoried-id> -a ts-terrabase-e2e --yes => Destroyed volume ID: <id> name: terrabase_data (all6)
flyctl apps destroy ts-terrabase-e2e --yes => Destroyed app ts-terrabase-e2e
flyctl apps list -o terrascale-previews-515 --json => []
```

All6 volume snapshot lists returned[] before deletion. Exact UTC command/output log: preview-deletion.log. Raw inventory: preview-apps.json (historical18), preview-apps-before.json (remaining1), preview-e2e-machines-before.json, preview-e2e-volumes-before.json, preview-vol_*-snapshots-before.json; final proof: preview-apps-after.json.


## Bootstrap and deployment contract

Organization `00000000000000000000000001` is named `TerraScale`. Startup always seeds storage without calling auth. Auth owns identity records and owner membership.

| Service | Project ULID | Database ULID | Database name | Placement |
|---|---|---|---|---|
| ts-auth | `00000000000000000000000002` | `00000000000000000000000003` | `ts-auth` | gru home; sjc, fra replicas |
| ts-dashboard | `00000000000000000000000005` | `00000000000000000000000004` | `ts-dashboard` | gru home; sjc, fra replicas |

Both fixed projects also receive version-1 catalog bindings to the global issuer `https://auth.terrascale.tech/terrascale`, organization org01 and authority organization org01, without auth startup dependency. Existing conflicting bindings are refused. Matching scoped project tokens can resolve their database; project/database/scope/signature validation remains enforced.

Existing startup seeding is reused and remains idempotent with fixed ids/timestamps, conflict protection and background retry. Explore routes resolve each database under its own project. Greenfield name/project changes have no migration or fallback. T3 owns the auth storage-name update; T4 owns dashboard recreation and its project/name update.

The PR removes the production deploy `false &&` guard while preserving push/main identity, the exact-tree Fast gate and gru → sjc → fra live/backup checks. UTC start/end timestamps are added to regional rollout output and GitHub job summaries for T6. The CI run URL will be recorded when deployment is authorized after T0.

Previews remain paused; the preview app and bucket inventories are empty. T6 owns repairing and re-enabling previews after this rebuild; working previews have not been proved.

## Validation

Focused platform proof on mariogk-dev at `7153c0ea` (tree `0ee126555af6eb8fbc3893fe031e78a939474d24`): `~/.local/bin/ts-heavy dotnet run --project tests/TerraBase.Fast -- --treenode-filter '/*/*/Platform*Tests/*'` returned exit 0, 13 succeeded, 0 failed, 0 skipped. This covers startup/restart/concurrent seed, fixed placement, project separation, scoped global-issuer access and refusal of conflicting bindings. The prior 9-pass/4-fail run compared submitted JSON bytes to storage-canonicalized bytes; the correction compares persisted bytes before and after the refused seed. Both raw logs are retained in `.cos/t2-greenfield-evidence/` outside the repository.

T3 owns the single full `~/.local/bin/ts-heavy make check`, using isolated server and SDK (`78e311f`) worktrees on mariogk-dev. The v3 run on the same tree passed formatting, solution build (0 warnings/errors), library checks, auth audit/OpenAPI, TerraBase analyzer checks, benchmark race tests, Endpoints 88/88 and Logging 46/46. Auth Fast finished 667/668: the signup-concurrency test counted the new synthetic bootstrap owner as a second signup account. Its exact redemption-response assertions passed. Test-only correction `92d6a305` requires one signup account, one synthetic owner and no plaintext password on the signup account. V3 failed and did not reach TerraBase Fast; earlier stopped runs are superseded/incomplete, with no full-gate credit. The sole frozen-tree delta run then finished exit 2: Auth 668/668, Endpoints 88/88, Logging 46/46 and Turso 6/6 passed; TerraBase 1515/1565 passed with 50 failures. T0 #206 addresses that baseline, including system-catalog replication and cursor recovery. The rebased candidate receives one new full gate; no prior failed/stopped run is credited.

`bash -n scripts/fly/deploy.sh scripts/fly/preview.sh` and `git diff --check` passed. `nice -n 19 bash scripts/fly/deploy-test.sh` returned `deploy: all checks passed`; `nice -n 19 bash scripts/fly/volume-reset-test.sh` returned `volume-reset: all checks passed`. Workflow actionlint passed with shellcheck disabled because the local shellcheck shim is unconfigured.

## Rebuild after the required green gate

Tigris cleanup is complete and snapshot expiry is accepted. Generate the remaining new secrets directly into Fly/GitHub secret stores without printing values. T3 has already staged a new `TS_AUTH_MASTER_KEY` on ts-auth (reported digest `736542cfbf684a4c`); preserve it and never rotate it again during this rebuild. T2 owns the audit signing key and deployment credentials. Create fresh volumes and exactly one shared-cpu-2x, 512 MB, 4 GB swap, always-running machine per service/region; configure auto-extend and verify scheduled daily snapshots. Merge only after T0 and the latest-commit required test gate. Deploy through CI and retain UTC regional logs/run URL. Verify actual region in each health response using `fly-prefer-region`, then measure both seeded databases replicating across gru/sjc/fra. Placement metadata and isolated seed tests do not prove live replication.

Cleanup totals: 11 machines, 16 volumes, 2 apps and 22 bucket deletions (20 old plus 2 empty preparatory buckets). One fresh private production bucket remains, with new storage credentials staged. No fresh machine/volume, rebuild merge/deployment or live health/replication proof yet.
