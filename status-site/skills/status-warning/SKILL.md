# TerraScale status incidents, warnings and maintenance

Use this repo skill when the human asks to publish, update or resolve a
TerraScale incident or warning, or to schedule maintenance. Use the admin page,
https://status.terrascale.tech/admin; sign in through ts-auth with owner or
admin membership in the TerraScale organization. Component status itself
is measured and needs no edits.

## Model

- Kind: `incident`, `warning` or `maintenance`. Impact: `none`, `minor`
  (degraded), `major` (partial outage) or `critical` (major outage).
- Incidents move investigating → identified → monitoring → resolved;
  maintenance is scheduled → in progress → completed, and follows its schedule
  unless completed early.
- Every status change is an update with a customer-facing message. Subscribers
  get every update.
- Components: tick the affected ones; none ticked means the whole platform.

The probe coordinator opens incidents on its own when a component stays down
for a minute or degraded for ten, and resolves them after five healthy
minutes. Editing or updating an automatic incident in the admin page takes it
over; the coordinator then leaves it alone. During maintenance covering a
component, no automatic incident opens for it.

## Publish

Ask the human for the title, the message, the impact and the affected
components if they have not given them. Keep the message factual and specific.
On Incidents → Publish, choose the kind, fill in the form and press Publish.
Maintenance needs its start and end, in UTC.

## Update, resolve, edit, delete

Open the incident from Incidents. "Post an update" changes the status (and
optionally the impact); only resolve when the human asks. "Edit" changes the
title, impact, components or a maintenance window. "Delete" is only for
records published by mistake.

Verify every change on https://status.terrascale.tech/incidents/ID. Do not
publish speculative incidents.
