# Internal billing (TS-621)

The `terrascale` organization uses the hidden **TerraScale Internal** plan.
Usage comes from TerraBase's usage API
(`src/TerraScale.Dashboard/Features/Usage/UsageBackend.cs`
calls the canonical project usage route through ts-dotnet); the
dashboard owns the plan, estimate and report-only cap. The cap never
suspends projects, databases or API keys.
Internal billing cannot enter Stripe checkout or accept Stripe projections.

## Configure the existing organization

Run the command with the dashboard's configured environment credentials,
delivered through ts-orch from OCI Vault using instance principal in production. It needs an existing platform
admin who is a member of the target organization and the exact organization ID.
The command signs in with ts-auth's device authorization grant (RFC 8628, client
`ts-cli`): it prints a verification link on `https://auth.terrascale.tech` and a
user code, and the admin named by `--admin-email` approves it on ts-auth's hosted
page. The command keeps that grant only in memory and revokes it when it ends. Configuration requires the active projects
`ts-dashboard`, `ts-auth`, `ts-router`, `ts-status`, `ts-orchestrator`, `ku` and
`ts-reviewer`. It creates no identities, projects, keys or service data.

Supply an existing paid base plan, a positive cap and explicit estimate rates
(zero is permitted for either rate). Choose the amounts with the owner;
there are no default internal prices. For example, after setting those values:

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- configure-internal-billing \
  --org "$INTERNAL_ORG_ID" --admin-email "$PLATFORM_ADMIN_EMAIL" \
  --base-plan "$INTERNAL_BASE_PLAN" --spend-cap-cents "$INTERNAL_CAP_CENTS" \
  --write-cents-per-million "$INTERNAL_WRITE_RATE_CENTS" \
  --storage-cents-per-gb-month "$INTERNAL_STORAGE_RATE_CENTS"
```

The command checks the organization is active and named exactly `terrascale`,
and refuses existing Stripe or checkout state. Repeating it updates the same
hidden plan and organization. Plans, policy and notifications persist in the
dashboard's TerraBase database. A persisted singleton document
(`InternalBillingService.SingletonId`) binds one internal organization, and a
persisted payment reservation fences it from Stripe checkout, even when
operators run concurrent commands across hosts. Rejected changes leave the
active policy unchanged.
After an uncertain commit, check the stored plan and cap before retrying.
Native dashboard sign-in stays unchanged.

## Read the estimate and evaluate the cap

Open **Usage** or **Organization → Billing** in a signed-in ts-auth browser
session. The estimate is the monthly base fee plus writes above the plan's
allowance and logical storage above its allowance. Each excess dimension is
rounded up to a cent. Reads are metered and free. Storage uses the current
logical byte count as a full-month estimate: upstream provides no byte-seconds.
The storage rate and allowance use decimal GB (1,000,000,000 bytes), the unit
the published catalog prices.
These best-effort counters are for dogfooding, not settlement-grade invoices.

The UTC month starts at midnight on the first day. The dashboard sums every
project's windowed usage, including projects added later. Project snapshots
can have different endpoints; the estimate retains the latest producer-reported
UTC day boundary within that month, at most the rounded end of the current UTC
day. This boundary includes the producer's partial current-day bucket, not an
observation timestamp. If any project is
missing, unavailable or lacks a windowed period, the bill and cap evaluation
are unavailable; partial usage never becomes a zero bill. `ts-status` keeps
its approved D1 storage exception; D1 activity is not TerraBase usage.

For an operator report, run the report command. It uses the same browser
approval as configuration:

```sh
nice -n 19 dotnet run --project src/TerraScale.Dashboard -- report-internal-billing \
  --org "$INTERNAL_ORG_ID" --admin-email "$PLATFORM_ADMIN_EMAIL"
```

The command writes JSON and returns an error for incomplete metering. At or
above the organization cap, it records one warning in the owner's dashboard
inbox per UTC month. Repeated reports do not duplicate that warning. Page
views show the warning without changing state. Spending settings can update
the cap; internal excess usage always remains enabled.

## Rollout dependencies

This change supplies dashboard policy and reporting. TS-598/TS-599/TS-612 must
provide the seven real service projects, ts-auth ownership/key integration
and application-data provisioning in TerraBase (or greenfield re-bootstrap).
Configuration deliberately fails until all seven projects exist.

TS-581's deployed TerraBase must expose windowed project usage to ts-auth
member tokens. Older lifetime-only servers cannot produce a monthly bill.
The reporting command needs an interactive browser approval, not a service
API key. An unattended daily job needs a service credential or an upstream
service-token metering contract before it can be installed. This PR
does not deploy that job or claim the service cutovers are complete.
