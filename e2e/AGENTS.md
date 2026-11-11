# Journey suite (`e2e/`)

Playwright Test (TypeScript) that drives the real services built from the commit under test: TerraBase, ts-auth
and the dashboard behind an edge that serves the production host names (Caddy for HTTPS, a PostgreSQL TLS edge for
`pg.terrascale.tech`), a per-run CA, Mailpit as the SMTP server, a Cloudflare send-API sink that must stay empty and a
Stripe stand-in for the API and Checkout. CI runs it through `.github/workflows/journey.yml` on the
merge commit GitHub tests for a pull request, unless every file the pull request changes is a `*.md` file or sits
under `docs/`, `docs-site/`, `status-site/` or `landing-site/` other than `landing-site/static/pricing.json` (the
`journey` filter of `.github/workflows/ci.yml`). Pushes to `main` and dispatches of `CI` never run it on a local stack;
after each release the same specs run against production ("The production target" below).

## Run it on your machine (no root)

```sh
bash e2e/env/local.sh        # build, enter the namespaces, run the egress guard, boot, run the suite, stop
```

Needs the .NET SDK of `global.json`, Node 24 with npm, Go (`sdk/go/.go-version`), Rust through rustup
(`e2e/sdk/rust/rust-toolchain.toml`), Python 3.12, git, make, curl, openssl, tar, sha256sum, jq, `certutil` (Ubuntu
`libnss3-tools`; NixOS `nix shell nixpkgs#nss.tools`), `psql` of PostgreSQL 16 or later for `sslrootcert=system`
(NixOS `nix shell nixpkgs#postgresql`), iproute2 `ip`, procps `pgrep`, util-linux `unshare`, `nsenter`
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

## The production target

`bash e2e/env/production.sh` runs the same specs and config against production from the machine's own network: no
namespace, no stack, no argument and no URL to set. `.github/workflows/production-journey.yml` runs it on `main` only,
never for a pull request: once after the last release of each commit (the rule is in the root `AGENTS.md`), daily at
13:23 UTC and on dispatch, one run at a time. It is not a gate. `production.sh`:

- refuses to run when an `E2E_*`, `TERRASCALE_*`, `PW_*`, `PLAYWRIGHT_*`, `NODE_EXTRA_CA_CERTS`, `NODE_OPTIONS`,
  `NODE_TLS_REJECT_UNAUTHORIZED`, `CURL_CA_BUNDLE`, `REQUESTS_CA_BUNDLE` or proxy variable is set, or when a
  `*.terrascale.tech` name of `env/hostnames` resolves to a loopback address (the namespace of `local.sh`); it sets
  `E2E_TARGET=production`, the state (`.state/production`, emptied first), the runners (`.state/sdk`) and the system
  CA bundle itself;
