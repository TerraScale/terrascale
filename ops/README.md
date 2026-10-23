# Production operations

Production runs on OCI under `ts-orch`; the current production host is
`ts-gru-1` in `sa-saopaulo-1` (`gru`). Region names `sjc` and `fra` map to
`us-sanjose-1` and `eu-frankfurt-1`; this mapping does not establish active deployments.

Operational ownership and commands live in
[ts-orchestrator](https://github.com/TerraScale/ts-orchestrator). Use its
[CLI documentation](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/cli.md),
[cluster visibility documentation](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/cluster-visibility.md)
and [release contract](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/releases.md).
GitHub-hosted CI builds images in GHCR; the orchestrator owns rollout authority.

Production secrets belong in OCI Vault. Hosts retrieve only their authorized
secrets through instance principals and exact-secret IAM grants. The
[secret lifecycle documentation](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/secrets.md)
describes current delivery limitations; do not infer that automatic creation
and delivery are complete. `secrets/secrets.json` inventories names and consumers,
contains no secret values, and does not prove deployed Vault state.

## Tigris storage ownership

Tigris remains connected to its organization through Fly. This storage
integration is retained independently of OCI compute ownership. Authenticate
with `flyctl auth login`, then use
`fly storage create` / `flyctl storage create` for bucket provisioning.
The installed CLI supports organization-scoped creation without a compute app:

```sh
flyctl storage create --org <tigris-org> --name <bucket-name>
```

Keep the Tigris endpoint `https://fly.storage.tigris.dev`, existing `TIGRIS_*`
configuration and the Tigris credential names in `secrets/secrets.json`.
Production credentials still belong in OCI Vault. The removed PR-preview
scripts provisioned and destroyed buckets specifically for retired compute
previews; their deletion does not retire organization-level Tigris storage.
