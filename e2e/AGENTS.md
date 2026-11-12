# Journey suite (`e2e/`)

Playwright Test (TypeScript) that drives the real services built from the commit under test: TerraBase, ts-auth
and the dashboard behind an edge that serves the production host names (Caddy for HTTPS, a PostgreSQL TLS edge for
`pg.terrascale.tech`), a per-run CA, Mailpit as the SMTP server, a Cloudflare send-API sink that must stay empty, a
Stripe stand-in for the API and Checkout and an R2 stand-in for customer file storage. CI runs it through `.github/workflows/journey.yml` on the
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
The egress guard checks that `status.terrascale.tech` answers 404 from `127.0.0.1`, that
`fra.api.terrascale.tech` answers from `127.0.0.1` in two-region mode regardless of its HTTP status, and that
`sjc.api.terrascale.tech` and `1.1.1.1` cannot connect in either mode.
To iterate: `bash e2e/env/local.sh prepare`, `bash e2e/env/local.sh start`,
`bash e2e/env/local.sh exec bash e2e/env/up.sh`, then from `e2e/`
`bash env/local.sh exec npx playwright test --project pages-desktop`, and `bash e2e/env/local.sh stop`.

## Two local TerraBase regions

Placement work uses an opt-in pair of real cells, with the same builds and namespaces:

```sh
export E2E_REGIONS=2
bash e2e/env/local.sh prepare
bash e2e/env/local.sh start
bash e2e/env/local.sh exec bash e2e/env/up.sh
cd e2e
bash env/local.sh exec npx playwright test --project placement
bash env/local.sh stop
```

The namespace remembers the mode chosen by `start`; later `exec` and `stop` need no switch. Without it, the
Development cell, service startup and default test list stay the same. The placement project is selected explicitly;
selecting it in one-region mode reports its tests skipped with the reason that `E2E_REGIONS=2` is required.

The pair runs as Production because Development refuses `TS_PEERS_FILE`. The peers file in `$E2E_STATE/peers`
names `journey-1 gru 127.0.0.1` and `journey-2 fra 127.0.0.2`. Both use port 8081 on their own address for
authenticated HTTP/2 peer calls, with the run's shared peer credential. Namespace-local routes give a call to one
peer the other's source address, which the pull endpoint uses to identify the replica. The client, metrics and
PostgreSQL ports are 8080/9091/6432 for gru and 8180/9191/6532 for fra; the data directories are
`$E2E_STATE/terrabase` and `$E2E_STATE/terrabase-fra`. The edge and run CA cover
`gru.api.terrascale.tech` and `fra.api.terrascale.tech`. The dashboard and ts-auth keep using gru's internal listener.
Discovery returns gru and fra, both known to the dashboard map; the default cell still identifies as local.

Both regions use the same signed R2 stand-in, with separate `terrascale-gru-files` and `terrascale-fra-files`
object namespaces. Production also requires backup and shape credentials: the same stand-in serves the
`journey-backups` and `ts-terrabase-shapes-prod` buckets, using generated run credentials and HTTPS through the
edge. Its extra platform-bucket operations are accepted only in two-region mode. It implements the single-object
S3 operations, listings and unversioned deletion needed here, without multipart uploads or bucket snapshots.
The throwaway S3 credential is shared across these buckets; production uses separate file and backup identities.

Placement has its own setup account and project key, using the normal journey setup. Its API fixture deletes that
account's seed database to free the Free plan's one database slot. Each scenario deletes its temporary database or
bucket, and `placement-close` deletes the account. Specs added under `placement/` use the placement project's
`metadata.setup` and registered secrets, as the interactions specs do. Run one such spec with:

```sh
bash env/local.sh exec npx playwright test placement/placement.spec.ts --project placement
```

`placement/closure.spec.ts` closes projects across the pair and signs up two owners of its own, because a closed
project cannot be the placement account's. Its first test creates a bucket with a file and a database through fra,
sends a version 2 closure to gru's internal listener with the run's peer credential (the first line of
`$E2E_STATE/secrets`), waits for `200 accepted`, and then sees fra refuse a new bucket, an upload URL and a database
for the project, list no bucket and hold neither the object nor the database; that owner stays, since ts-auth knows
no closure of its project. Its second test upgrades its owner to Pro at the Stripe stand-in for a second project,
creates a database of that project through fra, sees the console's project deletion refused with 409 while a `GET`
of the closure on each internal listener shows no fence in gru or fra, then deletes the database, deletes the project
and deletes the account. Its third test signs up nobody: it sends a closure, the same closure with the header
`X-TerraBase-Closure-Scope: region` and the check of the dashboard's platform project to fra's internal listener, for
both versions, and each answers 409 `platform_project_protected`.

