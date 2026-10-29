# ts-go

Follow [workspace Shipping code](/home/mariogk/Projects/TerraScale/AGENTS.md#shipping-code-user-2026-10-29-go-fast-break-things-fast-q-trunk-q-local-check).
## Layout and checks

- Go module: `terrascale.tech/sdk`. Root files implement clients; `v1/` holds database contracts, `tsauth/` identity support, `cmd/` command-line tools and `contracts/` fixtures.
- Use Go and the go-modern-guidelines plugin.
- Fast: `go test ./...`.
- Full tests: `go test -race -count=1 ./...`; build: `go build ./...`.

## API rules

- Use one fixed global API and auth origin, with no regional discovery or failover. `ListRegions` reads the placement catalog, not an endpoint discovery service.

## Configuration

- Keep features always on; no feature flags, enable switches, opt-ins or kill switches. Keep one correct value in code, with no new tuning knobs.
- Fakes, demo stores and skip-auth belong only in test code and test binaries.
- Keep the switch-style configuration check; unavoidable third-party names need a narrow allowance and a one-line reason.
- README.md records implementation and qualification gaps. Do not claim unshipped behavior or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards.
