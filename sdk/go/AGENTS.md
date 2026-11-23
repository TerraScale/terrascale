# ts-go

## Layout and checks

Run area commands from `sdk/go` in the monorepo.

- Go module: `terrascale.tech/sdk`. Root files implement clients; `v1/` holds database contracts, `tsauth/` identity support, `cmd/` command-line tools and `contracts/` fixtures.
- Use Go and the go-modern-guidelines plugin.
- Build and vet: `go build ./... && go vet ./...` (`make sdk-go-check` from the repository root).

## API rules

- Use one global API and auth origin, with no regional discovery or failover: `https://api.terrascale.tech` and `https://auth.terrascale.tech` are the defaults, and `Config.TSAuthBaseURL` names the ts-auth of another deployment. `ListRegions` reads the placement catalog, not an endpoint discovery service.

## Configuration

- Keep features always on; no feature flags, enable switches, opt-ins or kill switches. Keep one correct value in code, with no new tuning knobs.
- No fakes, demo stores or skip-auth. The journey in `e2e/` is the only test; an SDK operation gets a step in its runners (`e2e/AGENTS.md`).
- Keep the switch-style configuration check; unavoidable third-party names need a narrow allowance and a one-line reason.
- README.md records implementation and qualification gaps. Do not claim unshipped behavior or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards.
