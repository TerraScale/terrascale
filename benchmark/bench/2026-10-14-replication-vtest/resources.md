# ts-vtest resources created by the V2/V5 lane

State checked 2026-10-14T17:45Z (`flyctl machine list`, `flyctl volumes list -a ts-vtest-tb`). The V-wave owner destroys
them after the final reruns: `scripts/vtest.sh destroy` (bucket, then app with its machines and volumes).

| Resource | Name / id | Region | State |
| --- | --- | --- | --- |
| Fly app | `ts-vtest-tb` (org `terrascale-previews-515`) | — | deployed, **left running** |
| Machine (shared-cpu-2x, 512 MB, 4 GB swap) | `891e70eb4d0378` | gru | started |
| Machine | `6835466b44e258` | sjc | started |
| Machine | `48e4967a225338` | fra | started |
| Volume 1 GB | `vol_4oj2nq8ylpjjwl2r` (on `891e70eb4d0378`) | gru | created |
| Volume 1 GB | `vol_4oj2nqmj3ez6oppr` (on `6835466b44e258`) | sjc | created |
| Volume 1 GB | `vol_4oj2nqmwdwwog72r` (on `48e4967a225338`) | fra | created |
| Tigris bucket (TerraBase exits 2 without one outside Development) | `ts-vtest-tb` | — | exists |
| Fly secrets on `ts-vtest-tb` | `TERRABASE_MASTER_KEY`, `TERRABASE_PEER_SECRET` (generated, 0600 temp file deleted), `AWS_*`/`BUCKET_NAME` (set by `flyctl storage create`) | — | set |
| Image | `registry.fly.io/ts-vtest-tb:vtest-a439e1d17ddc` `sha256:b2267cc04eb069432bcdaf204eaee536320d70b109ddeb61d4bbd51c22edc804` | — | deployed |
| Synthetic tenant (data inside the cluster) | owner `01M4XPQPMBAZXA6GW4R5C7W2HP`, org `01M4XPQPQ31V2MXW39W44J1FGW`, project `01M4XPQPT87J82RB1Q4ZCE0FCH`, database `e2e` | — | exists |
| Forged-peer probe orgs (data) | `forged-{none,wrong}-{gru,sjc,fra}` created by the forged operator user `01J0F0RGED0000000000000000` | — | deleted 2026-10-14T20:25Z |
| API keys | `01M4XPSTE9D4S5CZ6HDWKRNWZA`, `01M4XQ8G1W3XFEK44VDG6QMWK5`, `01M4XQ907E0WV33PPEMSQJ91SN`, `01M4XQD8YQ8GAPZJ2GRRDDHHSG` (24 h, documents:read/write) | — | each revoked on exit |

Nothing was created outside `ts-vtest-tb`: no builder app (the image is published on beta and layered on Fly's depot
builder). An earlier depot build of the full source OOM-killed crossgen2 (exit 137) and pushed nothing.

## Log (appended by `scripts/vtest.sh`)

2026-10-14T16:47:43Z created app ts-vtest-tb in terrascale-previews-515
2026-10-14T16:47:47Z created Tigris bucket ts-vtest-tb (credentials set as ts-vtest-tb secrets by flyctl)
2026-10-14T16:47:48Z secrets set on ts-vtest-tb: TERRABASE_MASTER_KEY, TERRABASE_PEER_SECRET (generated, temp file deleted)
2026-10-14T17:16:00Z deployed a439e1d17ddcc43df9e714eeb3c6afc6dc9187bd to ts-vtest-tb (published on beta): image registry.fly.io/ts-vtest-tb:vtest-a439e1d17ddc digest sha256:b2267cc04eb069432bcdaf204eaee536320d70b109ddeb61d4bbd51c22edc804
2026-10-14T17:16:32Z created machine 891e70eb4d0378 in gru + volume vol_4oj2nq8ylpjjwl2r
2026-10-14T17:17:04Z created machine 6835466b44e258 in sjc + volume vol_4oj2nqmj3ez6oppr
2026-10-14T17:17:39Z created machine 48e4967a225338 in fra + volume vol_4oj2nqmwdwwog72r
2026-10-14T17:18:06Z bootstrapped synthetic tenant org 01M4XPQPQ31V2MXW39W44J1FGW project 01M4XPQPT87J82RB1Q4ZCE0FCH database e2e
2026-10-14T17:32:37Z machine 891e70eb4d0378 (gru) SIGKILLed by the V5 R3 test; restarted by its on-failure policy, readyz 200 at 17:45Z
2026-10-14T19:45Z..20:02Z V2 S3 on image vtest-3c459a5fa160 (owner's deploy): sjc 6835466b44e258 updated to entrypoint `sleep infinity`, sqlite3 installed in its ephemeral rootfs, one `_tb_state` row deleted, then updated back to entrypoint `/app/terrabase` (now explicit in its config; the image's own entrypoint, same binary). No volume or snapshot created.
2026-10-14T19:46Z..19:47Z V2 S4: clocks stepped with `date -s` (sjc +2000 ms, fra -2000 ms) and stepped back by the test (residual about 1-2 ms per machine)
2026-10-14T20:06Z..20:24Z V4 row 11: databases v4-revoke-20261014200603/201628/202140/202227/202259 created and deleted (replicas removed first); keys v4-revoke-* minted and revoked (0 keys left in the vtest project)
2026-10-14T20:25Z deleted the six forged-peer probe organizations (forged-{none,wrong}-{gru,sjc,fra}), HTTP 204 each
