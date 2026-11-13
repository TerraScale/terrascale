# Recover CI and deployment

Deployments and SDK releases require a successful main-push CI run for their exact
revision, including both journey shards and `all-green`. A pull-request run or
maintenance dispatch cannot replace that proof. Re-run failed jobs of the main
push CI run after fixing its failure. A revision with no main-push CI run cannot
be qualified by dispatching CI.

Normal CI dispatches run the journey and both core services. Build-only and
release-only maintenance dispatches verify the existing main-push proof with
`qualified-main` instead of running another journey during the maintenance window.
Native-artifact maintenance does not qualify a revision.

## Recover the dashboard

The dashboard needs its own push or dispatch Images run. Its `test` job builds
and checks the application that run publishes. A failed application check cannot
qualify a deployment. A failed release can be recovered by re-running the Images
push run:

```sh
gh run rerun <images-push-run-id> --repo TerraScale/terrascale
```

CI completion calls `heal`. The Images push run also calls it after the application
build and its image job finish. If CI finishes first, heal dispatches Images on
main while the push run is still active. If the build finishes first, the later
CI completion triggers the handoff. A dispatch already in progress owns the
handoff. A completed original run can be re-run for the same signed revision.
Dispatches still run dashboard-ci again.

The recovery dispatch signs the main head at creation. It must pass main-push
qualification for that exact revision. An older Images run keeps signing its own
revision after unrelated pushes. The `dashboard-release` lock serializes release
jobs. Inside it, the release step checks the newest eligible commit and the live
revision again. A second contender sees the first deployment live and cannot
deploy it again. A failed readiness read stops deployment.

Inspect the CI and Images run URLs and the final live readiness revision before
reporting recovery. A successful handoff alone is not deployment proof.

## Recover the sites

Docs, landing and status deploy from `.github/workflows/sites.yml` (`Sites`).
Recover pending site changes on qualified main with:

```sh
gh workflow run sites.yml --repo TerraScale/terrascale --ref main
```

Each job waits for its site's lock and checks out the exact qualified revision.
It reads the live Worker version message under that lock. Only a strict ancestor
can advance to the candidate revision; an equal or newer live revision is left
alone. A Worker with no recorded revision deploys the qualified revision.

The path filter compares the live revision with the candidate. It includes
changes from earlier commits whose CI failed or whose site deployment never
completed. Status Workers are selected separately, so recovery after a partial
deploy advances only the Workers that still need it.

The deploy records its commit as the Worker version message.
`scripts/ci/prove-worker-deploy.mjs` reads Cloudflare's records and requires each
selected Worker to serve its recorded version at 100%, with that commit as its
message.

## SDK tags and production alerts

Older tags without a journey-qualified main-push CI run are refused. Push an SDK
tag only after main CI is green for that exact commit. A tag pushed earlier fails
and needs a re-run after CI succeeds.

Production-journey reports update one alert at a time. GitHub retains one pending
report per concurrency group. A newer pending report replaces an older pending
report. The newest state wins.
