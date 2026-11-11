# Journey suite (`e2e/`)

Playwright Test (TypeScript) that drives the real services built from the commit under test: TerraBase, ts-auth
and the dashboard behind a Caddy edge that serves the production host names, a per-run CA, Mailpit as the SMTP
server and a Cloudflare send-API sink that must stay empty. CI runs it on every pull request through
`.github/workflows/journey.yml`.

## Run it on your machine (no root)

```sh
bash e2e/env/local.sh        # build, boot the stack in a rootless user+network namespace, run the suite, stop
```

Needs the .NET SDK of `global.json`, Node 24, Go, Rust, Python 3 and `certutil` (Ubuntu `libnss3-tools`; NixOS
`nix shell nixpkgs#nss.tools`). The namespace has no internet, so nothing can reach production. To iterate,
`bash e2e/env/local.sh start`, then `bash e2e/env/local.sh exec npx playwright test --project setup --project pages-desktop`
from `e2e/`, then `bash e2e/env/local.sh stop`. The script also builds the five SDK runners (`e2e/sdk/build.sh`).

## Stages

`setup` signs up, reads the code from Mailpit, onboards on Free, creates a database and an API key. Then
`pages-mobile`, `hosted`, `interactions` and `invitation` run in parallel. `finish` (the teardown of `setup`) runs
SDK group A, deletes the database through the UI, runs SDK group B and deletes the account. `desktop-setup` makes a
second user for `pages-desktop`, and its teardown `desktop-close` only deletes that account. CI runs the two users in
two shards; a teardown whose setup failed fails once with "setup did not finish" and runs nothing else.

## Add a page or an operation

- A dashboard page state: one entry in `pages/inventory.ts` (URL, the link pattern it covers, data checks). Every
  audited page fails on a link that matches no entry, so a new page fails the suite until it has one.
- An SDK operation: one entry in `sdk/operations.json` (from `sdk/SQL-API.md`) and its step in each runner under
  `sdk/<sdk>/`. An operation the PR environment cannot run carries a `needs` reason.

## Rules

- Real services only: never fake our own code; only third parties (SMTP, Cloudflare, Stripe) are stood in for.
- No allow-list: every page fails on console errors or warnings, page errors, failed requests, HTTP >= 400, broken
  images, horizontal overflow, axe WCAG 2.2 AA violations and uncovered links. A page whose purpose is an error
  expects its status in its own test (`audit.expectStatus`).
- No retries, no fixed sleeps, no `waitForTimeout`: every wait ends on a condition.
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
