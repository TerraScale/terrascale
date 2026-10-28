# Go SDK and raw HTTP parity qualification, 2026-10-28

Synthetic qualification only: no live cell or speedup. Earlier live source `62adf93c4` reportedly emitted an empty, rejected H2 cell; its raw evidence is outside this branch and remains rejected. These controls do not establish SDK overhead or gru/sjc/fra latency.

| Defect in `62adf93c4` | Qualified behavior |
| --- | --- |
| Public `/v1/regions` discovery | Strict `TS_PEERS_FILE` node/region/IPv4 rows, sorted/deduplicated; malformed rows and missing gru/sjc/fra rejected |
| End-window cancellation censors tails | Separate parity loop retains every started-in-window latency through a bounded drain; throughput includes drain; `apibench.RunStep` unchanged |
| SDK retries hidden behind 200 headers | Attempts must equal logical starts, warmup/window/drain included and readiness excluded; body-read failures counted |
| Transport errors bypass SDK refresh | Mismatches counted and response passed through; shipped retries/401 refresh retained |
| Unbounded setup and uncertain ownership | `owned` row before mutation; shared 60 s setup deadline; exact-name delete on independent 30 s context after cancellation too |
| Ambiguous cleanup claim | `logical_accepted`, `absent` or `failed`; physical/remote erasure unverified; uncertain-create absence and delete failure require reconciliation |

Original stack base `a78bd87a1`: controls `1f6dfb69a`, transport `150af4155`, cell `9c18b1526`, runner `dea7d77df48e278166edd8fdfdff01d784c0c583`. Serial publication rebased identical Go/module bytes onto actual predecessor merges #798/#799/#800; this report was condensed afterwards.
SDK: public ts-go `07ffb51e1d66fb2f7a42673683e731bdfc26d158`, `go.sum` `h1:89Ytf7BpR2v42aQ/I4iw8bTRtvaJ781Ag3UrM6f7OSw=`. It has no retry setting, so existing `run` keeps shipped retries. No workflow, AGENTS, StorageFormat or production code changes.

All checks ran on beta, Go 1.27.3, through `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 … --repo terrascale --branch <branch>`. HTTPS fixtures use a per-test CA for api.terrascale.tech, normal certificate checks, no live calls or credentials, and parallel execution.

| Checked source | Commands | Outcome |
| --- | --- | --- |
| Cell `ec3c18d74`, pre-split | `go test -race -count=1 -v -run Parity ./clients/go/`; `go test -race ./clients/go/ ./internal/apibench/` | pass |
| Runner `51f5235a2` | `go vet ./...`; parity controls; `go test -race -count=1 ./...` | vet/controls pass; full FAIL in clients/go after 2.721 s, other packages pass |
| Same runner | `go test -race ./clients/go/` nine times | 9/9 pass |
| Runner `d427a74d3` | Test drains 1→10 s; vet; full race suite three times | vet and all 12 packages pass in 3/3 runs |
| Each original lower layer | `go vet ./clients/go`; `go test -race ./clients/go/ ./internal/apibench/` | pass |

The full failure's diagnosis was lost to truncated output. A short test drain under module load is suspected, unconfirmed. The longer test-only drain preserves assertions, parallelism and cancellation checks, and waits only as needed.

| Controls | Evidence |
| --- | --- |
| `TestParityRegionsFromPeersFile` | Sort/dedupe; extra region retained; bad/missing/IPv6/extra-field/empty/unset/absent inputs rejected |
| `TestParityMeasureKeepsWhatRunStepCensors`, `...KeepsTailsPastTheWindow` | 1.2 s read dropped by RunStep retained by parity; 700 ms tail extends duration; warmup excluded |
| `...BoundsTheDrain`, `...StopsOnCancellation` | 16 stuck reads reported unfinished and cancelled; missing latency/RPS null; no op outlives the call |
| `TestParityCellAcceptsExactReadsOnEveryLane` | raw-h1/raw-h2/sdk × c1/c16 accepted with exact documents, starts=attempts and exact protocols |
| `...RejectsHiddenRetriesAndMismatches` | Hidden 200-body retry, SDK 503 retry, wrong region and version rejected |
| `TestParityTransportRejectsWrongProtocol`, `...KeepsSDKTokenRefresh` | Wrong protocol/auth host refused or counted; SDK 401 refresh still works |
| Three `TestParityRunner*` cases | 36 cells per region; owned identity precedes mutation; bounded uncertain create and cancelled run still delete exact name; reconciliation marked |

RoundTripper counts cannot expose net/http internal wire replays; got_conn is diagnostic. Live setup/deletion, three quiet same-host before/after repeats, authenticated regional parity and normal rollout proof remain incomplete. Production mutation waits for reconciliation of the retained synthetic tenant.
