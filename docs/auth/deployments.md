# Deployments

Outside Development ts-auth runs as exactly one of two deployments. `TS_AUTH_PUBLIC_ORIGIN` names it, and `TS_REGION`
(set by ts-orch) must be that deployment's authority region, which holds its durable single-use proofs, attempt
budgets and email admission. If the origin is anything else, or the region is wrong or missing, ts-auth exits with
code 2.

| Deployment | `TS_AUTH_PUBLIC_ORIGIN` | Authority (`TS_REGION`) | Dashboard origin | Passkey relying party | Email |
| --- | --- | --- | --- | --- | --- |
| production | `https://auth.terrascale.tech` (also when unset) | `gru` | `https://dash.terrascale.tech` | `terrascale.tech` | required |
| staging | `https://stg.auth.terrascale.tech` | `stg` | `https://stg.dash.terrascale.tech` | `terrascale.tech` | none |

`TS_AUTH_ENVIRONMENT=Development` accepts any origin. There, the authority is `TS_REGION`, or `gru` when it is unset.

## Email

Production requires `TS_AUTH_SMTP_HOST`, `TS_AUTH_SMTP_USERNAME`, `TS_AUTH_SMTP_PASSWORD`, `CLOUDFLARE_ACCOUNT_ID` and
`CLOUDFLARE_EMAIL_TOKEN`. Staging sends no email and refuses to start if any of them is set.

In staging every flow that sends mail answers as it does in production, with the same status, body and
anti-enumeration behavior. This covers sign-up and verification codes, sign-in passcodes, password reset,
invitations, and alert, allowance and support mail. Each code or link is still stored, expires and is single-use, but
nothing is delivered. Admitted deliveries report `Retryable`, and `ts_auth_emails_total{outcome="not_configured"}` counts
them. So an email-verified sign-up cannot be completed in staging. Staging access goes through flows that need no
mail: the dashboard OpenID Connect client, service clients and agent identities.

TLS, audit signing, token validation, storage, revocation and durable single-use proofs are enforced in both
deployments.

## Tokens

The issuer is `<public origin>/terrascale`. Access tokens always carry `https://api.terrascale.tech` plus every
`TS_AUTH_ACCESS_TOKEN_AUDIENCES` value. The staging spec sets `TS_AUTH_ACCESS_TOKEN_AUDIENCES=https://stg.api.terrascale.tech`.
That matches staging TerraBase's `TERRABASE_AUTH_ISSUER=https://stg.auth.terrascale.tech/terrascale` and
`TERRABASE_AUTH_AUDIENCE=https://stg.api.terrascale.tech`.
