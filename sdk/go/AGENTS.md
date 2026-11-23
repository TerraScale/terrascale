# ts-go

Read the root [AGENTS.md](../../AGENTS.md). Load the `use-modern-go` skill before editing Go code.

## Layout and checks

Run area commands from `sdk/go/` in the monorepo.

- Go module: `terrascale.tech/sdk`. Root files implement clients; `v1/` holds database contracts, `tsauth/` identity support, `cmd/` command-line tools and `contracts/` fixtures.
- Build and vet: `go build ./... && go vet ./...` (`make sdk-go-check` from the repository root).

## API rules

- Use one global API and auth origin, with no regional discovery or failover: `https://api.terrascale.tech` and `https://auth.terrascale.tech` are the defaults, and `Config.TSAuthBaseURL` names the `ts-auth` of another deployment. `ListRegions` reads the placement catalog, not an endpoint discovery service.
- `README.md` records implementation and qualification gaps; do not claim unshipped behavior.
