# Live signup email delivery check

The production journey checks signup email delivery; there is no separate check. Its `setup` test creates a fresh
mail.tm inbox, submits the hosted sign-up form of `https://auth.terrascale.tech/ui/terrascale/sign-up` once, waits at
most 120 seconds for the message, and checks the sender (`no-reply@terrascale.tech`), the subject and the 6-digit code
on its own line before it verifies the account with that code (`e2e/journey/setup.spec.ts`, `e2e/support/inbox.ts`).
Provider acceptance alone cannot pass it, and it does not tell which provider sent the message.

`.github/workflows/production-journey.yml` runs it after every release and daily at 13:23 UTC; it is not a merge or
release gate. Each run sends three authentication emails (the two owners and the invitation's second user, each to its
own inbox). A failure opens or comments on the `Production journey failure` issue with the run link and the failing
step, and the next green run closes it. When mail.tm fails before the first request to production, the run says
"inbox provider" and production was not contacted.

The inboxes stay on mail.tm, so nobody can register an address again while an account a run leaves behind still uses
it; the journey's last steps delete the owners' accounts. No production credential or repository secret is needed; the
inbox addresses and credentials are generated per run and redacted from everything that uploads. `e2e/AGENTS.md` ("The production target") describes the run. It replaced the daily
`auth-mail-delivery.yml` workflow and `scripts/auth/signup-mail-check.py`, which never verified an account.
