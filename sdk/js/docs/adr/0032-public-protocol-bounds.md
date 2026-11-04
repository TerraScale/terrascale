# Native public protocol bounds

Status: Accepted from the current producer contract; runtime qualification required.

Request JSON values are bounded to nesting depth 16 and a shared 4,096-value
node budget, and integer quantities use exact bigint/decimal representations.
Request bodies are bounded to 4 MiB and responses to 64 MiB by default. A SQL
request carries 1 to 256 statements. Opaque cursors remain operation-bound.

The producer is authoritative for operation-specific bounds. Identity discovery
has its separate 64 KiB limit. Deadlines
cover credentials, discovery, dispatch and response consumption. Mutations are
never automatically replayed; replay-safe reads retry transient refusals within
their fixed deadline. Invalid media, malformed values, cancellation,
and uncertain outcomes must remain observable in installed consumers.
