# Dashboard on OCI gru

Production runs under ts-orch on OCI gru. Images come from
`ghcr.io/terrascale/ts-dashboard`, pinned by digest in the serving service spec.
The source contract is `deploy/m1/services/ts-dashboard.json` in ts-orchestrator.
No infrastructure, credentials or billing changes are authorized by this document.

## Runtime contract

- `ASPNETCORE_URLS=http://127.0.0.1:8085` binds the dashboard to loopback.
- `TS_DASHBOARD_TERRABASE_URL=http://127.0.0.1:8081` selects TerraBase's private
  listener. Production requires this explicit origin; only Development defaults
  to loopback. The peer credential is restricted to that origin; redirects fail closed.
- ts-orch's HTTPS edge preserves `Host` and supplies `X-Forwarded-Proto` and
  `X-Forwarded-For`. ASP.NET Core trusts forwarding only from loopback peers.
  Device approval URLs use the resulting public scheme and host.
- The service owns `dash.terrascale.tech`, one replica, port 8085 and readiness
  path `/readyz`. `/readyz` and `/health/ready` answer the same JSON body:
  `{"status":"ok","region":"gru","dependencies":{"ts-auth":"ok","terrabase":"ok"}}`,
  or 503 with `"status":"unavailable"` and the failing dependency marked
  `"unavailable"`. ts-auth is probed at its public `/readyz` (2 s deadline,
  result shared for 5 s); TerraBase state is reread per probe (2 s deadline).
  `region` is `TS_REGION` as ts-orch sets it, `null` when unset. Public health
  bodies never carry the build version or revision.

## Secrets and state

ts-orch reads OCI Vault in gru using instance principal and delivers protected
files named by `<NAME>_FILE`. Required names are `TS_AUTH_DASHBOARD_CLIENT_SECRET`,
`TS_DASHBOARD_SESSION_ENCRYPTION_KEY`, `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`
and `TERRABASE_PEER_SECRET`. Names, validation and ownership are in
[identity-descriptor.md](identity-descriptor.md). CI secrets remain in GitHub
Actions secrets. Never print values or copy them into code or configuration.

Dashboard state remains in its separate TerraBase database through ts-dotnet;
ts-auth owns authorization. Missing credentials or dependencies fail closed.
ts-status remains on Cloudflare Workers + D1 and monitors public HTTP endpoints.

## Read-only release proof

```sh
curl -fsS https://dash.terrascale.tech/readyz
curl -fsS https://dash.terrascale.tech/health/ready
```

Match the commit after `+` in `version` to the reviewed image revision. A ready
response does not qualify sign-in, persisted state or Stripe acceptance;
retain those separate release gates. Workflow changes belong to the orch lane.
