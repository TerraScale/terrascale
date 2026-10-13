# Fly greenfield rebuild — 2026-10-13

Status: **WAITING ON T0**. Cleanup and bootstrap PR preparation only; no new deployment or merge.

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

Proof after deletion: `flyctl volumes snapshots list vol_rnzgjwgg509lg8er -a ts-terrabase --json` still lists its four snapshots with `status: created`. Provider-supported early deletion remains unresolved.

## Tigris inventory — deletion blocked

`flyctl storage list -o terrascale` returned:

```text
NAME              ORG
ts-terrabase-dev   terrascale
ts-terrabase       terrascale
```

This is the Fly-linked bucket inventory, not proof of every standalone bucket in the Tigris organization. **No Tigris bucket was deleted.**

Tigris CLI v3.4.1 was fetched from its official GitHub release into task evidence storage. `tigris whoami` failed twice:

```text
ID token verification failed. Please run "tigris login" to re-authenticate.
```

Stopped Tigris operations after the two identical failures. Re-authentication and a successful `tigris whoami` identifying TerraScale are required before enumerating and deleting all buckets; the personal organization must never be touched.

## Bootstrap and deployment contract

Organization `00000000000000000000000001` is named `TerraScale`. Startup always seeds storage without calling auth. Auth owns identity records and owner membership.

| Service | Project ULID | Database ULID | Database name | Placement |
|---|---|---|---|---|
| ts-auth | `00000000000000000000000002` | `00000000000000000000000003` | `ts-auth` | gru home; sjc, fra replicas |
| ts-dashboard | `00000000000000000000000005` | `00000000000000000000000004` | `ts-dashboard` | gru home; sjc, fra replicas |

Both fixed projects also receive version-1 catalog bindings to the global issuer `https://auth.terrascale.tech/terrascale`, organization org01 and authority organization org01, without auth startup dependency. Existing conflicting bindings are refused. Matching scoped project tokens can resolve their database; project/database/scope/signature validation remains enforced.

Existing startup seeding is reused and remains idempotent with fixed ids/timestamps, conflict protection and background retry. Explore routes resolve each database under its own project. Greenfield name/project changes have no migration or fallback. T3 owns the auth storage-name update; T4 owns dashboard recreation and its project/name update.

The PR removes the production deploy `false &&` guard while preserving push/main identity, the exact-tree Fast gate and gru → sjc → fra live/backup checks. UTC start/end timestamps are added to regional rollout output and GitHub job summaries for T6. The CI run URL will be recorded when deployment is authorized after T0.

Previews remain paused. The preview organization has 18 apps and 18 machines against the previously reported 20-machine limit, and `scripts/fly/preview.sh` does not map the staged auth master/audit secrets to the auth container. No working preview was proved; no preview resources were deleted (outside this authorization).

## Validation

Pending remote `~/.local/bin/ts-heavy make check` on mariogk-dev in isolated server and SDK worktrees. `bash -n scripts/fly/deploy.sh scripts/fly/preview.sh` and `git diff --check` passed. `nice -n 19 bash scripts/fly/deploy-test.sh` returned `deploy: all checks passed`; `nice -n 19 bash scripts/fly/volume-reset-test.sh` returned `volume-reset: all checks passed`. The full check has already passed whitespace verification and its solution build (0 warnings, 0 errors); remaining stages are pending.

## Resume after T0

Finish TerraScale Tigris cleanup and resolve retained snapshots; generate new secrets directly into Fly/GitHub secret stores without printing values. T3 has already staged a new `TS_AUTH_MASTER_KEY` on ts-auth (reported digest `736542cfbf684a4c`); preserve it and never rotate it again during this rebuild. T2 owns the audit signing key and deployment credentials. Create fresh volumes and exactly one shared-cpu-2x, 512 MB, 4 GB swap, always-running machine per service/region; configure auto-extend and verify scheduled daily snapshots. Merge only after T0 and the latest-commit required test gate. Deploy through CI and retain UTC regional logs/run URL. Verify actual region in each health response using `fly-prefer-region`, then measure both seeded databases replicating across gru/sjc/fra. Placement metadata and isolated seed tests do not prove live replication.

No merge, new image, secret rotation, fresh machine/volume creation or live health/replication proof occurred in this phase.
