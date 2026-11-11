# Journey suite (`e2e/`)

Playwright Test (TypeScript) that drives the real services built from the commit under test: TerraBase, ts-auth
and the dashboard behind a Caddy edge that serves the production host names, a per-run CA, Mailpit as the SMTP
server and a Cloudflare send-API sink that must stay empty. CI runs it through `.github/workflows/journey.yml` on the
merge commit GitHub tests for a pull request, unless every file the pull request changes is a `*.md` file or sits
under `docs/`, `docs-site/`, `status-site/` or `landing-site/` other than `landing-site/static/pricing.json` (the
`journey` filter of `.github/workflows/ci.yml`). Pushes to `main` and dispatches never run it.

## Run it on your machine (no root)

```sh
bash e2e/env/local.sh        # build, enter the namespaces, run the egress guard, boot, run the suite, stop
```

Needs the .NET SDK of `global.json`, Node 24 with npm, Go (`sdk/go/.go-version`), Rust through rustup
(`e2e/sdk/rust/rust-toolchain.toml`), Python 3.12, git, make, curl, openssl, tar, sha256sum, `certutil` (Ubuntu
`libnss3-tools`; NixOS `nix shell nixpkgs#nss.tools`), iproute2 `ip`, procps `pgrep`, util-linux `unshare`, `nsenter`
and `mount`, a CA bundle at `/etc/ssl/certs/ca-certificates.crt`, and unprivileged user namespaces; Ubuntu 23.10 and
later restrict those through AppArmor until `sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0`, the one
command CI runs with sudo. The `ubuntu-24.04` runner image has all of them; CI installs only the pinned versions of
the .NET SDK, Node and Go through setup actions and of Rust through rustup. `local.sh start` creates user, network,
mount and PID namespaces. The network has only loopback: the production names resolve to the local edge and every
other name or address fails to connect, so nothing reaches production. Everything the suite starts runs inside
through `local.sh exec`. `local.sh stop` asks the namespace's first process to stop the stack in order and exit, kills
it if it is still there after 30 s, and returns once no process is left inside: the kernel kills them all when that
first process ends (`stop` fails if one is still there 10 s after the kill). CI enters through the same `local.sh`.
To iterate: `bash e2e/env/local.sh prepare`, `bash e2e/env/local.sh start`,
`bash e2e/env/local.sh exec bash e2e/env/up.sh`, then from `e2e/`
`bash env/local.sh exec npx playwright test --project pages-desktop`, and `bash e2e/env/local.sh stop`.

## Stages

`setup` signs up, reads the code from Mailpit, onboards on Free, creates a database and an API key. `desktop-setup`
does the same for a second user whose only project is `pages-desktop`; its teardown `desktop-close` only deletes that
account. CI runs the two users in two shards, each with the `projects` of its row in the journey matrix of
`journey.yml`. In the phone shard, once `setup` has passed, the tests of `pages-mobile`, `hosted`, `interactions` and
`invitation` share six workers. Playwright queues them in the order `playwright.config.ts` lists the projects, so
in practice the flows start once every page test has started; nothing enforces that order and no test depends on it.
The organization audit is read through the tab's own Action filter, and its second page is a state of its own (the
phone owner's organization holds 12 audit events before `finish`; the two pages show 20). `finish`, the teardown of
`setup`, starts when all four have ended and runs its tests in order in one worker: SDK group A, the database
deletion through the UI, SDK group B, a rename and the account deletion. A teardown whose setup failed fails once
with "setup did not finish" and runs nothing else.

## The SDK stage and the Free plan's request budget

TerraBase admits each organization's requests through a token bucket (Free: 50 per second, burst 100;
`src/TerraBase/Features/Qos/QosPolicy.cs:10`) and refuses the rest with `429 tenant_busy`. The Go and Rust SDKs
resend a refused REST call after `Retry-After`, and the JavaScript and .NET SDKs resend refused reads, so a refusal
does not always fail an operation. `support/sdk.ts` runs the five runners one at a time, and starts a runner only
when a model of the bucket holds that runner's `requestsPerRunner` plus 20 (each group starts the model at 0 tokens
and books a runner's requests when it exits). Around each runner it reads TerraBase's own counters for the journey
organization's lane from `http://127.0.0.1:9091/metrics` (`terrabase_qos_request_duration_seconds_count` and
`terrabase_qos_rejected_total` with `plan="hobby"` and the organization's `tenant_bucket`); each reading waits until
the metrics differ from the last reading, because the exporter repeats the same answer for about 300 ms.
A runner fails when TerraBase refused any of its requests or admitted more than its booked count, with a message that
names the runner, the count and `requestsPerRunner`, and when TerraBase admitted none (the metric moved). Each runner
prints `<sdk> group <a|b>: TerraBase admitted N of the M requests booked and refused 0`. The 20 tokens of headroom
absorb requests in the lane that no runner's readings count, between two runners; none are expected. After a change
to an SDK call or to `sdk/operations.json`, run `finish` locally and set `requestsPerRunner` to the admitted counts
the runners print.

## Add a page, a dialog, a hosted page, a flow, a secret, a host name or an SDK operation

