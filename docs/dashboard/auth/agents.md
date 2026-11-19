# Project agents

Authentication → Agents lists the current project's AI agent identities at
`/auth/agents`. Filter by owner user ID or open an agent to inspect its scopes,
status, key ID and thumbprint prefixes, creation, last use and expiry. The owner
link opens that user's Authentication profile.

Project viewers can read agents and their policy. Project administrators can
save the allowed scopes, revoke a key or revoke an agent. Writes use the
signed-in administrator's ts-auth token and the dashboard form token. Revocations
use the same confirmation dialog controls as dashboard API keys.

An empty agent policy blocks new agents. Saving a policy does not change existing
agents' scopes. Enter one scope per line; commas remain part of a scope name.
Saving preserves the current owner filter and page cursor.
Revoking a key leaves the agent's other active key usable.
Revoking an agent prevents new token grants; issued tokens expire within ten
minutes. Agents are created through enrollment or the management API; the
dashboard has no creation form.

The dashboard calls ts-auth's project agents and agent-policy APIs and stores no
agent data. The journey interaction `dashboard-agents.spec.ts` covers policy
saves, list and detail data, the owner link, cancellation and both revocations.
