# Journey suite (`e2e/`)

Playwright Test (TypeScript) that drives the real services built from the commit under test: TerraBase, ts-auth
and the dashboard behind a Caddy edge that serves the production host names, a per-run CA, Mailpit as the SMTP
server and a Cloudflare send-API sink that must stay empty. CI runs it on every pull request through
`.github/workflows/journey.yml`, on the merge commit GitHub tests for the pull request.

## Run it on your machine (no root)

```sh
bash e2e/env/local.sh        # build, enter a network namespace, run the egress guard, boot, run the suite, stop
```

Needs the .NET SDK of `global.json`, Node 24, Go, Rust, Python 3.12, `certutil` (Ubuntu `libnss3-tools`; NixOS
`nix shell nixpkgs#nss.tools`) and util-linux `unshare` and `nsenter` with unprivileged user namespaces; Ubuntu 23.10
and later restrict those through AppArmor until `sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0`, the
one command CI runs with sudo. The namespace has only loopback: the production names resolve to the local edge and
every other name or address fails to connect, so nothing reaches production; CI enters it through the same
`local.sh`. To iterate: `bash e2e/env/local.sh prepare`, `bash e2e/env/local.sh start`,
`bash e2e/env/local.sh exec bash e2e/env/up.sh`, then from `e2e/`
`bash env/local.sh exec npx playwright test --project pages-desktop`, and `bash e2e/env/local.sh stop`.

## Stages

`setup` signs up, reads the code from Mailpit, onboards on Free, creates a database and an API key. Then
`pages-mobile`, `hosted`, `interactions` and `invitation` run in parallel. `finish` (the teardown of `setup`) runs
SDK group A, deletes the database through the UI, runs SDK group B and deletes the account. `desktop-setup` makes a
second user for `pages-desktop`, and its teardown `desktop-close` only deletes that account. CI runs the two users in
two shards; a teardown whose setup failed fails once with "setup did not finish" and runs nothing else. `finish`
runs the five SDK runners one at a time, nothing else uses the organization meanwhile, and `support/sdk.ts` starts
each runner only when a model of the Free plan's request budget (50 per second, burst 100; `QosPolicy.cs:10`) holds
that runner's requests plus 20; a 429 still fails the run.

## Add a page, a dialog or an SDK operation

A page or tab: one state in `pages/inventory.ts` with its URL, the link pattern it covers, the heading or landmark
only it has (the type requires one) and its data checks; both page projects audit it. A dialog opened by a URL
parameter (`?create=1`): a state whose heading is the dialog's own (`inDialog`) and whose pattern lists that
parameter. If you forget, every audited page whose same-origin `a[href]` or GET form leads there fails with "link
to … matches no audited page and no exclusion"; a page or dialog reached only by a button, a Datastar action or a
POST is not caught. An SDK operation: one entry in `sdk/operations.json` (from `sdk/SQL-API.md`), its step in all
five runners under `sdk/<sdk>/`, and the per-runner request count in `support/sdk.ts`. A runner without the step
reports it failed ("no implementation for …" in Go, Rust and .NET, a TypeError or KeyError in JavaScript and Python);
a stale request count is absorbed up to the 20 tokens of headroom, then the run fails on 429; an SDK method with no
entry in `operations.json` fails nothing. An operation the PR environment cannot run carries a `needs` reason.

## Rules

- Real services only: never fake our own code; only third parties are stood in for (Mailpit for SMTP, a sink for
  Cloudflare's send API, a closed edge for Stripe). One exclusion: our status site (`status-site/`, Cloudflare
  Workers on D1) does not run here, so the edge answers `status.terrascale.tech` with 404 (`env/Caddyfile:38-41`)
  and the dashboard's Regions card (`HomeMeasurements.cs:194`) is tested on its unavailable path only.
- No allow-list: every audited state fails on a console error or warning, an uncaught page error, a failed request,
  an HTTP status of 400 or more, a broken `<img>`, horizontal overflow of the document, an axe WCAG 2.2 AA violation
  or an uncovered link. A page whose purpose is an error expects its status in its own test (`audit.expectStatus`),
  matched by URL and status whatever the order of its response and console events.
- No retries, no fixed sleeps, no `waitForTimeout`: every wait ends on a condition. The SDK stage's pacing waits are
  computed from the request budget, not fixed.
- Step-ups: ts-auth issues at most 10 per user in 15 minutes (`StepUpProofs.cs:59-61`); a run spends 1 for the
  desktop owner (account deletion), 6 for the phone-and-flows owner (the access token in `interactions`, 4 in `hosted`,
  account deletion) and none for any other user; API keys need none.
- Secrets: every value the run generates is registered when it exists, through `saveSecret` in `support/state.ts`
  (passwords, API keys, the access token, every cookie of every context; the runners write their scoped keys to
  `secrets.d`) or in `$E2E_STATE/secrets` (the service keys of `env/up.sh`), and is masked in the job log. Before
  anything uploads, `env/scrub.py` replaces each value, raw, URL-encoded and JSON-escaped, with `[redacted]` in every
  file, inside zip archives and inside the report's embedded zip, then checks none is left; a file it cannot rewrite
  is deleted and named, and the step fails, so nothing uploads. Text is redacted; pixels are not scanned: screenshots
  mask the secret fields by selector and videos are off. The 6-digit codes are not redacted (single use, under 16
  characters). Every secret of a PR run is generated in that run and dies with the VM: `ci.yml` passes no secret to
  `journey.yml`, which grants `contents: read` only, checks out with `persist-credentials: false` and uses no
  repository secret. Never print a secret.
