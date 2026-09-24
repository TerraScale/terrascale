# Aborted RustFS end to end attempt

Status: **aborted before qualification** — 2026-09-23

The run was started against the current TerraBase source, then stopped by the
coordinator to serialize RustFS measurements with the 100k tier workload. It
must not be used as qualification evidence.

Command configuration:

```text
--workload=e2e-volume --object-tier=s3 --documents=100000 --batch-size=5000
--point-reads=50000 --updates=1000 --deletes=1000 --queries=1000
--relationships=10000 --seed=1337
```

Observed phases before termination:

- `bulk-write` began at `02:15:39` local benchmark output time.
- `relationship-write` began at `02:15:50`.
- The process tree was terminated with `SIGTERM`; wrapper exit code was `143`.

No complete E2E receipt was emitted. The partial `console.log`, bucket name,
and source initialization files are retained for audit. A later clean run must
use a fresh output directory and S3 bucket.
