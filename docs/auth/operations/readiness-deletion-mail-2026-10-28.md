# Readiness tolerance, deletion cleanup backoff, mail-start order, sign-up in one write

## Readiness tolerates fewer than three failed passes

Every replica checks the same TerraBase. One slow probe write used to make every replica unready at once, which took
ts-auth out of rotation. A replica that has been ready now stays ready through up to two consecutive failed passes and
turns not ready on the third, about 15 s at the 5 s interval. Before its first success, and for failures an operator
must fix (missing signing keys), it is not ready at once. Every failure is still logged.

## Deletion cleanup backs off and is never silent

A failing logical-cleanup job waits 1 minute after its first failure, doubling per attempt up to 30 minutes, with
±20 % jitter. A job that made progress (`batch_remaining`, `records_remaining`) retries after 1 minute.

Each blocked attempt (handoff required, scope mismatch, storage) logs a warning with its failure code, attempt count,
next retry and due date. A malformed job, one whose key does not match its fields, used to be skipped silently while
it stayed at the head of the queue; it is now logged as an error for an operator to repair or remove.
`ts_auth.deletion_cleanup` counts attempts by outcome.

## Mail starts reserve delivery before charging limits

Sign-in, sign-up and reset starts reserve a delivery slot first. A full queue answers 503 without spending the
caller's IP budget, email cooldown or hourly email budget, so a retry after the 503 is not answered with a 429.

Per-IP mail budget, reviewed against shared NAT and office IPs: since #780 each IP may burst 30 mail starts and regains
one per minute (60 an hour); an IPv6 /48 gets four times that. Per-email limits (60 s cooldown, hourly cap) apply on
top. All mail shares one 1000-a-day provider budget, so a looser per-IP limit would let a single IP exhaust everyone's
mail. The budget is unchanged.

## Sign-up verification: one write fewer

The sign-up code is consumed and the account (email claim and user record) is created in one transaction. If the
address got an account in the meantime, nothing is consumed and the redemption fails as before. The session is still
written separately.
