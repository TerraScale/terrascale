# region-sim 2026-11-05T11:45:58Z

TerraBase `/home/mariogk/remote-builds/terrascale/wrk-replica-lag-maintenance-sol-40b0774443484a5988ff2db205ac4c1a0607e56ec3d0497e358d753d5cea905c/src/TerraBase/bin/Debug/net11.0/terrabase`, ts `/home/mariogk/remote-builds/terrascale/wrk-replica-lag-maintenance-sol-40b0774443484a5988ff2db205ac4c1a0607e56ec3d0497e358d753d5cea905c/benchmark/bin/ts`; auth: region-sim ts-auth stand-in (test-only); run 1m40s.

| scenario | pass/fail | lag | notes |
| --- | --- | --- | --- |
| cli-login | pass | — | device flow auto-approved by stand-in; whoami region-sim@example.test |
| create-project-db-replica-sjc | pass | — | project 01M6NRCF1935TBFT3MC5Y95SCC, db 01M6NRCF8T38H95AXB0EEPG70R home gru replicas [sjc] |
| dml-in-A-visible-in-B | pass | 664.3 ms | DDL+insert/update/delete via ts exec on gru; lag = last write ack to visible on sjc |
| restart-B-catches-up | pass | 631.7 ms | 51 writes while sjc-1 down; lag = ready to caught up; restart 1.799s |
| replica-lag | pass | p50 107.0 ms / p99 298.4 ms | 500 writes, peer delay 85ms one way, 56 reads on sjc over 50 ms |
