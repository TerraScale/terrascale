# Hosted email-code resend cooldown

Sign-in, sign-up and password-reset mail starts share one persisted 60-second
cooldown per normalized email within a tenant, including unknown addresses.
An immediate resend or a switch to another mail-start route returns HTTP 429
with `Retry-After` and a non-cacheable hosted error page. It neither replaces
the current code nor spends another hourly email allowance. The existing code
can still be verified during the cooldown. A refused sign-in resend keeps the
code-entry form, email and safe continuation, and says that no new code was
sent. Entering the original code does not require another mail request.

The cooldown measures elapsed time from the last admitted request; crossing
a clock-minute boundary does not clear it. Replicas share its stored deadline.
After the cooldown, a new code replaces the previous code. Email-code sign-in
continues to return the same generic response for known and unknown accounts.

Existing admission budgets still apply: 30 calls per IP per minute, 10 mail
calls per IP per 15 minutes, and five mail starts per normalized email per UTC
clock hour. Refused cooldown requests still spend the IP budgets. IPv6 grouping
and tenant isolation are unchanged. A cooldown does not guarantee delivery.

Fast integration coverage in `HostedSignInTests` exercises every initial mail
route against all three subsequent routes, normalized known/unknown addresses,
another replica, the exact elapsed-time boundary, unchanged pending codes and
no extra captured mail. `AuthThrottleTests` retains hourly-budget coverage.
