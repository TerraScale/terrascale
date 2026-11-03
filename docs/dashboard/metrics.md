# Dashboard metric definitions

Dashboard stats show a label and a value only. Each area lists the exact meaning of its stats and the
behavior that page copy no longer spells out.

## Accounts, organizations and support

### Profile stats

- **Scope roles**: roles held in the current scope — 1 for the active organization role plus 1 when a
  project is selected and you hold a role in it.
- **Listed capabilities**: common resource capabilities granted in the current organization and project
  scope. Billing, platform-admin and other routes may apply separate checks.
- **Sessions**: active local dashboard sessions for your account; "Not loaded" when the session list is
  unavailable.

### Organization overview

- **Projects**: projects in the active organization. The current project and database counts appear in
  the Projects card.
- **Members**: people with organization access. Pending invitations appear on the Invitations tab.
- **Plan**: the organization's plan name; "None" when billing is required, "Not reported" when the plan
  is unknown. Price and billing status are on the Billing tab.
- **Created**: the organization record's creation date (UTC).

### Behavior no longer described on the pages

- Personal access tokens act as you through the TerraScale MCP server with your dashboard permissions;
  creating one confirms your sign-in with TerraScale first.
- Sign out everywhere ends every sign-in of your account, including the current one; API keys keep working.
- Email-code sign-in stays available after unlinking a provider. Email-change codes are six digits, valid for
  ten minutes, void after five wrong attempts and replaced by a new request; the cancel link goes to the
  current address and signs you out everywhere.
- The language preference is saved with the account; the dashboard currently renders in English.
- Organization invitations expire after seven days; owners and admins can refresh them. Invitation links
  must be opened by the invited email's account.
- Required two-factor blocks members without it from opening the organization until they set it up.
- Single sign-on (OIDC): once the email domain is verified by DNS record, everyone at that domain signs in
  through the identity provider and joins with the default role on first sign-in. Email code and password
  sign-in are refused for that domain except for organization owners. The client secret is write-only;
  leaving it blank keeps the current secret.
- The audit view shows only events inside the plan's audit retention window; exports use the same window.
- Project regions: the primary region is the default home for new databases. Placement is configured per
  database; writable databases need at least two regions; changing project regions does not move
  existing databases. Hosted authentication runs only in GRU (São Paulo).
- Project grants take a canonical user ULID of an existing organization member.
- Support response targets are first-response targets, Monday to Friday 09:00–18:00 CET, not an SLA.
  A request ID (`X-Request-ID`) grants no access to diagnostics.

## Databases, backups and imports

### Databases list (`/databases`)

| Stat | Definition |
| --- | --- |
| Databases | Customer databases in the current project. |
| Ready | Databases whose status is `ready`. |
| Provisioning | Databases still being created (`accepted`, `pending` or `provisioning`). |
| Failed | Databases whose provisioning did not finish. Shown only when non-zero. |
| Regions | Distinct serving regions across the project's databases. |
| Documents | Live documents across every collection of every database in the project. `Unknown` when TerraBase did not report counts; no zero is inferred. |
| Storage | Bytes of live documents across every collection of every database in the project, as reported by TerraBase. `Unknown` when counts were not reported. |

### Database overview (`/databases/{id}`)

| Stat | Definition |
| --- | --- |
| Collections | Collections in this database. `—` when TerraBase did not report contents. |
| Documents | Live documents across all collections of this database. |
| Storage | Bytes of live documents across all collections of this database. |
| Restore points | Available backup snapshots of this database. `—` when the backup provider did not answer. |
| Created | Database creation day; the tooltip shows the full UTC date and time. |

### Replication (`/databases/{id}/replication`)

All values cover the selected range and come from TerraBase's `replication_bytes` meter, per database and region.

| Stat | Definition |
| --- | --- |
| Replicated ops | Operations replicated across the database's regions. |
| Replication bandwidth | Wire bytes, counted once per directed region pair. |
| Replication sent | Bytes sent by source regions. Metered separately from client bandwidth. |
| Replication received | Bytes received at destination regions. |
| Worst lag | Current maximum replication lag across regions. |

The topology map labels each region with its current lag reported by TerraBase.

### Import job (`/imports/{database}`)

| Stat | Definition |
| --- | --- |
| Status | Current import state. |
| Source | Export format of the uploaded file. |
| Size | Size of the uploaded export file. |
| Destination | The new database the import writes to. |
| Created | When the import was created, in UTC. |

In the dry-run preview, **Tables** counts included tables out of all source tables, **Rows** counts rows in included
tables, and **Blocking issues** and **Warnings** count the preview's issues by severity.

