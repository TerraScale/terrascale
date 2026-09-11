# E-16 tenant-scale qualification

`scripts/tenant-scale.sh` is the named E-16 qualification command for the
100,000-database member shape. It has a cheap validator lane and an explicitly
opt-in live lane:

```sh
# Portable evidence check (does not start a member or create data).
./scripts/tenant-scale.sh validate e16-tenant-scale.json

# Expensive, destructive qualification on a disposable member and data root.
TERRABASE_E16_ALLOW_EXPENSIVE=1 ./scripts/tenant-scale.sh qualify \
  --api-url http://127.0.0.1:7101 \
  --metrics-url http://127.0.0.1:9461/internal/metrics \
  --api-key-file .terrascale/stack/bootstrap.key \
  --member-pid "$(awk '$1 == \"member-gru\" { print $2 }' .terrascale/stack/stack.pids)" \
  --out e16-tenant-scale.json
```

The PID must be the member process itself, and its cgroup v2 limits must be
exactly 2 OCPU (`cpu.max=200000 100000`) and 12 GiB
(`memory.max=12884901888`). An unbounded or differently capped member is a
hard failure. Start the Rust member with `nice -n 19` (as required by the
repository rules); the qualification command does not silently apply a weaker
envelope.

The live lane first checks the real member `/api/v1/health`, the member's
health-shaped `/api/v1/regions` probe, and private `/internal/metrics`
endpoints. This `/api/v1/regions` check is against a regional TerraBase
origin, not the edge-owned public region catalog served by `ts-proxy` at the
same path (see ADR 0043). The API and metrics URLs must identify the same
private member host. It then creates
exactly 100,000 uniquely named databases using `POST /api/v1/databases` with
SQLite-backed member state. Every create must return 201 with the requested
record, and a subsequent member `GET /api/v1/databases` must contain all
100,000 names. Duplicate, failed, skipped, placeholder, in-memory, stub, or
synthetic work cannot produce a passing receipt. The victim and adversarial
neighbour are exercised through concrete member paths under
`/api/v1/databases/<name>/collections`.

After creation, the command measures at least 1,000 victim requests alone and
at least 1,000 while exactly the same victim is loaded by at least 10,000
concurrent adversarial-neighbour requests. The loaded victim p99 must be no
more than 2× the solo p99, and create p99 must be below 5 seconds. It then
leaves the member idle for at least 30 seconds and measures `/proc/<pid>/stat`;
idle process CPU must be at most 1% of the idle window, the operational
definition of approximately zero idle cost here.

The receipt records both Prometheus scrapes and requires observed samples in
each scrape for `terrabase_ready`, `terrabase_requests_total`,
`terrabase_request_duration_seconds`, and `terrabase_active_members`. Missing
series, a public/non-member metrics source, or non-empty error/skip counts
fail the validator. `crates/terrabase-server/tests/tenant_scale_evidence.rs`
invokes only `validate` with temporary JSON fixtures, so the 100,000-database
run is never part of portable unit or integration test execution.

## Parent integration

Keep this lane outside `make check` and invoke it as a separately named,
disposable qualification step after the local member is running:

```make
tenant-scale:
	TERRABASE_E16_ALLOW_EXPENSIVE=1 ./scripts/tenant-scale.sh qualify --api-url "$(E16_API_URL)" --metrics-url "$(E16_METRICS_URL)" --api-key-file "$(E16_API_KEY_FILE)" --member-pid "$(E16_MEMBER_PID)" --out "$(E16_OUT)"
```

The parent may wire that command into an operator-only workflow, but should
not add it to portable `check`; it intentionally creates 100,000 databases
and leaves a qualification data set that must be discarded with the member's
disposable data root.
