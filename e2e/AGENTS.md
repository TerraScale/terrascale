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
`pages-desktop`, `pages-mobile`, `hosted`, `interactions` and `invitation` run in parallel. `finish` (the teardown
of `setup`) runs SDK group A, deletes the database through the UI, runs SDK group B and deletes the account.

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
- Tests that type a password or code or show a key run without trace and video; `env/scrub.py` removes any artifact
  file that still holds a generated secret. Never print a secret.
