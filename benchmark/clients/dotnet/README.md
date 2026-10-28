# .NET bench-client (TS-761, Slice 2)

**Incomplete:** this is the SDK-linked, fail-closed CLI preflight. It is not a
load generator and cannot produce capacity evidence yet. A valid workload exits
1 with `SDK_BLOCKED` before reading credentials, contacting a service, creating
a database or opening results. Invalid arguments/workloads exit 2. `--help`
exits 0. Existing results remain intact.

## Build and check

Requires Linux, Bash, Git, GNU coreutils (`timeout`), util-linux (`flock`), the .NET SDK pinned by
[global.json](global.json) (11.0.100-rc.1.26425.128) and read access to the
official private SDK Git repository. No SDK source is vendored. The production
project references a separate checkout of the official `ts-dotnet` main build,
pinned by [sdk.rev](sdk.rev). The CLI reports the referenced SDK assembly
version and source revision. The checkout and build outputs are ignored.

```sh
cd clients/dotnet
bash prepare-sdk.sh
nice -n 19 make check
nice -n 19 make build
```

The six shared `workloads/` files and Slice 1's runner are merged on main.
The prepared checkout must remain clean; preparation refuses local changes and
requires the pinned revision to be an ancestor of official `origin/main`.
`make build`, `make test` and `make check` prepare a missing checkout before
restore; a stamp inside `.sdk/.git` avoids fetching on every invocation.
Preparation holds an exclusive `.sdk.lock`; verification takes a shared lock.
Every Makefile `dotnet` command runs through `with-sdk.sh`, holding a shared
lock for the complete restore, build, formatting or test process. Preparation
cannot replace SDK sources during compilation. Direct MSBuild invocations are
rejected; use `make` or `bash clients/dotnet/with-sdk.sh dotnet ...`, which
preserves the caller's working directory. Do not set its internal
`TS_BENCH_SDK_LOCKED` marker yourself. If an
interrupted clone leaves an unusable `.sdk`, move it aside before preparing again.
Every build verifies the SDK origin, clean worktree and exact pinned HEAD before
compilation, so diagnostics cannot silently report a different SDK source.
Preparation fetches explicitly into `refs/remotes/origin/main` regardless of
the checkout's configured fetch mapping.
`nice` is applied by the local caller, never embedded in the Makefile or CI.
This slice does not modify the repository-wide workflow, which does not yet
run .NET client checks; the checks above are local validation only.
`bench-client` runs the built DLL with `dotnet` from PATH and preserves the
caller's working directory, including relative workload/output paths.
The launcher bounds the entire preflight to five seconds, including hostname
lookup, file opening and reads; termination escalates after one additional
second. Deadline expiry exits 2 with a fixed diagnostic. Use the launcher rather
than invoking the DLL directly to enforce this process deadline.
Exit 137 reports a killed process and preserves the exit status; it cannot
distinguish forced timeout termination from an external kill or resource exhaustion.

## Shared contract

Validation follows the merged [docs/protocol.md](../../docs/protocol.md):
positive integer percentage weights summing to 100, seven distinct supported
operations, bounded durations/concurrency/dataset/target and exact increasing
ramp steps. Unknown, duplicate, missing or incorrectly typed fields and trailing
JSON values are rejected, including nested fields. Input is bounded to 1 MiB
and 16 JSON levels. Diagnostics never include input values, file paths, tokens
or response bodies. The excluded `mariogk-desktop` host is refused before input
is read. A failed or empty hostname lookup exits 2 with `HOST_UNAVAILABLE` and
refuses the run.

The official SDK production dependency is present; its public ULID generator,
validator and regional HTTPS resolver are exercised by tests. No UUID conversion,
SDK internals, raw HTTP or credential-swapping transport is used. SDK preparation
uses Git only for dependency retrieval, not for database requests.

## Historical upstream prerequisites

The table below records the older SDK audit, not the current pin. The benchmark
now pins v0.0.18; see [the SDK pin notes](SDK-PIN.md) for its verified source and
remaining benchmark implementation gaps. The workload preflight still fails
closed because its lifecycle and measurement contract are not implemented.

Audited official `ts-dotnet` main at
[`887d9de694bb3d9106d7d8e70029f9f7f554ff85`](https://git.terrascale.tech/TerraScale/ts-dotnet/src/commit/887d9de694bb3d9106d7d8e70029f9f7f554ff85).
The SDK's own [migration status](https://git.terrascale.tech/TerraScale/ts-dotnet/src/commit/887d9de694bb3d9106d7d8e70029f9f7f554ff85/README.md)
says TS-589 project JWT routes are still pending. TerraBase's producer
[public route table](https://git.terrascale.tech/TerraScale-Old/terrabase-premonorepo/src/commit/f5a862b15cb08f5202a46b4183b2ef647a5feb72/docs/api.md)
already defines these routes.

| Contract requirement | Official SDK main | Required upstream change |
| --- | --- | --- |
| JWT client-credentials exchange | `TerraScaleAuthClient.Oidc.RequestClientCredentialsTokenAsync` exists | Automatically create/deliver dedicated project-scoped credentials under the operating contract; verify actual grants (integration pending) |
| Regional HTTPS endpoint | `TerraBaseStaticEndpointResolver` exists | Implement automatic discovery of every eligible serving region; retain tenant placement authorization |
| Plain ULIDs | `TerraScaleIdGenerator` and `TerraScaleIds` exist | Verify producer acceptance for every persisted entity; never convert to UUIDs |
| Create owned database | `TerraBaseDatabase.CreateAsync` sends hidden legacy `PUT`; management create is legacy organization-scoped | Expose public project-JWT `POST /v1/projects/{project}/databases` through SDK |
| Delete owned database | Only legacy organization/user/database-ID management delete exists | Expose public project-JWT `DELETE /v1/projects/{project}/databases/{database-name}` through SDK |
| Active secondary index | No index client/API exists | Expose public project index create/list/drop; confirm active before warmup |

Starting a database without a valid cleanup call would leave live data.
Using the legacy management client would violate the greenfield/public-JWT
contract. Calling the REST routes directly or hiding a secondary-index scan
under `query.indexed` would violate the SDK/measurement requirements. The
preflight rejects the run instead of making any of those substitutions.

After the prerequisites land: update the pinned main revision, implement the
database lifecycle and seed validation through those public SDK calls, implement
the bounded intended-start scheduler/HDR/JSONL/summary/ramp contract, run unit
and SDK contract tests, then attach a bounded gru live receipt. The current
preflight does **not** implement any of those measurement/lifecycle components.
No real benchmark was run and no performance number is claimed. The final
protocol also requires per-window step/concurrency/elapsed fields, complete
workload configuration and compatible dense HDR snapshots in summaries.

The runner retains a failed preflight receipt. Its current closed region
selector and storage bypass remain implementation removal gaps, not approved
production setup. Automatic eligible-region discovery/local region inference
and credential delivery remain unimplemented requirements. No switch activation
command is a replacement for the missing SDK/load lifecycle.

The current preflight returns `SDK_BLOCKED`, so the runner retains a FAILED
receipt with throughput and latency unavailable. It cannot produce a capacity
verdict until the SDK and load client prerequisites above are complete.

[GRU-selected preflight evidence](bench/gru-preflight.md) is validation evidence,
not a deployed benchmark. This PR lands only the preflight after green CI,
current ts-reviewer approval and a conflict-free state. The full TS-761 slice
remains incomplete until the load client and live receipt are implemented.
