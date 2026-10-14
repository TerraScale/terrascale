2026-10-14T20:59:55Z started machine 891e70eb4d0378 (ts-vtest-tb, existing)
2026-10-14T20:59:59Z started machine 6835466b44e258 (ts-vtest-tb, existing)
2026-10-14T21:00:05Z started machine 48e4967a225338 (ts-vtest-tb, existing)
2026-10-14T21:00:49Z deploy start: vtest.sh deploy 30d6f8331840add5f7df2a2b7f76fd38c29b2a81 via ts-heavy-any (branch feat/repl-v5-harness scripts)
2026-10-14T21:09:27Z deployed 30d6f833 to ts-vtest-tb, image vtest-30d6f8331840 digest sha256:84e483a6…; build host beta leaves /tmp/vtest-src-30d6f833… worktree + /tmp/vtest-ctx-30d6f833…
2026-10-14T21:09:36Z proof start: f3-partition.py (creates own DB f3-*, keys f3-*-rev/-ctl; partition sjc; heal in finally; cleanup)
2026-10-14T21:14:14Z attempt1 aborted in setup (no shape handles); cleanup revoked keys 01M4Y3ZP7F2BREJXVGNX8186H9, 01M4Y3ZPA7AM2YXFY6ZQH3Y7CP (204) and deleted DB f3-20261014210936 01M4Y3ZP43AP9KCXWT4974B6PH (204); no partition applied
2026-10-14T21:14:14Z attempt2 start
2026-10-14T21:15:31Z attempt2 aborted in setup (429 shape_busy on bootstrap, needs #307); cleanup revoked 01M4Y486CE89D3VSAVXVKX1ZPX, 01M4Y486FCC6N5MS2B7MAHTJP1 (204), deleted DB f3-20261014211415 01M4Y4869A0M0JZNPDDZYTKQXG (204); no partition
2026-10-14T21:15:36Z attempt3 start
2026-10-14T21:20:09Z attempt3 aborted at baseline (sjc/fra had not yet replicated keys); cleanup revoked 01M4Y4ARYP8Y43YY9AW15KW0ED, 01M4Y4AS1M14ZFB9BW98BKP66H (204), deleted DB f3-20261014211539 01M4Y4ARVF8VV6VAG7WPKZB0RH (204); no partition
2026-10-14T21:20:14Z attempt4 start
2026-10-14T21:32:30Z attempt4 complete: partition sjc 21:20:45Z, heal 21:30:58Z (rc 0), keys 01M4Y4K56QE4C4SEMHC2HT32MA (revoked in-test) 01M4Y4K59FDMW8VR4E2MZHGYVZ (204), DB f3-20261014212014 01M4Y4K53FD9PTD3V2YC2QN8CY deleted (204)
2026-10-14T21:32:30Z row11 rerun start: revoke-timing.py 5 trials (creates/deletes v4-revoke-* DB and keys)
2026-10-14T21:33:03Z row11 rerun complete: DB v4-revoke-20261014213233 01M4Y59M8D3D79PW650KPRCFF0 deleted (204); 5 trial keys revoked in-trial; no leftovers reported
2026-10-14T21:33:05Z stopped machine 891e70eb4d0378
2026-10-14T21:33:08Z stopped machine 6835466b44e258
2026-10-14T21:33:11Z stopped machine 48e4967a225338
2026-10-14T21:33:24Z removed build leftovers on beta: /tmp/vtest-src-30d6f8331840add5f7df2a2b7f76fd38c29b2a81 (worktree), /tmp/vtest-ctx-30d6f8331840add5f7df2a2b7f76fd38c29b2a81(+.publish.log)
vtest.sh: 2026-10-14T21:20:45Z partitioned sjc from the other two (6PN drops both ends)
vtest.sh: 2026-10-14T21:30:58Z healed: 6PN rules flushed on all three

## Live real-key runs (second session)

- 2026-10-14T21:57:10Z started machine 891e70eb4d0378
- 2026-10-14T21:57:14Z started machine 6835466b44e258
- 2026-10-14T21:57:18Z pushed proof-only branch fix/f3-live-proof-candidate at ce3fff5f16a1584c4253a87d088f3fac9fb61100 (main+#307+#309+#314+#283+#316); deploy start
- 2026-10-14T21:57:18Z started machine 48e4967a225338
- 2026-10-14T22:05:26Z deployed ce3fff5f (proof-only candidate) image vtest-ce3fff5f16a1 digest sha256:2f77584c…352ab; build host beta leaves /tmp/vtest-src-ce3fff5f… + ctx
- 2026-10-14T22:05:27Z stopped machine 891e70eb4d0378 (review REQUEST CHANGES; live rerun after fixes)
- 2026-10-14T22:05:30Z stopped machine 6835466b44e258 (review REQUEST CHANGES; live rerun after fixes)
- 2026-10-14T22:05:33Z stopped machine 48e4967a225338 (review REQUEST CHANGES; live rerun after fixes)
- 2026-10-14T22:16:49Z force-pushed proof-only branch fix/f3-live-proof-candidate to 20f6238b (main 7d22f0b3 + #307 + #309 + #314 + #283 + #316@398eb707)
- 2026-10-14T22:16:52Z started machine 891e70eb4d0378
- 2026-10-14T22:16:56Z started machine 6835466b44e258
- 2026-10-14T22:17:00Z started machine 48e4967a225338
- 2026-10-14T22:21:05Z deployed 20f6238bcd10d8fd36730571a6e10b2a6327676a to ts-vtest-tb: image registry.fly.io/ts-vtest-tb:vtest-20f6238bcd10 digest sha256:a3aedc6f67c522be921b0515a0b718434f015e46903d1ab54878f8b556024986
- 2026-10-14T22:21:41Z live run1 start: f3-live.py (DB f3l-*, keys f3l-*-rev/-ctl; partition sjc + DNS cut; heal in finally)
- 2026-10-14T22:42:40Z live run1 heal failed (vtest heal rc1: ssh to sjc unauthenticated with sjc DNS cut); flushed fra manually; restarting sjc to clear its in-memory ip6tables rules
- 2026-10-14T22:49:05Z force-pushed proof-only candidate 84d1e0991f4663f6eab4f77c16a271a66c120db2 (main 35b14217 + #307 + #309 + #314 + #283 + #316@41e715bc); deploy start
- 2026-10-14T22:53:10Z deployed 84d1e0991f4663f6eab4f77c16a271a66c120db2 to ts-vtest-tb: image registry.fly.io/ts-vtest-tb:vtest-84d1e0991f46 digest sha256:380977aea1c8f2ba6ccef1740f50a0fb3f94269c40acfa539b825785fea524ac
- 2026-10-14T22:53:25Z live run2 start (6PN-only partition)
- 2026-10-14T23:14:24Z live run1 (20f6238b): keys 01M4Y83QGAC185ZG1JPZNKD3JW (revoked in-test) 01M4Y83QK1514QB3JPN0GKQ998 (204); DB f3l-20261014222142 01M4Y83QCV0V06W89RSR3V770K deleted (204)
- 2026-10-14T23:14:24Z live run2 complete: partition sjc 22:54:18Z, heal 23:04:50Z rc0; keys 01M4Y9XV3B7CKGWR9VXNZFXNSS (revoked in-test, cleanup 404) 01M4Y9XV613R4AVXY60Z1CDSF6 (204); DB f3l-20261014225326 01M4Y9XV02D0WM7A9824EJ0H8B deleted (204)
- 2026-10-14T23:14:39Z stopped machine 891e70eb4d0378
- 2026-10-14T23:14:42Z stopped machine 6835466b44e258
- 2026-10-14T23:14:45Z stopped machine 48e4967a225338
- 2026-10-14T23:14:47Z removed /tmp/vtest-src|ctx-{ce3fff5f,20f6238b,84d1e099}* on build hosts
- vtest.sh: 2026-10-14T22:22:28Z partitioned sjc from the other two (6PN drops both ends)
- vtest.sh: 2026-10-14T22:54:18Z partitioned sjc from the other two (6PN drops both ends)
- vtest.sh: 2026-10-14T23:04:50Z healed: 6PN rules flushed on all three
