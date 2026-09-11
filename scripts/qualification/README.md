# Real-service SDK qualification

`run.sh` consumes a coordinator-supplied immutable `.nupkg` after checking
`TERRASCALE_SDK_NUPKG_SHA256`, restores this standalone consumer from that
artifact, and runs one required phase. It has no
`ProjectReference`, mock handler, or skipped mode. The caller must provide:

`TERRASCALE_API_URL`, `TERRASCALE_AUTH_URL`, `TERRASCALE_PROJECT_ID`,
`TERRASCALE_DATABASE_ID`, `TERRASCALE_API_KEY`, and
`TERRASCALE_JOURNEY_RUN_ID`.

Set `TERRASCALE_SDK_NUPKG` and its lowercase hexadecimal
`TERRASCALE_SDK_NUPKG_SHA256` before every invocation. A coordinator may
prepare the artifact separately with `dotnet pack`; phase execution never
rebuilds or repacks source.

Run `./run.sh --phase seed|verify|exercise|cleanup`. Output is bounded JSON
observations and never includes credentials or response bodies. `verify`
expects `${runId}-{typescript,go,rust,dotnet}` documents. `cleanup` deletes only
all three dotnet-owned document IDs, attempts every deletion, and aggregates failures.
Run IDs must match `[A-Za-z0-9][A-Za-z0-9_-]{0,63}`. Missing inputs and any live failure are
non-zero failures; local execution does not qualify a provider run until the
coordinator retains its logs and package hash.

`tests/Regression.csproj` exercises the journey's assertions and bounded waits
with local inputs. These runner regressions are separate from production
qualification. Run them against the same supplied immutable package with
`./run.sh --regression`; this mode requires only `TERRASCALE_SDK_NUPKG` and
`TERRASCALE_SDK_NUPKG_SHA256`, and still verifies the package ID, version, and
copied-artifact hash before restoring `Regression.csproj`.

If the provider contract supplies a deliberately out-of-scope project API key,
set `TERRASCALE_DENY_API_KEY` (and optionally
`TERRASCALE_DENY_DATABASE_ID`, which defaults to `TERRASCALE_DATABASE_ID`) for
the live `exercise` phase. The journey then requires the deny key to receive
HTTP 401 or 403 from the selected database; the check is skipped when the
optional input is absent. The deny key must differ from the authorized key.
All mutation calls use deterministic, phase-scoped idempotency keys so a
replayed request can be distinguished from a new write.
