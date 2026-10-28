# Changing the sign-in email

An account changes its sign-in email in three steps:

1. The backend requests a `change_email`/`ts-auth` step-up proof and sends the browser through hosted
   verification (fresh password or email code, then active MFA), as in `step-up-proof.md`.
2. `POST /v1/terrascale/account/email-change` with `X-TerraScale-Step-Up-Proof` and `{"new_email"}`
   consumes the proof and stores one pending change per account (a new request replaces it). The new
   address receives a six-digit code valid for ten minutes; the current address receives a notice with a
   cancel link valid just as long.
3. `POST /v1/terrascale/account/email-change/confirm` with `{"code"}` from the same account and
   authentication state commits the change.

The commit is one TerraBase transaction: it consumes the pending change, releases the old address's unique
reservation, reserves the new one (another account that claimed it first wins: 409 `email_taken`) and
advances the authentication epoch. Every session, refresh token and sign-in access token of the account is
refused from then on, step-up continuations die, and API-key or OAuth-client secrets whose lineage carries
the old authentication state stop exchanging. The old address no longer signs in to the account.

Wrong codes are charged with a conditional write; five void the change. Expired, replaced, replayed or
other accounts' changes all answer 403 `email_change_denied`. Requests are limited to three per account and
three per target address per hour, plus a platform-wide hourly budget that bounds the mail they send.

The cancel link opens `/ui/terrascale/email-change/cancel`, which only renders a confirmation form. Posting
it from that page cancels the change by signing the account out everywhere (the epoch advance voids the
pending change) and answers the same whether or not the link was still live. Linked social providers keep
signing in by their stable subject. No storage-format bump is needed: `email_changes` is a new auth-owned
collection.
