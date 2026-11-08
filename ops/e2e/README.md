# ops/e2e customer journey

`journey.py` walks the whole live customer path over the public API, the way the
SDKs and the CLI do, and proves it still works end to end:

1. Signs up a fresh plus-tag identity on the hosted sign-up form and redeems the
   emailed code (read over IMAP; never from argv or env).
2. Approves a CLI device grant and gets the user's access token.
3. Creates an organization, a project and a project API key (completing the
   hosted step-up with a second emailed code).
4. Exchanges the key for a short-lived access token.
5. Creates a database in the home region and runs one SQL REST round trip
   (`CREATE TABLE`, `INSERT`, `SELECT`).
6. Discovers the serving regions the same way the SDKs and the CLI do
   (anonymous `GET /v1/regions`), then for every region creates one small
   database with that region as its explicit `homeRegion` and runs one SQL
   round trip against it. Steps are recorded per region
   (`create_database:gru`, `sql_round_trip:sjc`, …). A `421 wrong_region`
   answer fails the step and its message (which names the home region) is
   printed; no secret is ever shown.
7. Runs the SDK quick starts published on the docs site. For each selected
   language (`js`, `go`, `python`, `dotnet`, `rust`) the sample code block is
   extracted from `docs-site/docs/getting-started/<lang>.md` into a temp dir
   together with the minimal project file the page says to create, the SDK
   version the page pins is installed, and the sample runs with the journey's
   API key, project and database passed through `TERRASCALE_API_KEY`,
   `TERRASCALE_PROJECT_ID`, `TERRASCALE_DATABASE_NAME` (and
   `TERRASCALE_REGION` for the C# quickstart) — never through argv. The temp
   dir is removed afterwards. A language whose toolchain is missing is
   recorded as `skip`.
8. Tears everything down in reverse order (per-region databases, the primary
   database, key, project, organization, account), best effort, also when an
   earlier step failed.
9. Prints a per-step table with ok/FAIL/skip and milliseconds, plus the total
   time to first query. Exits non-zero if any step failed.

Everything created is named with the prefix `e2e-<utc yyyymmddhhmmss>-` so
anything a failed run leaves behind is easy to find and delete by hand.

## Prerequisites

- Python 3 (stdlib only; no packages to install).
- A mailbox you control whose IMAP server delivers mail addressed to
  `anything+e2e-...@yourdomain` (plus-tag aliasing), reachable over IMAPS on
  port 993.
- The IMAP password in a file with mode `0600`, for example
  `~/.config/terrascale/imap.secret`. The script refuses any other mode and
  never prints or logs the password, the codes, the key or any token.

## Run

```
python3 ops/e2e/journey.py \
  --base-url https://api.terrascale.tech \
  --auth-url https://auth.terrascale.tech \
  --email 'you+e2e-{ts}@yourdomain' \
  --imap-host imap.yourdomain \
  --imap-user you@yourdomain \
  --imap-secret-file ~/.config/terrascale/imap.secret \
  --region gru
```

`{ts}` in `--email` is replaced with the run's UTC timestamp, so every run
signs up a distinct identity and the script is safely re-runnable. `--region`
is optional (the platform picks the home region when omitted). `--sdks`
narrows the quick-start languages (comma-separated; default: every language
whose toolchain is on PATH — `node`, `go`, `python3`, `dotnet`, `cargo`).
`--keep` skips teardown to inspect what was created. `--dry-run` prints the
planned steps and makes no network call and does not read the secret file.

## Toolchain expectations for the SDK steps

- `js`: Node 22 or 24; installs the pinned `@terrascale/sdk` from npm.
- `go`: Go 1.25+; `go get`s the pinned `terrascale.tech/sdk` tag.
- `python`: Python 3.10+; builds a venv and installs the SDK from the
  monorepo git ref the page pins (it is not on PyPI).
- `dotnet`: .NET SDK per the repo pin; packs `sdk/dotnet` from this monorepo
  checkout into the temp dir (the package is not on NuGet) and references it
  from a fresh console project.
- `rust`: cargo 1.99+; uses the git dependency the page documents (the crate
  is not on crates.io).

The quick-start samples come from the docs pages at run time; nothing is
copied into this repo. Child process output is printed only on failure, only
the last 20 lines, with the key, tokens and other secret values replaced by
`***`.

## Cost and safety

The script creates and deletes real resources: one account, one organization,
one project, one API key, one small database per serving region plus the
primary database, each living for under a few minutes.
Run it only against environments you own; it is not a CI job. Two emails are
sent per run (the sign-up code and the step-up code), and the hosted
mail-start limits (60-second cooldown per address, 5 codes per address per
hour) apply.
