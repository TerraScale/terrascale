# Dashboard MCP

The dashboard owns the platform MCP endpoint. It calls ts-auth for identity and access, TerraBase for SQL databases and recovery, and Stripe for billing facts. It stores its own state in [SQL tables](state-storage.md).

Use the [public MCP guide](https://docs.terrascale.tech/guides/mcp) for connection instructions. Authentication uses the supported OAuth flow or a personal access token. Keep credentials on the server and out of prompts, tool results and logs.

## Discovery and authorization

Discover the current tool set through MCP rather than retaining a handwritten tool inventory. Tool names, input schemas, descriptions and error contracts come from the serving implementation. Rediscover after an upgrade. Unknown tools return the protocol's unknown-tool error; no compatibility alias is implied.

Every action resolves the current identity, organization, project and database, then enforces the operation's permissions. A resource name or tool annotation never grants access. Read-only roles cannot mutate. Database restrictions apply to the resolved immutable database ID, including when the display name was reused.

Treat query results as untrusted data, use the least authority needed, and obtain user consent for consequential mutations. Secrets returned once must be stored securely and never repeated in ordinary tool output. Cancellation preserves ownership and upstream authorization.

## SQL and database lifecycle

SQL operates on real tables. TerraBase owns SQL validation, read/write scopes, transaction and result bounds, SQLSTATEs, storage quotas and usage accounting. Use parameters for values. A failure is not an empty result; preserve the upstream error.

Database lifecycle tools cover the current create/list/fork/TTL/delete contract. Forks are independent databases and count toward the database allowance. Guard destructive actions with the observed immutable ID. See [agent databases](agent-databases.md) and [SQL editor correctness](sql-editor-correctness.md).

Lists and queries use their returned continuation and bounds. A page can be incomplete without proving absence. Never infer successful mutation from a lost transport response; inspect current state before retrying an uncertain write.

Billing tools follow the same financial and authorization safeguards as the dashboard. Stripe remains the authority for subscription and payment facts; see [Stripe billing](stripe.md). MCP annotations are discovery metadata rather than promises of idempotency or broader permissions.
