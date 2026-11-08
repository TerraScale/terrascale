# ts-status deployment pipeline

The fixed /api/v1/deployments endpoint observes TerraBase, ts-auth, ts-status
and the dashboard from the gru and sfo vantage Durable Objects and also reports
the revision each vantage runs. It accepts no caller-controlled target.
Observations share the existing 10-second edge cache per colo, and concurrent
cache misses coalesce within a Worker instance. Query strings cannot bypass
that cache. Cached observations keep their original timestamps; clients receive
`no-store`, so they cannot extend proof freshness. Invalid vantage payloads
produce unknown observations for that region while preserving the other region.

Cloudflare Workers Builds, connected to the GitHub repo, deploys every push to
main with the deploy command `npm run deploy`. GitHub Actions
(`.github/workflows/ci.yml`, GitHub-hosted runners) is the merge gate for pull
requests and merge groups: its `test` job runs the dependency audit, `npm run
check`, `npm test`, the Python tests and `npm run build`. It must pass on the
exact head being merged. Non-production branch builds are off.

`npm run deploy` builds, bootstraps D1 from schema.sql, then deploys the one status Worker,
`ts-status`. Its four vantages (sfo, gru, fra, nrt) are Durable Objects inside
it, created with the location hints wnam, sam, weur and apac, so there is no
other Worker to deploy. Pull requests get no preview deploy and the Worker has
no preview URLs. Cloudflare runs it all;
ts-status has no process on OCI, so it keeps working while the platform is down.

Workers Builds deploys with its own build token. The status Worker's admin,
subscription and probe credentials are Worker secrets.

The build's `WORKERS_CI_COMMIT_SHA` supplies the status Worker's `VERSION`
binding. `GET https://status.terrascale.tech/health/version`
returns that full revision and the colo the request entered (not proof of
where any vantage ran) without caching or
reading D1; after a deploy, check that its revision matches the merged commit.

## Monitoring coverage

Each round records every answering vantage with the colo it reports from its
own `/cdn-cgi/trace`. `/api/v1/status` reports `monitoringCoverage` and
`/api/v1/regions` each vantage's requested hint, observed colo, last answer and
coverage state. A vantage that did not answer the latest round, gave no
location, or ran outside its area (`colos` in src/probe/vantages.ts) is a
coverage failure, shown apart from service health. Durable Objects hinted to
`sam` start in Eastern North America, so the South America vantage is expected
to report a coverage failure until it runs in South America.
