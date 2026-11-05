# region-sim 2026-11-05T11:13:52Z

TerraBase `/home/mariogk/remote-builds/terrascale/wrk-replica-lag-checkpoint-sol-40409159a1cb23e8e8be1e2adfab03931280b6af03827813ebe8348e311b4868/src/TerraBase/bin/Debug/net11.0/terrabase`, ts `/home/mariogk/remote-builds/terrascale/wrk-replica-lag-checkpoint-sol-40409159a1cb23e8e8be1e2adfab03931280b6af03827813ebe8348e311b4868/benchmark/bin/ts`; auth: region-sim ts-auth stand-in (test-only); run 1m47s.

| scenario | pass/fail | lag | notes |
| --- | --- | --- | --- |
| cli-login | pass | — | device flow auto-approved by stand-in; whoami region-sim@example.test |
| create-project-db-replica-sjc | pass | — | project 01M6NPHFQKZXNBKXEBENXJQCXM, db 01M6NPHFZF8E9QN210CKFD2ZKQ home gru replicas [sjc] |
| dml-in-A-visible-in-B | pass | 745.0 ms | DDL+insert/update/delete via ts exec on gru; lag = last write ack to visible on sjc |
| restart-B-catches-up | pass | 823.9 ms | 51 writes while sjc-1 down; lag = ready to caught up; restart 2.112s |
| replica-lag | pass | p50 154.3 ms / p99 353.2 ms | 500 writes, peer delay 85ms one way, 8 reads on sjc over 50 ms |
