# Internal billing (TS-621)

The `terrascale` organization uses the hidden **TerraScale Internal** plan.
Usage comes from TerraBase through ts-go; the dashboard owns the plan, estimate
and report-only cap. The cap never suspends projects, databases or API keys.
Internal billing cannot enter Stripe checkout or accept Stripe projections.

## Configure the existing organization

Use the dashboard's TerraBase connection variables from
[the state cutover runbook](terrabase-cutover.md), an existing platform admin,
and the exact organization ID. Configuration requires the active projects
`ts-dashboard`, `ts-auth`, `ts-router`, `ts-status`, `ts-orchestrator`, `ku` and
`ts-reviewer`. It creates no identities, projects, keys or service data.

Supply an existing paid base plan, a positive cap and explicit estimate rates
(zero is permitted for either rate). Choose the amounts with the owner;
there are no default internal prices. For example, after setting those values:

```sh
nice -n 19 bin/dashboard-release configure-internal-billing \
  --org "$INTERNAL_ORG_ID" --admin-email "$PLATFORM_ADMIN_EMAIL" \
  --base-plan "$INTERNAL_BASE_PLAN" --spend-cap-cents "$INTERNAL_CAP_CENTS" \
  --write-cents-per-million "$INTERNAL_WRITE_RATE_CENTS" \
  --storage-cents-per-gib-month "$INTERNAL_STORAGE_RATE_CENTS"
```

The command checks the organization is active and named exactly `terrascale`,
and refuses existing Stripe or checkout state. Repeating it updates the same
hidden plan and organization. Plans, policy and notifications persist in the
dashboard's TerraBase database. The plan, organization assignment and cap commit
in one TerraBase transaction, which enforces a single internal organization even
when operators run concurrent commands across hosts. Rejected changes leave the
active policy unchanged.
After an uncertain commit, check the stored plan and cap before retrying.
Native dashboard sign-in stays unchanged.

## Read the estimate and evaluate the cap

Open **Usage** or **Organization → Billing** in a signed-in ts-auth browser
session. The estimate is the monthly base fee plus writes above the plan's
allowance and logical storage above its allowance. Each excess dimension is
rounded up to a cent. Reads are metered and free. Storage uses the current
logical byte count as a full-month estimate: upstream provides no byte-seconds.
These best-effort counters are for dogfooding, not settlement-grade invoices.

The UTC month starts at midnight on the first day. The dashboard sums every
project's windowed usage, including projects added later. Project snapshots
can have different endpoints; the estimate ends at the latest measured
observation within that month. If any project is
missing, unavailable or lacks a windowed period, the bill and cap evaluation
are unavailable; partial usage never becomes a zero bill. `ts-status` keeps
its approved D1 storage exception; D1 activity is not TerraBase usage.

For an operator report, supply `TS_INTERNAL_BILLING_ACCESS_TOKEN` through the
environment, holding a signed-in ts-auth member's access token with access to
every project. Never put the token in arguments or logs:

```sh
nice -n 19 bin/dashboard-release report-internal-billing \
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
The reporting command currently accepts a member access token, not a service
API key. An unattended daily job needs renewable member credentials or an
upstream service-token metering contract before it can be installed. This PR
does not deploy that job or claim the service cutovers are complete.
