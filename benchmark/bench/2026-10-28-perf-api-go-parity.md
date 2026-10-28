# Go SDK and raw HTTP parity qualification, 2026-10-28

**Synthetic qualification only. No live cell was measured and no speedup is claimed.** These controls show the parity
runner rejects the defects found in the first live attempt. They do not establish SDK overhead, gru/sjc/fra latency or
parity. The earlier live attempt with `origin/fix/api-go-parity` `62adf93c4` reportedly produced an empty, rejected H2 cell.
Its raw evidence is not in this branch; that result stays rejected, and nothing here replaces it.

## Defects fixed

| Defect in `62adf93c4` | Fix |
| --- | --- |
| Regions came from public `/v1/regions` | `TS_PEERS_FILE` rows `<node> <region> <ipv4>`, sorted and deduplicated. A malformed row rejects the file; gru, sjc and fra are required |
| `apibench.RunStep` cancels at the window end and keeps only operations that finish inside it, so slow tails vanish and a slow cell can be empty | Parity-only loop: every read started in the 5 s window keeps its full latency. Throughput runs to the last measured completion. 2 s warmup, 30 s bounded drain, bounded HDR histogram. `RunStep` and other workloads are unchanged |
| SDK retries after a valid 200 header and a failed body read were invisible | A cell is rejected when RoundTripper attempts differ from logical starts (warmup and window; readiness excluded). Failed body reads are also counted |
| Mismatches returned a transport error, which bypassed the SDK's 401 refresh | Mismatches are counted and the response passes through unchanged, so the shipped refresh and retries still run |
| Database name known only after setup; setup unbounded | `owned` row before the create; create, collection and seeds share one 60 s deadline; exact-name delete on a fresh 30 s context, after cancellation too |
| Cleanup reported plain `accepted` | `deletion` is `logical_accepted`, `absent` or `failed`. `remote_erasure` is `unverified`. Uncertain create plus absent, or a failed delete, sets `reconcile_required` |

Evidence limit: RoundTripper counts cannot show net/http's internal wire replays. `got_conn` is diagnostic only.

## Source

- Stack on `origin/main` `a78bd87a1`, about 400 changed lines per layer, no workflow, AGENTS or StorageFormat edits:
  `fix/api-go-parity-controls` `1f6dfb69a` (SDK pin, peers, measurement),
  `fix/api-go-parity-transport` `150af4155` (attempt observer, HTTPS fixture),
  `fix/api-go-parity-cell` `9c18b1526` (cell and rejection), then `fix/api-go-parity-runner` (lifecycle, this report).
- SDK: public `github.com/TerraScale/ts-go` `07ffb51e1d66fb2f7a42673683e731bdfc26d158` via the `go.mod` replace,
  `go.sum` `h1:89Ytf7BpR2v42aQ/I4iw8bTRtvaJ781Ag3UrM6f7OSw=`. This SDK has no retry setting, so `run` keeps the shipped retries.
- Fixture: `httptest` HTTPS with h1/h2 for `api.terrascale.tech`, using a per-test CA leaf and normal verification
  (no `InsecureSkipVerify`). It uses no live API, credentials, tenant or SSH. Every test is `t.Parallel()`.

## Commands and outcomes

All ran on beta through `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 bash -c '<cmd>' --repo terrascale --branch <branch>`, Go 1.27.3.

| Branch head | Command | Outcome |
| --- | --- | --- |
| cell `ec3c18d74` (pre-split) | `go test -race -count=1 -v -run Parity ./clients/go/`; `go test -race ./clients/go/ ./internal/apibench/` | pass |
| runner `51f5235a2` | `go vet ./...`; `-v -run Parity`; then `go test -race -count=1 ./...` | vet ok, parity pass; **full run FAIL**: `clients/go` 2.721s, all other packages ok |
| runner `51f5235a2` | `go test -race ./clients/go/` once, then eight more times | 9/9 ok (no repro alone) |
| runner `d427a74d3` | test drains raised to 10 s; `go vet ./...`; `go test -race -count=1 ./...` ×3 | vet ok; 3/3 all 12 packages ok |
| `1f6dfb69a`, `150af4155`, `9c18b1526` | `go vet ./clients/go`; `go test -race ./clients/go/ ./internal/apibench/` | each layer ok |

The full-run failure was not captured; `tail` truncated it. Likely cause: under full-module load, the runner test's 1 s
drain expired and a cell was rejected early. This is unconfirmed, and the failure is kept here. The drain only waits as
long as it needs, so raising it does not weaken any check.

## Controls

| Control | Shows |
| --- | --- |
| `TestParityRegionsFromPeersFile` (7 cases, plus unset and absent) | sort/dedupe, extra region kept; missing required, IPv6, extra field, bad region, empty rejected |
| `TestParityMeasureKeepsWhatRunStepCensors` | the same 1.2 s read: `RunStep` max < 1 s, parity max ≥ 1.2 s |
| `TestParityMeasureKeepsTailsPastTheWindow` | 700 ms tail kept, duration extends past the window, warmup excluded |
| `TestParityMeasureBoundsTheDrain`, `...StopsOnCancellation` | 16 stuck reads reported unfinished, cut at the deadline; cancellation ends at once; nil latency/RPS; no op outlives the call |
| `TestParityCellAcceptsExactReadsOnEveryLane` | raw-h1/raw-h2/sdk × c1/c16 accepted with attempts = starts and exact protocol counts |
| `TestParityCellRejectsHiddenRetriesAndMismatches` | SDK body-read retry after a 200 (all results ok, zero bad headers) rejected as `attempts_differ_from_starts`; SDK 503 retry, wrong region, wrong version rejected |
| `TestParityTransportRejectsWrongProtocol`, `TestParityCellKeepsSDKTokenRefresh` | h2 on an h1 lane counted bad; non-document route refused; SDK 401 refresh still works |
| `TestParityRunner*` (3) | 3 regions × 36 cells then `logical_accepted`; zero requests before `owned`; a hung create is bounded and still deleted, `reconcile_required`; a cancelled run still deletes |

## Not done

No live run and no quiet same-host before/after repeats. The PR waits for the parent after #795.
