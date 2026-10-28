# API benchmark auth readiness blocker, 2026-10-28

Read-only operator evidence; no latency comparison, speedup or recovered tenant.
At 05:33:36 UTC, gru's running auth task answered `/healthz` 200 and `/readyz`
503 (`not_ready`). Image:
`ghcr.io/terrascale/ts-auth@sha256:9632c983ca6c3236ffd9a6abe9225506aaaaa54e03372f8c9dff8a721d240372`.

A second NetBird SSH observation at 05:38:37 UTC read the bounded stdout/stderr
files of that exact running container under ts-orch's `runtime-logs` directory.
Only readiness component, timestamp and allowlisted cause names were exported;
request data, credentials and exception messages were excluded.

| Readiness log window (UTC) | Cause | Observations |
| --- | --- | ---: |
| 05:36:59–05:38:34 | `TerraScale.Authentication.Contracts.ApiException` | 5 |
| 05:37:04–05:37:59 | `TerraScale.DotNet.TerraBase.TerraBaseUnavailableException` | 4 |

None of these nine records reported recovery. They establish storage-boundary
failures during readiness, not the underlying storage error or a transport cause.
The retention-cut hotfix #794 was still in the merge queue at observation time;
this probe does not prove that it will repair readiness. No deployment was forced.

The retained synthetic tenant recovery state was untouched. No token exchange,
receipt retry, tenant creation or deletion was attempted. Fresh live comparisons
remain unqualified until readiness changes and retained state is reconciled.
Sanitized observations are retained on delta in
`/tmp/perf-api-auth-region-state-20261028-c.json` and
`/tmp/perf-api-auth-region-state-20261028-readiness-file-logs.json`.
