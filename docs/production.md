# Dashboard production

CI (`ci.yml`) must pass for every change; Slow runs independently, reports
failures, and does not block. `images-ghcr.yml` publishes each main commit as
`ghcr.io/terrascale/ts-dashboard:<commit>` and `:main`; ts-orch deploys it to
`https://dash.terrascale.tech`. No dashboard volumes or local data stores exist.

Dashboard-owned persistent state uses the official ts-dotnet Documents SDK and
the pre-created `ts-dashboard` database (`00000000000000000000000004`) under
project `00000000000000000000000005`. Collection initialization never creates
or replaces a database. Conditional dashboard writes retain gru as their
authority; TerraBase owns replication to sjc and fra. ts-auth owns users, the
TerraScale organization, its owner and the two project identities.

Stripe issues `STRIPE_SECRET_KEY` and `STRIPE_WEBHOOK_SECRET`; generated
placeholders cannot replace them. `TERRABASE_PEER_SECRET` must equal the value
on `ts-terrabase` and `ts-auth`; the dashboard does not start without it. See
[identity-descriptor.md](identity-descriptor.md).
