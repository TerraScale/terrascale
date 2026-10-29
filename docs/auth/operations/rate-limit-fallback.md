# Rate limits survive a storage outage

On 2026-10-28 from about 17:43Z to 19:30Z, gru's TerraBase failed the rate limiter's calls. The limiter failed
closed, so every token request and hosted mail start answered 503 and sign-in was down even though nothing else
had failed yet.

`AttemptLimiter` now runs the same budgets on this replica's memory (`LocalAttemptStore`) when its storage call
fails transiently: a timeout, no connection, or a TerraBase 5xx, 429 or 421 answer. Fixed windows, cooldowns and
token buckets keep their maxima and intervals per replica. A replica therefore still refuses brute force, and the
limiter alone never takes sign-in down. Each fallback is counted (`ts_auth.rate_limit_fallback{purpose}`) and logged
as a warning at most once a minute, naming the failure's type.

These still fail closed with `503` and `Retry-After: 2`:

- a rejected storage credential or another non-transient error;
- a damaged stored bucket;
- persistent write contention;
- a full in-memory store (100,000 buckets, idle ones dropped after an hour);
- lockout-notice claims, which decide whether to send mail.

The limiter's 503 on token requests now carries `Retry-After: 2` and logs its cause.

Trade-off: during the outage, each replica counts alone, so with N replicas an attacker gets up to N times one budget.
Buckets in memory are not merged back into TerraBase when it recovers. Production runs one ts-auth replica per
region today.
