# Databases for agents fixtures

These synthetic request/response recordings follow the `Databases for agents`
section of `sdk/SQL-API.md` at `923bd4934`. The shared .NET agent fixtures were
absent from main when this SDK implementation began. Credentials are synthetic.
They qualify the SDK's HTTP boundary, not deployed server behavior.

`databases.json` covers database creation, both fork routes, TTL changes,
deletion, scoped key creation, key exchange and refusals. Optional fields are
omitted; an absent database TTL is represented by `expiresAt: null`.
Scoped key responses use ts-auth's `project_id` and `expires_at` fields.
