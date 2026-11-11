# ts-go

## Layout and checks

Run area commands from `sdk/go` in the monorepo.

- Go module: `terrascale.tech/sdk`. Root files implement clients; `v1/` holds database contracts, `tsauth/` identity support, `cmd/` command-line tools and `contracts/` fixtures.
- Use Go and the go-modern-guidelines plugin.
- Fast: `go test ./...`.
- Full tests (including isolated package installation): `go test -tags slow -race -count=1 ./...`; build: `go build ./...`.

## API rules

- Use one global API and auth origin, with no regional discovery or failover: `https://api.terrascale.tech` and `https://auth.terrascale.tech` are the defaults, and `Config.TSAuthBaseURL` names the ts-auth of another deployment. `ListRegions` reads the placement catalog, not an endpoint discovery service.

## Configuration

- Keep features always on; no feature flags, enable switches, opt-ins or kill switches. Keep one correct value in code, with no new tuning knobs.
- Fakes, demo stores and skip-auth belong only in test code and test binaries.
- Keep the switch-style configuration check; unavoidable third-party names need a narrow allowance and a one-line reason.
- README.md records implementation and qualification gaps. Do not claim unshipped behavior or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards.
- Each area's Fast suite must finish in under 30 s wall time; each single test must finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. Keep default parallelism.
- Test only the happy path and critical paths in Fast; move heavy critical tests to Slow and delete everything else. New features add at most one happy-path and one critical-failure test.
