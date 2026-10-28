# Auth readiness prerequisite, 2026-10-28

This is a read-only availability observation, not a performance result or cause analysis.
A NetBird SSH probe on the admitted gru service host read the local public health
and readiness endpoints without a credential and inspected the running image identity.

| UTC | Region | Health | Readiness | Public readiness body |
| --- | --- | ---: | ---: | --- |
| 05:09:55.821461 | gru | 200 | 503 | status: not_ready |

Image: `ghcr.io/terrascale/ts-auth@sha256:9632c983ca6c3236ffd9a6abe9225506aaaaa54e03372f8c9dff8a721d240372`.
No cause was returned. This does not establish why the earlier synthetic-account
receipt returned 503. No receipt retry, deletion continuation or new tenant was
started during this probe. Prior private recovery material remains retained.
Snapshot: `/tmp/perf-api-auth-region-state-20261028-b.json` on delta.

No matching auth container was observed in the terrascale containerd namespace on
the sjc/fra host aliases; their public auth readiness was not measured. That local
inventory alone does not prove those regions' public auth routing or availability.
