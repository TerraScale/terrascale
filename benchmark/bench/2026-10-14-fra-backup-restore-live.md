# Fly fra / Tigris restore drill — 2026-10-14

COS authorized this early drill on production `ad2b9ee841379f7308a416bcb3f0838c60353714`, before the format reset. No final-image proof is claimed: the later format-reset image remains outside this drill. No remote-ACK durability qualification is claimed.

| Result | Evidence |
|---|---|
| Known data | 12 documents written through fra; initial SHA-256 `5e95b5c8d26e5390ee8c43eed37ee2446d28b02ef6cd72d0942fef14f8958880` matched gru/sjc |
| Tigris | `ts-terrabase-backups-20261013-v2`; timestamped snapshot and CDC object listings |
| Loss simulated | fra stopped; `/data/data` wiped and its absence checked; original machine `286557e5b45498` subsequently deleted |
| Tenant recovery | Three authenticated/decompressed Tigris snapshots installed on wiped volume `vol_r7ymzxl21qqd52yr`; SQLite integrity checks passed; physical copy incarnations renewed using the existing engine SQL |
| Catalog | Old machine identity correctly refused the fresh replacement catalog because its existing anchor proved an older copy; fresh replacement `78111541ae9d38` pulled a fresh trusted catalog from gru/sjc and opened automatically |
| State equality | All 4096 buckets matched across all three regions for each restored database; the catalog digest also matched |
| Replication | All six directed regional write paths passed after recovery; 15 final documents had identical logical digest `e2fda34c44c39e5e8125cb7ecd90c4cc882d253fd6b358df97c8d0bc7da84d28` |
| Fly snapshot | Daily automatic snapshots enabled in all three regions, retention 5 days; on-demand fra snapshot `vs_6mJLpO2PP98cXpownzyM` restored into a cloned volume, mounted on a DNS-excluded helper; integrity and original 12-document digest passed |
| Cleanup | Snapshot helper machine and cloned volume deleted; rollback snapshot retained; only the three production machines remain started |

| Timing | Seconds | Measurement boundary |
|---|---:|---|
| Download / authenticate / decompress / integrity checks | 5.014 | Four Tigris objects, including a catalog decoded for inspection but never installed; three tenant files installed |
| Regional recovery and final verification | 764.435 | 07:20:47–07:33:31 UTC; includes rollback snapshot, helper work, format-record restoration, correct old-anchor refusal, new machine creation, digest and replication checks |
| Slowest write convergence | 1.784 | HTTP write through one regional listener to successful read through another; sequential verification includes preceding reads |
| Tenant snapshot to Tigris object, largest observed bound | 8.473 | Snapshot timestamp in key to LastModified plus its 1-second resolution; includes upload, not a monotonic latency measurement |

One operational sample. This was authenticated snapshot recovery with operator installation of files, not the managed API's whole-directory restore. The volume was wiped and reused; it was not deleted. No old `_system` catalog or anchor was restored, removed or bypassed. The unchanged format record came from the same stopped-volume snapshot. No timeout, production code, CI setting or secret value changed.

The original image override retained the temporary helper command; explicit `/app/terrabase` corrected it. Startup then refused restored files without the format record. Both preparation errors are included in the recovery timing. Two SFTP directory collisions were corrected by removing empty staging directories. The old machine's stale-anchor refusal was expected security behavior and required replacing the identity.

## Commands and retained output

Published JSON records:

1. Known data: [initial write](2026-10-14-fra-backup-restore-live/known-data.json), [pre-drill regional equality](2026-10-14-fra-backup-restore-live/known-data-before.json).
2. Recovery: [Tigris restore](2026-10-14-fra-backup-restore-live/tigris-restore-proof.json), [database bucket digests](2026-10-14-fra-backup-restore-live/database-bucket-digests.json).
3. Verification: [six replication paths and catalog state](2026-10-14-fra-backup-restore-live/replication-proof.json), [timings](2026-10-14-fra-backup-restore-live/timings.json).
4. Persistence: [Tigris shipping](2026-10-14-fra-backup-restore-live/tigris-shipping-proof.json), [fra snapshot listing](2026-10-14-fra-backup-restore-live/fra-fly-snapshots.json), [Fly snapshot restore](2026-10-14-fra-backup-restore-live/fly-snapshot-proof.json).

```sh
flyctl storage status ts-terrabase-backups-20261013-v2 -a ts-terrabase
flyctl volumes list -a ts-terrabase --json
flyctl volumes snapshots create vol_r7ymzxl21qqd52yr -a ts-terrabase --json
flyctl volumes create cos_snapshot_drill -a ts-terrabase --region fra \
  --snapshot-id vs_6mJLpO2PP98cXpownzyM --size 1 --yes --json
uv run --with boto3 --with cryptography --with lz4 python tigris-restore.py
uv run --with lz4 python digests.py
python3 replication-proof.py
uv run --with boto3 python tigris-shipping-proof.py
```

The Python commands were executed from `/home/mariogk/.local/state/cos-backup-restore/live-20261014/`. Their scripts and operational logs remain private and are not included in this repository; the nine linked JSON records are published here. They read credentials obtained from the Fly application's process through Fly SSH, never user login. The temporary credentials were the application’s Tigris S3 access credentials. Temporary credential and plaintext database files were removed afterwards; post-drill credential rotation is not established by the retained evidence. The digest producer calls real `terrabase.v1.Replication/Digest` on each region through its own Fly proxy and hashes the decoded 4096 × 16-byte bucket vector. Redacted records are retained alongside this report.

