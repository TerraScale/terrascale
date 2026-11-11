# ts-status deployment pipeline

The fixed /api/v1/deployments endpoint observes TerraBase, ts-auth, ts-status
and the dashboard from the gru and sfo vantage Durable Objects and also reports
the revision each vantage runs. It accepts no caller-controlled target.
Observations share the existing 10-second edge cache per colo, and concurrent
cache misses coalesce within a Worker instance. Query strings cannot bypass
that cache. Cached observations keep their original timestamps; clients receive
`no-store`, so they cannot extend proof freshness. Invalid vantage payloads
produce unknown observations for that region while preserving the other region.

ts-status lives in `status-site/` of the terrascale monorepo.
On every pull request that touches `status-site/`, the `status` job of
`.github/workflows/ci.yml` runs `npm ci`, `npm run check`, `npm test`, the
Python tests, `npm run lint` and `npm run build` on a GitHub-hosted runner.
On main, the `deploy-status` job of `.github/workflows/sites.yml` waits for the
`status-deploy` lock, checks out the tip of `main`, builds it and deploys it
with `npm run deploy:prebuilt` (the two vantage Workers, then `ts-status`) using
the repository's `CLOUDFLARE_API_TOKEN`, and checks that the live revision is
the commit it checked out. Pull requests never deploy and the Worker has no
preview URLs.
Cloudflare runs it all; ts-status has no process on OCI, so it keeps working
while the platform is down.

The Worker applies `schema.sql` to D1 on first use, so a deploy needs no
separate D1 step. The status Worker's admin, subscription and probe
credentials are Worker secrets.

The build's `REVISION` environment variable, the commit the deploy job checked
out, supplies the status Worker's `VERSION` binding.
`GET https://status.terrascale.tech/health/version` returns that full revision
and the colo the request entered (not proof of where any vantage ran) without
caching or reading D1; `deploy-status` checks it matches that commit.

## Monitoring coverage

Each round records every answering vantage with the colo it reports from its
own `/cdn-cgi/trace`. `/api/v1/status` reports `monitoringCoverage` and
`/api/v1/regions` each vantage's requested hint, observed colo, last answer and
coverage state. A vantage that did not answer the latest round, gave no
location, or ran outside its area (`colos` in src/probe/vantages.ts) is a
coverage failure, shown apart from service health. Durable Objects hinted to
`sam` start in Eastern North America, so the South America vantage is expected
to report a coverage failure until it runs in South America.
