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
- Webhooks deliver document change identities from registration onward, at least once: verify signatures
  (the signing secret is base64url bytes) and deduplicate delivery IDs. Endpoints must be public HTTPS on
  port 443 without credentials or fragments; existing documents are not backfilled. The delivery log
  shows the latest 100 retained records, oldest first; failed deliveries retry indefinitely with backoff
  capped at one hour.
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