## Insights: overview, analytics, usage, billing, logs, notifications and API keys

### Analytics and overview

TerraBase measures every analytics value. Time buckets follow the selected range automatically.
Counters flush every 10 seconds, so the newest requests can take that long to appear.

| Label | Definition |
| --- | --- |
| Requests | Requests in the selected period. Includes the dashboard's own reads while you browse a database (for example document counts); these are not read operations and are not billed. |
| Errors | Failed requests in the selected period. |
| Worst p95 latency | Maximum p95 of any database-region time bucket in the selected period. Percentiles are approximate histogram estimates in milliseconds. |
| Storage on disk | Latest sampled on-disk size per database region, summed across regions. Includes engine files; Usage shows logical document bytes. Shown only when sampled. |
| Operations | Operations in the selected period. Read operations match Usage's read counts; committed put, patch and delete operations are writes. Billing uses separate 4 KiB operation units, so one operation can consume several units. |
| Client bandwidth | Server egress plus client ingress. |
| Replication sent | Replication bytes observed at sending regions (TerraBase `replication_bytes` meter, separate from client bandwidth). |
| Replication received | Replication bytes observed at receiving regions. |
| Replication bandwidth | Wire bytes, counted once per directed region pair. |
| By region | Requests and worst database p95 per region in the selected period. Selecting a region filters the charts. |

The overview's region table uses ts-status measurements for the regions used by the project or organization.
Replication health counts database regions in each state; worst lag is the current maximum across regions.

### Prometheus export

`GET /v1/projects/{projectId}/metrics` on `api.terrascale.tech`, scraped with OAuth2 client credentials:
an API key with `databases:read`, whose client ID is the key up to its first underscore and whose secret is the whole key.
Every metric carries the `database`, `database_id` and `region` labels; operations add `kind` and latency adds `quantile`.

### Usage

| Label | Definition |
| --- | --- |
| Plan allowance | Published allowances count 4 KiB operation units, storage across selected regions and client egress. Document counters do not measure allowance use. |
| Reads, Writes | Best-effort metering from TerraBase for the selected period; not billing data. Counts since the last save can be lost on a crash. |
| Logical storage | Live document bytes. |
| Report-only spending cap | Internal organizations only. A monthly estimate alert threshold; empty or 0 means no alert. It never charges Stripe, restricts writes or suspends services. |

### Billing

| Label | Definition |
| --- | --- |
| Next invoice | The catalog base fee at renewal, or the recorded open invoice when one is dated later. Usage, credits, discounts and taxes can change the final invoice. Stripe confirms the final charge and billing date. |
| Billing period | Current subscription period, else a recorded invoice period, else one interval before renewal. |
| Outstanding balance | Remaining amount of open invoices in recorded history. |
| Stripe charges | None for Free and internal organizations. |
| Internal bill estimate | Report-only estimate from every project's UTC month-to-date usage. Reads are free; storage uses the current logical size for a full month, not historical byte-seconds. |

### Request logs and log drains

- Request logs show the latest 100 recorded requests within 24 hours, newest first. Restarts and capacity limits can omit requests, and recent requests from other regions may arrive later. Stored records older than 24 hours are pruned on the next database log flush.
- Operations count committed document operations, not billing units.
- Slow queries list query shapes last seen in the last 24 hours, slowest p95 first (maximum latency when p95 is unavailable). Counts are best-effort cumulative and may include earlier queries; the HTTP status filter does not apply. Run a query with `"explain": true` to see its plan and a suggested index.
- The project log drain sends every database's request logs to an OTLP/HTTP JSON logs endpoint (public HTTPS) about every 15 seconds from the moment it is saved. Each check reads the latest 100 requests per database, so heavy traffic or a failing endpoint leaves gaps. The optional header value is stored encrypted and never shown again.

### Alerts

- Rules are checked every minute over the last five complete minutes. A rule fires when the value is above its threshold and resolves when it is at or below it. Up to 20 rules per project.
- Operation units are measured for the whole organization, whatever database a rule names.
- Email goes to organization owners. Webhooks must be public HTTPS; failed deliveries retry each minute, up to 3 attempts.
- The webhook signing secret is base64url bytes. Verify `X-TerraScale-Signature`: `v1=` HMAC-SHA256 of `timestamp.body`.

### API keys

- Allowed IPs accept up to 32 CIDRs, one per line or comma separated; empty allows any IP. A request must match the key's list and the project's list when it has one. Changes apply within 60 seconds.
- Revoking stops a key everywhere within 2 seconds and closes its open connections (including Postgres) within 3 seconds. Rotation issues a new secret shown once; the old secret follows the same bounds when its grace period ends.
