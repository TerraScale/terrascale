# Hosted sign-in IP budgets

The hosted sign-in, sign-up, password-reset and device pages charge each end user's IP address (an IPv6 address per
/64, plus its /48 at four times the budget) before any account work. Account guessing is stopped by the per-email
password and code budgets.

## Client address under ts-orch

ts-orch serves every node through its edge on loopback, and the edge replaces `X-Forwarded-For` with the client's socket
address. When `TS_NODE` is set (ts-orch sets it on every node), ts-auth trusts `127.0.0.1` and `::1` as proxies in
addition to any `TS_AUTH_TRUSTED_PROXIES`. Before this change, production trusted no proxy, so every user on the
internet shared the edge's loopback address and one IP budget: about ten mail-sending requests per 15 minutes locked
sign-up and code sign-in for everyone (QA round 4, rows dashui:15 and dashui:35). Without `TS_NODE`, only the
configured proxies are trusted, as before.

## Token buckets

The IP budgets are token buckets (GCRA) stored in `rate_limits`, one component per region and summed on read like the
fixed windows. Each component holds its region's debt and the writer's clock at the write. A reader drains it only by
the time it has itself seen pass since then, so a replica whose clock is behind never inflates the debt; one ahead may
admit up to its skew early, never lock anyone out:

| Budget | Burst | Refill | Purpose |
|---|---|---|---|
| Every hosted call | 120 | 1 per 2 s (30/min) | `hosted-ip` |
| Mail-sending calls (code sign-in, sign-up, reset) | 30 | 1 per minute (60/h) | `hosted-ip-mail` |

The burst covers an office behind one NAT. Sustained rates match or exceed the old fixed windows (30 calls per minute,
10 mails per 15 minutes). A refused request writes nothing, so retrying while limited never extends the wait.
`Retry-After` is an upper bound on the time until one more request is admitted. A full bucket drains in four minutes
(calls) or thirty minutes (mail), never a fixed ten-minute lock.

The per-email budgets stay fixed windows. They send one lockout notice per stored deadline: 10 charged primary password attempts
per 15 minutes, 5 code redemptions per 30 minutes, and 5 code issues and a 60-second cooldown per address. Password
verification reserves one slot before checking the credential and returns only that request's reservation after the
session is issued and accepted, an eligible account receives its second-factor challenge, or its password step-up has
persisted the verified password (approved at once, or handed to the second factor); every other outcome retains the
charge until the window expires. The return uses a conditional write against
the same store, key and window that admitted it, skips a changed window, and subtracts only one charge. Concurrent
verifications count while in flight; a successful sign-in never clears other failures, and an exhausted budget still
refuses correct passwords. Projects using account-lockout protection do not use this password budget.

| Outcome | Password reservation | Second-factor budget |
|---|---|---|
| Wrong password | Charged | Unchanged |
| Right password, then wrong second factor | Returned at the eligible second-factor handoff | Charged by `mfa-proof` |
| Right password, account refused | Charged | Unchanged |
| Right password, signed in | Returned | Charged if a second factor was attempted |
| Cancelled, timed out or failed before primary success | Charged | Charged if a second factor was attempted |
| Return store unavailable | Charged; one warning, successful sign-in continues | Unchanged by the return |

An inbox-verification prerequisite retains the password charge; it has not accepted the guarded step yet. A platform
step-up that verified the password and now waits for the second factor has returned its password slot, exactly as a
sign-in does at that handoff; the second factor is charged by `mfa-proof`. Previously, successful sign-ins and confirmations also consumed the ten
slots, locking out ordinary account use. The shared IP
budgets above still charge every attempt. A refusal's
`Retry-After` is the remaining part of that window in seconds, and the page speaks it in whole minutes above two
minutes. A transient failure (a full delivery queue, storage that does not answer) keeps the visitor on their own
sign-in step with the address they entered and the same forms, so a failed sign-in always has a next action on the page.

## Signed-in visitors

`GET /ui/{slug}/sign-in` and `GET /ui/{slug}/sign-up` with an authorization `next` and a live session for the tenant
redirect to that authorization request instead of showing the form again. An application that lost its own session,
such as the dashboard's `/onboarding` page, then gets a code without another sign-in. The authorization endpoint still
enforces `max_age` and `prompt` and refuses rather than redirecting back, so this cannot loop.
