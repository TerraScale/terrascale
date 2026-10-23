# Live E2E integration

**2026-10-22: E2E-LIVE moved to ts-orch.** The live target is the disposable ts-orch
service `terrabase-e2e` (gru, sjc, fra), reached per region over NetBird; a region stops by applying the service
without it. Credential: one ts-auth project API key of the e2e project (`TERRABASE_E2E_ORCH_KEY`, user-created).
The outage proof is `make terrabase-e2e-orch-outage`. See docs/terrabase/e2e-ts-orch.md.

The local qualification records remain in [COVERAGE.md](COVERAGE.md) and [RUNS.md](RUNS.md).
They are not ts-orch deployment evidence. No ts-orch acceptance result is recorded here.
