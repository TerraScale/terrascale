# SDK tests use HTTP contracts

This policy supersedes the binary qualification instructions in README.md,
CHANGELOG.md, docs/release.md and the older repository agent rules.

Run `npm test` for the HTTP fake contracts and fixture checks. SDK tests, CI and
helpers must never download or run a TerraBase binary outside the monorepo.
The `provision:terrabase` and `test:integration` commands and their executable
helpers have been removed. No replacement binary runner is supported.

Optional environment-gated HTTP tests remain available against an independently
managed, authorized isolated service. They use the existing `TB_V1_URL`,
`TB_V1_PROJECT`, `TB_V1_DATA_KEY`, optional scoped keys and fixture collections;
they neither install nor start a service. These HTTP tests are separate from the
fake-only CI and are not evidence of qualification until actually executed.

`scripts/ci/terrabase-release.json` remains an archived contract-input digest pin.
The route generator and fixture integrity tests still consume it. Its historical
release metadata does not authorize downloading or running that release.
