# Emailed code lifetime and resends

> The resend rule below is superseded: a new code replaces the previous one. See `superseded-codes.md`.

Emailed six-digit codes (sign-in, sign-up verification and password reset) work
for 30 minutes (`Passcodes.Lifetime`), once, and die after 5 wrong guesses. The
hosted pages and the emails say "expires in 30 minutes". The redemption budget
per normalized email (`HostedAuth.CodeWindow`) is 5 attempts per 30 minutes, so
one budget window covers a code's whole life.

A new code of the same purpose keeps the live code it replaces redeemable until
that code's own expiry. Only the one replaced code is kept: a third request drops
the first. Both codes share one attempt count and one redemption, so redeeming
either consumes both. An email that arrives after the user asked for another
code still works. A sign-up resend keeps its account id, which binds the code
digest. The earlier code is redeemed with the latest pending token, the one on
the newest page. A code issued after an authentication-state change or through
issuance recovery keeps nothing from before.

Storage: passcode documents gain `previousCodeHash` and `previousExpiresAt`.
They are additive JSON fields, and pruning clears `previousCodeHash` with the
rest of the code material. Older documents without the fields keep only their
current code.

The other budgets are unchanged: the 60-second mail cooldown per email, 5 codes
issued per email per UTC hour, and 10 mail starts per IP per 15 minutes.
