# region-sim 2026-11-05T11:02:50Z

TerraBase `/home/mariogk/remote-builds/terrascale/wrk-replica-lag-trace-sol-5f3e34bc4db2e3de75d356ec0c87ae6d3e134f172810ea70c272e2e1c9fe4c34/src/TerraBase/bin/Debug/net11.0/terrabase`, ts `/home/mariogk/remote-builds/terrascale/wrk-replica-lag-trace-sol-5f3e34bc4db2e3de75d356ec0c87ae6d3e134f172810ea70c272e2e1c9fe4c34/benchmark/bin/ts`; auth: region-sim ts-auth stand-in (test-only); run 1m18s.

| scenario | pass/fail | lag | notes |
| --- | --- | --- | --- |
| cli-login | pass | — | device flow auto-approved by stand-in; whoami region-sim@example.test |
| create-project-db-replica-sjc | pass | — | project 01M6NNY3GMGVZNBX2EVDRHWTAQ, db 01M6NNY3M8EMRAHT5WCYJT7KCD home gru replicas [sjc] |
| dml-in-A-visible-in-B | pass | 816.1 ms | DDL+insert/update/delete via ts exec on gru; lag = last write ack to visible on sjc |
| restart-B-catches-up | pass | 483.6 ms | 51 writes while sjc-1 down; lag = ready to caught up; restart 922ms |
| replica-lag | pass | p50 106.4 ms / p99 257.5 ms | 500 writes, peer delay 85ms one way, 7 reads on sjc over 50 ms |
