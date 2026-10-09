# .NET bench-client (TS-761, Slice 2)

**Incomplete:** this is the SDK-linked, fail-closed CLI preflight. It is not a
load generator and cannot produce capacity evidence yet. A valid workload exits
1 with `SDK_BLOCKED` before reading credentials, contacting a service, creating
a database or opening results. Invalid arguments/workloads exit 2. `--help`
exits 0. Existing results remain intact.

## Build and check

Requires Linux, Bash, GNU coreutils (`timeout`), the .NET 10.0.401 SDK (or newer
10.0.4xx patch) and read access to the
official private SDK Git repository. No SDK source is vendored. The production
project references a separate checkout of the official `ts-dotnet` main build,
pinned by [sdk.rev](sdk.rev). The CLI reports the referenced SDK assembly
version and source revision. The checkout and build outputs are ignored.

```sh
cd clients/dotnet
bash prepare-sdk.sh
nice -n 19 make check
nice -n 19 make build
nice -n 19 ./bench-client run \
  --workload ../../workloads/point-reads.json --region gru --out /tmp/dotnet-results.jsonl
```

`workloads/` is provided by Slice 1's PR #13 and is not on main yet. Until it
merges, use a workload file from that branch. Do not substitute a legacy driver.
The prepared checkout must remain clean; preparation refuses local changes and
requires the pinned revision to be an ancestor of official `origin/main`.
Every build verifies the SDK origin, clean worktree and exact pinned HEAD before
compilation, so diagnostics cannot silently report a different SDK source.
`nice` is applied by the local caller, never embedded in the Makefile or CI.
This slice does not modify the repository-wide workflow, which does not yet
run .NET client checks; the checks above are local validation only.
`bench-client` runs the built DLL with `dotnet` from PATH and preserves the
caller's working directory, including relative workload/output paths.
The launcher bounds the entire preflight to five seconds, including hostname
lookup, file opening and reads; termination escalates after one additional
second. Deadline expiry exits 2 with a fixed diagnostic. Use the launcher rather
than invoking the DLL directly to enforce this process deadline.

## Shared contract

Validation follows Slice 1's draft
[docs/protocol.md](https://git.terrascale.tech/TerraScale/ts-benchmark/src/commit/4d001d6c33db45722ab5e66a3ebee61e6d95d949/docs/protocol.md):
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

## Upstream prerequisites

Audited official `ts-dotnet` main at
[`a4ff285c051e63e72cb01c4a94173c91da1dd69a`](https://git.terrascale.tech/TerraScale/ts-dotnet/src/commit/a4ff285c051e63e72cb01c4a94173c91da1dd69a).
The SDK's own [migration status](https://git.terrascale.tech/TerraScale/ts-dotnet/src/commit/a4ff285c051e63e72cb01c4a94173c91da1dd69a/README.md)
says TS-589 project JWT routes are still pending. TerraBase's producer
[public route table](https://git.terrascale.tech/TerraScale/terrabase/src/commit/f5a862b15cb08f5202a46b4183b2ef647a5feb72/docs/api.md)
already defines these routes.

| Contract requirement | Official SDK main | Required upstream change |
| --- | --- | --- |
| JWT client-credentials exchange | `TerraScaleAuthClient.Oidc.RequestClientCredentialsTokenAsync` exists | Provision dedicated project-scoped benchmark credentials in OCI Vault; verify actual grants |
| Regional HTTPS endpoint | `TerraBaseStaticEndpointResolver` exists | None for explicitly selected gru/sfo |
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
No real benchmark was run and no performance number is claimed. Reconcile with
Slice 1's final merged protocol and runner before marking this slice complete.

[GRU-selected preflight evidence](bench/gru-preflight.md) is validation evidence,
not a deployed benchmark. This PR lands only the preflight after green CI,
current ts-reviewer approval and a conflict-free state. The full TS-761 slice
remains incomplete until the load client and live receipt are implemented.