`placement/regions.spec.ts` R16 removes gru from a database's regions and reads gru's data directory
(`$E2E_STATE/terrabase`, through `stateDir`) to see that no file named after the database is left there, then that
the files are back once gru is added again. R17 removes a follower, verifies its routing record learns a rename, and adds it again with current data. R18 deletes a database at its home while its follower is removed, then checks the removed follower forgets it. R19 starts a real, unprimed node in the home region with NATS disconnected and verifies direct placement requests return 503 not_writer for both live and absent ids, while the writer returns 200 and 404 respectively.

Other existing projects can run in two-region mode by starting this same stack first and selecting their project
normally. No two-region CI job is configured; adding one requires its own journey shard with `E2E_REGIONS=2`,
the placement project and the normal prepare/start/up/stop lifecycle. The pair shares one machine and third-party
stand-ins, has no network delay and uses catalog-sweep replication without NATS; it does not model geographic
latency, regional host failure or production's NATS notifications.

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
  password and token are registered secrets. Errors name the "inbox provider" and include the method, path, HTTP status
  and any `RateLimit-*` / `Retry-After` response headers; a 429 with a valid `Retry-After` of at most 60 s (seconds or
  HTTP date) reserves one repeat no earlier than that delay in the same slot series, failing immediately if no slot
  fits before the mail poll deadline, which also bounds its requests.
- Not run on production (`onLocalStack`): the Cloudflare fallback count of `setup`, because production shows no
  sender; `desktop-close`'s check that the deletion never shows the pending page, because production's managed
  snapshots, peer regions and erasure budget can keep a closure pending for a while (there the step still has to reach
  `/account-deleted` by itself, without a click, within its timeout); the sign-up submission of the four `hosted`
  tests: each would send a production mail, and `setup`, `desktop-setup` and `invitation` already audit the verify
  page; and the global teardown's check of the Stripe stand-in's unexpected calls (`support/teardown.ts`), because
  production has no stand-in. The files tests of `interactions/files.spec.ts` are skipped there, and `finish` leaves
  out its object-store checks, because the browser and the runner may not reach the object store on production.
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
its job summary says so, and the newer release's own run follows. Both versions name the commit the service was built
from, so a rollout of either during a run is seen. Run it from your machine only from a clean shell: `bash e2e/env/local.sh prepare` once for the runners,
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

`interactions/files.spec.ts` (the phone owner, local stack only) creates a bucket, uploads through the upload zone,
downloads the same bytes, deletes the file and deletes the bucket in the next click: TerraBase erases a bucket's
deleted files with it. Its second test sees a forged upload and a forged delete refused and a bucket that still has a
file refused with 409, and leaves that bucket with one live file for `finish` (`$E2E_STATE/files-left.json`). Each
test asks the object store itself whether an object is there: `stored` of `support/files.ts` sends the stand-in a
`HEAD` signed with the run's R2 key pair. Just before its account deletion `finish` uploads and deletes one more file
in that bucket and reserves an upload it does not use. After the deletion the live and the just-deleted file are gone
from the object store, the reserved link still takes its file, and that file is gone again within the step's wait
(TerraBase deletes such late objects every 5 s until the link has expired). The test then puts one object under the
project's prefix that no record names, signed with the run's key pair (`putUnnamed`): the state an upload leaves when
it ends after its purge record is gone, six minutes after the reservation, which a run of three minutes cannot reach
by waiting. Only TerraBase's sweep of the closed project's prefix can delete it; a sweep comes each time the age of
the project's marker has doubled, so the wait is bounded by the time since the deletion began plus 15 s. A `finish`
without the bucket of the files tests fails with "left no bucket for the account deletion".

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
`requestsPerRunner` to the admitted counts the runners print. Group A's `origin.rule` sends no request: each runner
builds a TerraBase client and a ts-auth client with every base URL of `sdk/origins.json` and fails, naming the case's
index, when the origin either client takes, or its refusal, differs from the case's second (TerraBase) or third
(ts-auth) entry. Go reads both origins, and JS and Python the ts-auth one, from the request their SDK hands a
recording transport, which sends nothing. A change to the origin rule of `sdk/SQL-API.md` changes that file with it.

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
  keeps immutable except for its metadata and `cancellation_details`, gets 400 `invalid_request_error` without a line
  in `unexpected`, because two cancellers that race send it. Only a cancel that ends a subscription is added to
  `cancels`, where `journey/paid-finish.spec.ts` waits for exactly one entry of the deleted organization. Each
  line of its call log holds the method, the path, the query keys and the expand paths, and no other query or form
  value.