- A page or tab: one state in `pages/inventory.ts` with its URL, the link pattern it covers (only the query values a
  test visits), the heading or landmark only it has (the type requires one) and its data checks; both page projects
  audit it. A dialog opened by a URL parameter (`?create=1`): a state whose heading is the dialog's own (`inDialog`)
  and whose pattern lists that parameter. A dashboard URL that only a flow audits goes in `journeyPages` of
  `support/links.ts`. If you forget, every audited page whose same-origin `a[href]` or GET form leads there fails
  with "link to … matches no audited page and no exclusion"; a page or dialog reached only by a button, a Datastar
  action or a POST is not caught.
- A hosted page (`auth.terrascale.tech`): its path in `hostedPages` of `support/links.ts`, and a test that calls
  `audit.check` on it (`hosted/hosted.spec.ts` or a flow). An audited ts-auth page that links to a ts-auth path not
  in the list fails with "… matches no audited hosted page".
- A flow or a project: a project in `playwright.config.ts` with `dependencies: ['setup']` and
  `metadata: { setup: 'setup' }` (the user whose journey and session it reads; `desktop-setup` for the desktop user),
  added to one shard's `projects` in `journey.yml` (a project in no shard never runs in CI). A project that types or
  shows a secret outside the registered owner session uses `secretFree` (no trace); only the two page projects keep
  traces. A flow that opens its own browser context does it through `auditedContext` and calls `saveCookies` on it.
- A screen that shows a secret: `saveSecret` the value the moment the test reads it, and add the element's selector
  to `secrets` in `support/audit.ts`, which masks it in every screenshot.
- A host name: one line in `env/hostnames` (the namespace resolves it to the edge, the edge certificate names it and
  the browser may reach it) and, for HTTPS, a site in `env/Caddyfile`.
- An SDK operation: one entry in `sdk/operations.json` (from `sdk/SQL-API.md`), its step in all five runners under
  `sdk/<sdk>/`, and the new request count in `requestsPerRunner` of `support/sdk.ts` (see above). A runner without
  the step reports it failed ("no implementation for …" in Go, Rust and .NET, a TypeError or KeyError in
  JavaScript and Python); a request count that is too low fails the runner; an SDK method with no entry in
  `operations.json` fails nothing. An operation the PR environment cannot run carries a `needs` reason and is
  reported as not run.

## Rules

- Real services only: never fake our own code; only third parties are stood in for (Mailpit for SMTP, a sink for
  Cloudflare's send API, a closed edge for Stripe). One exclusion: our status site (`status-site/`, Cloudflare
  Workers on D1) does not run here, so the edge answers `status.terrascale.tech` with 404 (`env/Caddyfile:38-41`)
  and the dashboard's Regions card (`HomeMeasurements.cs:194`) is tested on its unavailable path only.
- Every audited state fails on a console error, warning or failed `console.assert`, an uncaught page error, a failed
  request, an HTTP status of 400 or more, a broken `<img>`, horizontal overflow of the document, an axe WCAG 2.2 AA
  violation or an uncovered same-origin link. What passes on purpose: the other console types (`log`, `debug`,
  `info`, `dir`, `dirxml`, `table`, `trace`, `clear`, the group, profile, count and time types), a status below 400,
  and a status a test expects for its own page with `audit.expectStatus`, matched by URL and status whatever the
  order of its response and console events, and seen exactly once. Not checked: links to another origin than the
  page's own, every link on the MCP and platform origins, popups, WebSocket frames, lazy-loaded and SVG images,
  overflow inside an element, and axe's best-practice rules and incomplete results.
- No retries, no fixed sleeps, no `waitForTimeout`: every wait ends on a condition. The SDK stage's pacing waits are
  computed from the request budget; the SDKs' own resends of a refused request are caught by the lane check.
- Step-ups: ts-auth issues at most 10 per user in 15 minutes (`StepUpProofs.cs:60-62,94`); a run spends 1 for the
  desktop owner (account deletion), 6 for the phone-and-flows owner (the access token in `interactions`, 4 in
  `hosted`, account deletion) and none for any other user; API keys need none.
- Secrets: every value the run generates is registered through `saveSecret` in `support/state.ts` or in
  `$E2E_STATE/secrets` (the service keys of `env/up.sh`) and masked in the job log: passwords before their first
  use, API keys and the access token right after the test reads them, the owner's cookies when `setup` saves its
  session, every cookie of a test's own context when the test ends, pass or fail, and the cookies of a context a
  flow opens itself when that flow passes (those flows keep no trace); the runners write their scoped keys to
  `secrets.d` before they use them. Before anything uploads, `env/scrub.py` replaces each value, raw, URL-encoded and
  JSON-escaped, with `[redacted]` in every file, inside zip archives and inside the report's embedded zip, then
  checks none is left; a file it cannot rewrite is deleted and named, and the step fails, so nothing uploads. Once
  the namespace step has passed, Stop runs before the scrub, even after a timeout or a cancel, and ends every
  process inside, so nothing the suite started writes after the scrub lists its files. Text is redacted; pixels are
  not scanned: screenshots mask the secret fields by selector and videos are off. The 6-digit codes are not redacted
  (single use, under 16 characters). Every secret of a PR run is generated in that run and dies with the VM: `ci.yml`
  passes no secret to `journey.yml`, which grants `contents: read` only, checks out with `persist-credentials: false`
  and uses no repository secret. Never print a secret.
