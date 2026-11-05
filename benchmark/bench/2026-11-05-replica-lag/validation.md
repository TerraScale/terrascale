# Maintenance change validation

Candidate `dda746a07` separates checksum observation from notice consumption and limits recovery pulls to idle replicas. These checks validate function; the live six-cell lag target is still outstanding.

| Check | Environment | Result |
| --- | --- | --- |
| Native prep and full TerraBase build | delta, x64 | Passed, zero warnings/errors, 64.18 s build |
| Two-region simulation, 85 ms one-way delay, 500 writes | delta, x64 | Bootstrap, DML, restart/catch-up passed; ack→visible p50 107.0 ms, p99 298.4 ms; 56 local reads over 50 ms |
| Focused anti-entropy integration test | beta, x64 | 1 passed, 0 failed/skipped, 1.888 s |

The anti-entropy test checks equal revisions, observation of a stale revision without pulling, explicit catch-up, tamper detection and healing. The simulation is recorded in [sim-maintenance.md](sim-maintenance.md). Its host was busy, and these runs on different hosts and loads do not support a controlled performance comparison.

## WAN prefetch and native writer separation

Source candidate `6486f05d2` moves the bounded home prefetch outside the native writer semaphore. The critical integration test holds a real home page response, commits a local write, and applies a newer pushed revision before releasing that response. It then checks overlay preservation, revision ordering and the original read-your-writes request position.

| Check | Environment | Result |
| --- | --- | --- |
| Full TerraBase build | beta, x64, two reserved CPUs | Passed, zero warnings/errors, 254.92 s |
| Delayed home answer critical test, five separate runs | beta, same admitted workspace | 5/5 passed; 2.316, 2.451, 3.000, 2.651, 2.861 s |

The [raw build/test log](wan-critical.log.gz) records these functional checks. The live six-cell lag target remains outstanding.
