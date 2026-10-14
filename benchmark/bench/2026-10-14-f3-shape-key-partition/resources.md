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
