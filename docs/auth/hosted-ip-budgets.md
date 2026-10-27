# Hosted sign-in IP budgets

The hosted sign-in, sign-up, password-reset and device pages charge each end user's IP address (an IPv6 address per
/64, plus its /48 at four times the budget) before any account work. Account guessing is stopped by the per-email
password and code budgets, which are unchanged.

## Client address under ts-orch

ts-orch serves every node through its edge on loopback, and the edge replaces `X-Forwarded-For` with the client's socket
address. When `TS_NODE` is set (ts-orch sets it on every node), ts-auth trusts `127.0.0.1` and `::1` as proxies in
addition to any `TS_AUTH_TRUSTED_PROXIES`. Before this change, production trusted no proxy, so every user on the
internet shared the edge's loopback address and one IP budget: about ten mail-sending requests per 15 minutes locked
sign-up and code sign-in for everyone (QA round 4, rows dashui:15 and dashui:35). Without `TS_NODE`, only the
configured proxies are trusted, as before.

## Token buckets

The IP budgets are token buckets (GCRA) stored in `rate_limits`, one component per region and summed on read like the
fixed windows:

| Budget | Burst | Refill | Purpose |
|---|---|---|---|
| Every hosted call | 120 | 1 per 2 s (30/min) | `hosted-ip` |
| Mail-sending calls (code sign-in, sign-up, reset) | 30 | 1 per minute (60/h) | `hosted-ip-mail` |

The burst covers an office behind one NAT. Sustained rates match or exceed the old fixed windows (30 calls per minute,
10 mails per 15 minutes). A refused request writes nothing, so retrying while limited never extends the wait.
`Retry-After` is an upper bound on the time until one more request is admitted. A full bucket drains in four minutes
(calls) or thirty minutes (mail), never a fixed ten-minute lock.

The per-email budgets stay fixed windows. They still send one lockout notice per stored deadline: 10 password attempts
per 15 minutes, 5 code redemptions per 30 minutes, and 5 code issues and a 60-second cooldown per address.

## Signed-in visitors

`GET /ui/{slug}/sign-in` and `GET /ui/{slug}/sign-up` with an authorization `next` and a live session for the tenant
redirect to that authorization request instead of showing the form again. An application that lost its own session,
such as the dashboard's `/onboarding` page, then gets a code without another sign-in. The authorization endpoint still
enforces `max_age` and `prompt` and refuses rather than redirecting back, so this cannot loop.