- asks mail.tm for an active domain before the first request to production ("inbox provider … production was not
  contacted" when it cannot), records both `/readyz` answers in `production-report/readyz.txt`, runs the projects of
  both pull-request shards but the paid flow (179 tests), and records both answers again after the suite. A new
  project runs against production only once it is added there; `paid`, `paid-invitation` and `paid-finish` stay
  local because they pay through the Stripe stand-in.

What the target changes (`E2E_TARGET`, read by `support/stack.ts`):

- Mail: `newAddress` creates a fresh mail.tm inbox for each sign-up on the domain the preflight found
  (`E2E_INBOX_DOMAIN`), and `nextMail` waits for its mail for at most 120 s (`mailWait`; the test timeout is 60 s plus
  that wait), reading the message inside that bounded poll. mail.tm refuses bursts from one address (its
  `RateLimit-Policy` is 10 requests a minute, and one a minute for `POST /accounts`), so every mail.tm call takes the
  next free slot of one series in the state (`inbox-requests`, slots 7 s apart, in `support/inbox.ts` and
  `env/production.sh`): eleven consecutive calls span at least ten gaps, 70 s, so they never share a 60-s window even
  when one reaches mail.tm up to 10 s later than another. Inbox creations take only every tenth slot, 70 s apart. The
  preflight's `GET /domains` claims the run's first slot and calls at once. A test's wait for its slot is added to
  its timeout. Inboxes are never deleted: a deleted address can be registered again by anyone, while the
  account a run leaves behind still uses it, and mail.tm refused deletes under that budget. The inbox's address,
  password and token are registered secrets. Errors name the "inbox provider".
- Not run on production (`onLocalStack`): the Cloudflare fallback count of `setup`, because production shows no
  sender; the sign-up submission of the four `hosted` tests: each would send a production mail, and `setup`,
  `desktop-setup` and `invitation` already audit the verify page; and the global teardown's check of the Stripe
  stand-in's unexpected calls (`support/teardown.ts`), because production has no stand-in.
- The SDK stage keeps the pacing and every per-operation check. TerraBase's lane counters (`127.0.0.1:9091`) exist only
  on the local target, so a refused request that an SDK resends itself (Go and Rust REST calls, JavaScript and .NET
  reads) is caught on pull requests only; on production a refusal fails the run when it reaches the runner.
- The browser may reach only the `*.terrascale.tech` names of `env/hostnames`, never Stripe, Cloudflare or the SMTP
  host, with a fresh home and no extra CA.
- Evidence: no trace, video or screenshot. The run writes `production-report/list.txt` (everything it printed, with
  the list reporter), `report.json` (JSON reporter) and `readyz.txt`; each audited page adds a `url` annotation and
  the SDK runners print their output. Playwright ends a run that reaches 25 minutes (`globalTimeout`, under the
  step's 30), so a slow run still writes `report.json`. A timeout or a cancel signals only the step's shell, so the
  scrub step first kills every process that carries the job's `RUNNER_TRACKING_ID` (the runner marks with it all a
  job starts, and kills them itself only when the job ends) and every process those started (Chromium overwrites its
  environment), until none is left; then `env/scrub.py` redacts the files, and they upload only when that step
  succeeded.
- Accounts: a run signs up three users (`setup`, `desktop-setup`, `invitation`); its last steps delete the two
  owners, the invitation's second user stays.

The workflow fetches only `uv` of the pinned tools (`env/tools.sh uv`): production starts no Caddy, no Mailpit and no
branch action. `env/local.sh` refuses to run when `E2E_TARGET` is set, before it builds anything.

A failed run opens the `Production journey failure` issue (found by its exact title in the REST list of open issues)
with the run link, the trigger and suite commit, both `/readyz` versions, one `Cause:` line when the run knows it
("inbox provider" when mail.tm failed first, "not ready" when a `/readyz` failed, "no report" when the run stopped
before or during the suite, or the suite's own error such as its global timeout), every failed test by name and the first ten with their page and error
(from `report.json`), or the last lines it printed when there is no `report.json`; the text stops under GitHub's
65,536 characters. While the issue is open, a failed run replaces its body and comments only when the failed tests
or the cause changed, naming the tests newly failing and fixed. The next green run closes it. A run during which a
`/readyz` version changed, because a newer release rolled out under it, neither opens, updates nor closes the issue:
its job summary says so, and the newer release's own run follows. Only the dashboard's version names a commit;
ts-auth's names its package version, so a ts-auth rollout during a run goes unseen. Run it from your machine only from a clean shell: `bash e2e/env/local.sh prepare` once for the runners,
then `bash e2e/env/production.sh`; each run makes three production accounts.

## Stages

`setup` signs up, reads the code from Mailpit, onboards on Free, creates a database and an API key. `desktop-setup`
does the same for a second user whose only project is `pages-desktop`; its teardown `desktop-close` only deletes that
account. `paid` signs up a third user, onboards on Free, renames the organization, creates a database, upgrades to Pro
from the billing tab through Checkout (the stand-in's page, its signed `checkout.session.completed` webhook and the
dashboard's return), checks the plan and its limits on the billing and organization pages, sees a webhook signed with
another secret refused, then creates a second database and an API key. `paid-invitation` invites a fourth user, who
signs up through the emailed link, accepts, onboards in the Personal organization every account gets, signs in again
to the organization choice and opens the Pro organization; the owner then sees both members and removes the new one.
`paid-finish`, the teardown of `paid`, runs in order in one worker: SDK group C, the branch action and the account
deletion, which waits for ts-auth's deletion cleanup (every 60 s) to prove the organization deleted, then for the
dashboard's deletion reconciler (every 30 s) to cancel the subscription at the stand-in; the wait is bounded by the
two periods plus 20 s for the reconciler's passes. It then reads the dashboard's `/metrics` until
`terrascale_deletion_cleanup_failing_entries` is 0, within one more period plus 20 s. CI runs the
users in two shards, each with the `projects` of its row in the journey matrix of `journey.yml`: the phone owner's
projects in one, `pages-desktop` and the paid flow in the other. In the phone shard, once `setup` has passed, the
tests of `pages-mobile`, `hosted`, `interactions` and `invitation` share six workers. Playwright queues them in the
order `playwright.config.ts` lists the projects, so in practice the flows start once every page test has started;
nothing enforces that order and no test depends on it. The organization audit is read through the tab's own Action
filter, and its second page is a state of its own (the phone owner's organization holds 12 audit events before
`finish`; the two pages show 20). `finish`, the teardown of `setup`, starts when all four have ended and runs its
tests in order in one worker: SDK group A, the database deletion through the UI, SDK group B, a rename and the account
deletion. A teardown whose setup failed fails once with "setup did not finish" and runs nothing else.

## The SDK stage and the plans' request budgets

TerraBase admits each organization's requests through its plan's token bucket (Free: 50 per second, burst 100; Pro:
500 per second, burst 1000; `src/TerraBase/Features/Qos/QosPolicy.cs:10,12`) and refuses the rest with
`429 tenant_busy`. Groups A and B run in the phone owner's Free organization; group C (a fork, the scoped key refused
on another database) runs in the paid owner's Pro organization with its API key, and its runners also get the first
database's id (`E2E_DATABASE_ID`) and the second database's name (`E2E_OTHER_DATABASE`). The Go and Rust SDKs resend a
refused REST call after `Retry-After`, and the JavaScript and .NET SDKs resend refused reads, so a refusal does not
always fail an operation. `support/sdk.ts` runs the five runners one at a time, and starts a runner only when a model
of the bucket holds that runner's `requestsPerRunner` plus 20 (each group starts the model at 0 tokens and books a
runner's requests when it exits). Around each runner it reads TerraBase's own counters for the organization's lane
from `http://127.0.0.1:9091/metrics` (`terrabase_qos_request_duration_seconds_count` and
`terrabase_qos_rejected_total` with the plan's label, `plan="hobby"` or `plan="pro"`, and the organization's
`tenant_bucket`); each reading waits until the metrics differ from the last reading, because the exporter repeats the
same answer for about 300 ms, and the reading after a runner is the second new answer, made after the runner exited
even when another worker's readings refresh the exporter while it runs. A runner fails when TerraBase refused any of
its requests or admitted more than its booked count, with a message that names the runner, the count and
`requestsPerRunner`, and when TerraBase admitted none (the metric moved). Each runner prints
`<sdk> group <a|b|c>: TerraBase admitted N of the M requests booked and refused 0`. The 20 tokens of headroom absorb
requests in the lane that no runner's readings count, between two runners; none are expected. After a change to an
SDK call or to `sdk/operations.json`, run `finish` (groups A and B) or `paid-finish` (group C) locally and set
`requestsPerRunner` to the admitted counts the runners print.

## The branch action

`paid-finish` runs `branch.sh` of `TerraScale/branch-action` with the environment its `action.yml` gives it (a pull
request event of its own, `GITHUB_OUTPUT` under `$E2E_STATE`) against the paid owner's second database with the
paid API key: create, create again (the same branch), reset (a new branch, the previous one kept) and delete. It
queries and writes through the printed connection URL with `psql` over the PostgreSQL edge, the password in
`PGPASSWORD`, and checks the dashboard's database list after each step. `env/tools.sh` fetches the action at the
commit in `branch_action`, checked by the archive's SHA-256, and runs the action's own `install-ts.sh`, which installs
the released `ts` it pins by version and SHA-256. To move the pin, set `branch_action` to the new commit and the
SHA-256 to its archive's.

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
- A Stripe call the dashboard makes: one route in `api` of `env/stripe.mjs` for exactly that method and path, whose
  answer holds only the fields the dashboard reads plus those Stripe.net needs to deserialize, named as in the entity
  classes of the Stripe.net version `Directory.Packages.props` pins (its `StripeConfiguration.ApiVersion` is the only
  `Stripe-Version` the stand-in accepts). A list route names the filters the dashboard must send (the customer, and
  `status=all` on subscriptions), so a list call without them is unexpected like a call to no route: either gets 404
  and a line in the stand-in's `unexpected` list, which fails the run when it ends (`support/teardown.ts`). A route
  also checks a parameter whose range Stripe checks, after its idempotency replay as Stripe does: a Checkout
  Session's `expires_at` outside 30 minutes to 24 hours from now gets Stripe's 400 `invalid_request_error` for that
  `param` and a line in `unexpected`. Only a `POST` is replayed by its idempotency key, as at Stripe, where a key on
  `GET` or `DELETE` has no effect: a second cancel runs again, and the cancel of a canceled subscription, which Stripe
  keeps immutable except for its metadata and `cancellation_details`, gets 400 `invalid_request_error` and a line in
  `unexpected`. Each
  line of its call log holds the method, the path, the query keys and the expand paths, and no other query or form
  value.

## Rules

- Real services only: never fake our own code; only third parties are stood in for (Mailpit for SMTP, a sink for
  Cloudflare's send API, `env/stripe.mjs` for Stripe's API and Checkout page: in-memory customers, sessions and
  subscriptions, the run's `STRIPE_SECRET_KEY` required, webhooks signed with the run's `STRIPE_WEBHOOK_SECRET`). The
  edge stands in for the ts-orch edge: Caddy for HTTPS and `env/pg-edge.mjs` for PostgreSQL on
  `pg.terrascale.tech:5432` (`SSLRequest`, TLS with the edge certificate, then TerraBase's listener). One exclusion:
  our status site (`status-site/`, Cloudflare Workers on D1) does not run here, so the edge answers
  `status.terrascale.tech` with 404 (`env/Caddyfile:38-41`) and the dashboard's Regions card
  (`HomeMeasurements.cs:194`) is tested on its unavailable path only.
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
  `hosted`, account deletion), 1 for the paid owner (account deletion) and none for any other user; API keys need
  none.
- Secrets: every value the run generates is registered through `saveSecret` in `support/state.ts` or in
  `$E2E_STATE/secrets` (the service keys of `env/up.sh`) and masked in the job log: passwords before their first use,
  API keys and the access token right after the test reads them, the owner's cookies when `setup` saves its session,
  every cookie of a test's own context when the test ends, pass or fail, and the cookies of a context a flow opens
  itself when that flow passes (those flows keep no trace); the runners write their scoped keys to `secrets.d` before
  they use them, and the branch action's connection URL and scoped key are registered the moment the test reads them
  from `GITHUB_OUTPUT`. Before anything uploads, `env/scrub.py` replaces each value, raw, URL-encoded and
  JSON-escaped, with `[redacted]` in every file, inside zip archives and inside the report's embedded zip, then checks
  none is left; a file it cannot rewrite is deleted and named, and the step fails, so nothing uploads. On a pull
  request, once the namespace step has passed, Stop runs before the scrub, even after a timeout or a cancel, and ends
  every process inside, so nothing the suite started writes after the scrub lists its files; on production the scrub
  step ends them itself ("The production target"). Text is redacted; pixels are not scanned: screenshots mask the
  secret fields by selector and videos are off. The 6-digit codes are not redacted
  (single use, under 16 characters). Every secret of a PR run is generated in that run and dies with the VM: `ci.yml`
  passes no secret to `journey.yml`, which grants `contents: read` only, checks out with `persist-credentials: false`
  and uses no repository secret. Never print a secret.