The Stripe journey process also forwards organization-create POSTs to real ts-auth. Its loopback-only
/auth-failure control drops one successful response for a specific checkout key after ts-auth commits, so the
checkout journey exercises a lost create response without replacing ts-auth or adding a production test seam.
Paid Checkout session creation must carry allowed_payment_method_types of card and link.

## Rules

- Real services only: never fake our own code; only third parties are stood in for (Mailpit for SMTP, a sink for
  Cloudflare's send API, `env/stripe.mjs` for Stripe's API and Checkout page: in-memory customers, sessions and
  subscriptions, the run's `STRIPE_SECRET_KEY` required, webhooks signed with the run's `STRIPE_WEBHOOK_SECRET`;
  `env/r2.mjs` for Cloudflare R2 behind `journey.r2.cloudflarestorage.com`, TerraBase's customer file store: in-memory
  objects of the `terrascale-<region>-files` bucket, every request signed with SigV4 by the run's key pair in
  `$E2E_STATE/r2`, presigned or in its headers, an upload refused unless its body matches its
  `x-amz-checksum-sha256`, a `ListObjectsV2` of one project's prefix (`app-files/<organization>/<project>/`, at most
  1000 keys a call; a listing of anything else is unexpected), and CORS for the dashboard's origin only. A request it refuses for any other reason than a
  checksum or a missing object is a line in its `unexpected` list, which fails the run when it ends). The
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
  order of its response and console events, and seen exactly once. A navigation that becomes a download ends in `net::ERR_ABORTED` for the
  download's own URL; that one failure passes, because the browser hands the response to the download. Not checked: links to another origin than the
  page's own, every link on the MCP and platform origins, popups, WebSocket frames, lazy-loaded and SVG images,
  overflow inside an element, and axe's best-practice rules and incomplete results.
- No retries, no fixed sleeps, no `waitForTimeout`: every wait ends on a condition. The SDK stage's pacing waits are
  computed from the request budget; the SDKs' own resends of a refused request are caught by the lane check.
- Step-ups: ts-auth issues at most 10 per user in 15 minutes (`StepUpProofs.cs:60-62,94`); a run spends 1 for the
  desktop owner (account deletion), 8 for the phone-and-flows owner (the access token and 2 API-key rotations in `interactions`, 4 in
  `hosted`, account deletion), 1 for the paid owner (account deletion) and none for any other user; API-key creation and revocation need none.
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


## SDK placement runners

With the two-region stack running, `bash env/local.sh exec npx playwright test placement/sdk.spec.ts --project placement`
runs group B and both placement operations in each SDK. `E2E_REGIONS=2` makes only the `placement.*` needs markers
runnable in the runners and in `support/sdk.ts`; the backup requirement stays separate.
Each runner creates with gru/fra, adds fra to gru, repeats the identical fra-only request for at most 90 seconds,
reads status, proves the retained data and the removed endpoint's 421, and deletes its database through fra.
The two-region group B books an upper bound of 110 requests to cover the bounded repeat loop; the metrics check
still refuses tenant_busy and verifies that every operation ran. The local edge does not implement production's
421 replay, so the runners explicitly use fra after verifying wrong_region.

## Social provider stand-in

The OAuth2 provider at identity.journey.terrascale.tech is served by env/oauth2.mjs behind the same edge. It validates redirect, state, one-use authorization code and PKCE, and supplies verified or unverified email claims through userinfo. Only inside the isolated namespace, 93.184.215.14/32 is assigned to loopback and this hostname resolves to it; every other hostname keeps its existing mapping. This exercises the production public-address SSRF checks without permitting loopback providers or any external route. The browser journey proves email ownership before creating or linking a user, replay refusal, and subsequent subject-based sign-in.
