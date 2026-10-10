# ts-dashboard deployment pipeline

Part of [TS-768](https://ku.mariogk.com/terrascale/issue/TS-768).

Every main push verifies public Go modules, generated assets, lint, unit tests
and all browser shards. The production job depends on every gate in the same
workflow. The actual merge commit must pass; pull requests never deploy.
Main jobs queue instead of cancelling an active delivery.

The job writes the full revision's deployment/qualified commit status before
calling Fly. Fly builds with VERSION set to that revision and replaces the
production machine. CI verifies the required machine settings, no persistent
volumes and the exact /health/ready revision. A healthy older machine fails.
The existing native-login smoke and its protected rollback remain in place.

OCI Vault must deliver the vendor-issued Fly deployment credential automatically.
The existing repository-secret consumer and demo-dependent deployment validation
are implementation gaps. Production requires persistent state and real ts-auth
authorization; it must not use a demo binary. The current Fly contract uses one
warm machine. The global Fly endpoint is reachable from GRU and SFO;
those observations do not claim there are two regional Dashboard replicas.
Regional scaling belongs to the service's separate architecture work.

The orchestrator's five-minute deployment-lag guard compares qualified main
with uncached GRU/SFO observations through ts-status. It alerts when the oldest
pending qualified build exceeds 30 minutes; new commits cannot reset that
clock. The guard checks the served revision, not only Fly's command result.

Merge a no-op Go source change for acceptance. Record merge time, main gates
complete, Fly deployment complete, and the first matching GRU/SFO observations
of https://ts-dashboard.fly.dev/health/ready. Report merge-to-live and
green-to-live together with the CI run and machine ID.
