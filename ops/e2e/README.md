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
6. Tears everything down in reverse order (database, key, project,
   organization, account), best effort, also when an earlier step failed.
7. Prints a per-step table with ok/FAIL and milliseconds, plus the total time
   to first query. Exits non-zero if any step failed.

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
is optional (the platform picks the home region when omitted). `--keep` skips
teardown to inspect what was created. `--dry-run` prints the planned steps and
makes no network call and does not read the secret file.

## Cost and safety

The script creates and deletes real resources: one account, one organization,
one project, one API key and one small database that lives for under a minute.
Run it only against environments you own; it is not a CI job. Two emails are
sent per run (the sign-up code and the step-up code), and the hosted
mail-start limits (60-second cooldown per address, 5 codes per address per
hour) apply.