```text
FRA_WIPE_CONFIRMED
FRESH_CATALOG_PREPARED_TENANT_RESTORE_RETAINED
3 tenant databases: 4096/4096 matching buckets across fra/gru/sjc
fra->gru PASS 1.007 seconds
fra->sjc PASS 1.784 seconds
gru->fra PASS 0.572 seconds
gru->sjc PASS 0.996 seconds
sjc->fra PASS 0.889 seconds
sjc->gru PASS 0.980 seconds
fra/gru/sjc: catalog OPEN/trusted, final document digest equal
Fly snapshot restore PASS 12 documents, integrity ok, original digest equal
```

## Timestamped shipping sample

| File | Object key | LastModified UTC | Bytes |
|---|---|---|---:|
| `v1/00000000000000000000000001/00000000000000000000000002/00000000000000000000000003` | `v1/00000000000000000000000001/00000000000000000000000002/00000000000000000000000003/wal/01M4WN6CK941KT5KX1QQJWT55E/0000000000000000-1791963124528` | 2026-10-14T07:32:04+00:00 | 1402 |
| `v1/00000000000000000000000001/00000000000000000000000002/00000000000000000000000003` | `v1/00000000000000000000000001/00000000000000000000000002/00000000000000000000000003/wal/01M4WN6CK941KT5KX1QQJWT55E/0000000000004212-1791963128140` | 2026-10-14T07:32:08+00:00 | 614 |
| `v1/00000000000000000000000001/00000000000000000000000002/00000000000000000000000003` | `v1/00000000000000000000000001/00000000000000000000000002/00000000000000000000000003/wal/01M4WN6CK941KT5KX1QQJWT55E/0000000000005257-1791963129138` | 2026-10-14T07:32:09+00:00 | 439 |
| `v1/00000000000000000000000001/00000000000000000000000005/00000000000000000000000004` | `v1/00000000000000000000000001/00000000000000000000000005/00000000000000000000000004/wal/01M4WN6CK8AGF2YYPN70PBK7Q3/0000000000000000-1791963121137` | 2026-10-14T07:32:01+00:00 | 195 |
| `v1/00000000000000000000000001/00000000000000000000000005/00000000000000000000000004` | `v1/00000000000000000000000001/00000000000000000000000005/00000000000000000000000004/wal/01M4WN6CK8AGF2YYPN70PBK7Q3/0000000000000155-1791963124526` | 2026-10-14T07:32:04+00:00 | 195 |
| `v1/00000000000000000000000001/00000000000000000000000005/00000000000000000000000004` | `v1/00000000000000000000000001/00000000000000000000000005/00000000000000000000000004/wal/01M4WN6CK8AGF2YYPN70PBK7Q3/0000000000000310-1791963129139` | 2026-10-14T07:32:09+00:00 | 198 |
| `v1/01M4VJ03JHFR1BKJBEM7XKQ9AK/01M4VJ040HC0Y720JKQPH7RV85/01M4VJ046F1TPBDPFJ143ST4B2` | `v1/01M4VJ03JHFR1BKJBEM7XKQ9AK/01M4VJ040HC0Y720JKQPH7RV85/01M4VJ046F1TPBDPFJ143ST4B2/wal/01M4WN6CKD67DMW2VA93HPEDWH/0000000000000000-1791963121135` | 2026-10-14T07:32:01+00:00 | 195 |
| `v1/01M4VJ03JHFR1BKJBEM7XKQ9AK/01M4VJ040HC0Y720JKQPH7RV85/01M4VJ046F1TPBDPFJ143ST4B2` | `v1/01M4VJ03JHFR1BKJBEM7XKQ9AK/01M4VJ040HC0Y720JKQPH7RV85/01M4VJ046F1TPBDPFJ143ST4B2/wal/01M4WN6CKD67DMW2VA93HPEDWH/0000000000000155-1791963124526` | 2026-10-14T07:32:04+00:00 | 195 |
| `v1/01M4VJ03JHFR1BKJBEM7XKQ9AK/01M4VJ040HC0Y720JKQPH7RV85/01M4VJ046F1TPBDPFJ143ST4B2` | `v1/01M4VJ03JHFR1BKJBEM7XKQ9AK/01M4VJ040HC0Y720JKQPH7RV85/01M4VJ046F1TPBDPFJ143ST4B2/wal/01M4WN6CKD67DMW2VA93HPEDWH/0000000000000310-1791963129139` | 2026-10-14T07:32:09+00:00 | 200 |
| `v1/_system` | `v1/_system/wal/01M4WN6CKD67DMW2VA93HPEDWF/0000000000000000-1791963121138` | 2026-10-14T07:32:01+00:00 | 9692 |
| `v1/_system` | `v1/_system/wal/01M4WN6CKD67DMW2VA93HPEDWF/0000000000059117-1791963123136` | 2026-10-14T07:32:03+00:00 | 7094 |
| `v1/_system` | `v1/_system/wal/01M4WN6CKD67DMW2VA93HPEDWF/0000000000104889-1791963124527` | 2026-10-14T07:32:04+00:00 | 8147 |

These distinct CDC objects prove recurring continuous shipping during the observed window. They do not prove a full 24-hour daily Tigris snapshot schedule; fresh snapshot generations here were triggered by restart. Fly daily snapshot configuration and one real on-demand snapshot restore were checked separately; this drill did not exercise the daily Fly schedule.
