# TerraScale

TerraScale is the public monorepo for **TerraBase** (a leaderless, multi-region document database), hosted authentication, the dashboard, the .NET, JavaScript, Go, Python and Rust SDKs, documentation, shared .NET libraries and benchmarks.

Agent and contributor rules: [`AGENTS.md`](AGENTS.md). Shipping from a dev host: [`ts-ship`](docs/dev/shipping.md).

## Layout

| Path | What |
|---|---|
| `src/` | Shared libraries, authentication, TerraBase and `TerraScale.Dashboard` |
| `tests/` | Integration suites (`*.Fast`, `*.Slow`), TestHarness projects, and the libs test projects |
| `benchmark/` | Go benchmark code and `benchmark/bench/` results; `benchmark/dotnet/` for .NET library benchmarks |
| `sdk/` | Published SDKs in `dotnet/`, `js/`, `go/`, `python/` and `rust/` |
| `docs-site/` | Public documentation site deployed to Cloudflare |
| `docs/` | Engineering documentation for TerraBase, authentication and dashboard |
| `scripts/` | Build, test and release commands for each area |
| `.github/workflows/` | Scoped CI, dev-host test proof verification, package releases and deployments |

## Production

Production is moving to OCI `gru` (`sa-saopaulo-1`, host `ts-gru-1`) under
[`ts-orch`](https://github.com/TerraScale/ts-orchestrator). The regional footprint also includes
`sjc` (`us-sanjose-1`) and `fra` (`eu-frankfurt-1`); a region token alone does not prove a service is ready.
Fast and release qualification run on dev hosts; GitHub Actions verifies their source-bound proof and publishes images to GHCR.
The orchestrator owns artifact admission and rollout authority, private NetBird networking, and
OCI Vault credential delivery using instance principals. See its
[region contract](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/automatic-regions.md) and
[secret delivery contract](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/secrets.md)
for implementation limits and operational ownership. Tigris backup storage retains its Fly organization
connection and `fly.storage.tigris.dev` endpoint; compute ownership does not change that integration.

## Make targets

Run them from the repository root. Locally, prefix each with `nice -n 19`.

| Command | What it does |
|---|---|
| `make build` | Build the solution |
| `make test-fast` | Run the blocking integration suites |
| `make test-slow` | Run the non-blocking integration suites |
| `make check` | Formatting, build and the Fast suite; CI is the gate, so running it locally is optional |
| `make dev` | Run TerraBase and ts-auth locally under Aspire (see below) |
| `make libs-*`, `make auth-*`, `make terrabase-*`, `make benchmark-*` | Platform area targets |
| `make sdk-<lang>-test-fast` | Fast for one SDK (`dotnet`, `js`, `go`, `python`, `rust`) |
| `make docs-test-fast`, `make dashboard-test-fast` | Documentation or dashboard Fast |

## Local development with Aspire

`make dev` runs TerraBase and ts-auth on this machine through the [Aspire](https://aspire.dev) TypeScript AppHost in
`aspire/apphost.mts`, with the Aspire dashboard for their logs, traces and health. It is local development only; nothing
here deploys or touches production.

- Needs Node.js 20.19+ and the .NET SDK pinned in `global.json`. Services use the SDK source in `sdk/dotnet`.
  The Aspire CLI is a local dotnet tool (`dotnet-tools.json`), run as `dotnet aspire` after `dotnet tool restore`
  (`make dev` runs it). Trust the development certificate once with `dotnet aspire certs trust`.
- Every listener stays on loopback: TerraBase binds `127.0.0.1` in Development (8080 public, 8081 internal,
  9091 metrics) and ts-auth listens on `127.0.0.1:8090` (metrics 9092). Locally, ts-auth stores in the TerraBase at
  `127.0.0.1:8081`.
- Each run is disposable: TerraBase starts from an empty data directory (`$TMPDIR/terrascale-aspire-terrabase`, emptied
  at the start of every run), and ts-auth gets a new master key that is never written to disk.
- ts-auth answers `/readyz` 503 ("no signing keys") until the platform issuer has a signing key. The `auth-signing-key`
  resource runs `ts-auth provision-signing-key` against that run's local TerraBase only; it never starts by itself
  (start it from the dashboard). See [docs/auth/signing-keys.md](docs/auth/signing-keys.md).
- Agents: `.mcp.json` registers the Aspire MCP server (`dotnet aspire agent mcp`, stdio) for Claude Code; it asks for
  approval the first time.

## Docs

- TerraBase: [`docs/terrabase/README.md`](docs/terrabase/README.md)
- ts-auth: [`docs/auth/README.md`](docs/auth/README.md)
- Benchmarks: [`benchmark/README.md`](benchmark/README.md)

- Dashboard: [`docs/dashboard/README.md`](docs/dashboard/README.md)
- Public documentation: [`docs-site/README.md`](docs-site/README.md)
- SDKs: [`sdk/dotnet`](sdk/dotnet), [`sdk/js`](sdk/js), [`sdk/go`](sdk/go), [`sdk/python`](sdk/python), [`sdk/rust`](sdk/rust)
