# Rename a database

Open a database's Overview, choose Settings, enter a new name, and type
the current name exactly to confirm. Names match `[a-z][a-z0-9_-]{0,62}`, excluding
`con`, `prn`, `aux`, `nul`, `com[0-9]` and `lpt[0-9]`. Input is case-sensitive and
never trimmed. Organization management access and a ready database are required.

After rename converges, the old name stops resolving unless claimed again;
there is no alias or redirect. Update the database name in your application
configuration. The dashboard URL keeps the immutable database ULID. The database's
data and placement are preserved. New restricted API keys send the database ULID
to ts-auth; the dashboard displays the current name without changing that restriction.

DECISION: locally observed duplicate names return `409`; conflicting requests accepted in different regions may both succeed.
Names are unique per project after convergence; concurrent cross-region claims resolve to one winner. The smallest ULID keeps the target name; the loser retains its ULID and data under a deterministic conflict label such as `<name>-conflict-<ulidtail>`.
Reconciliation emits an event, preserves every ID/data/file/backup/key, and aligns name indexes with current labels across regions. Same-database renames are serialized at home.

The ts-dotnet project client PATCHes the database ULID with only `{"name":"new-name"}`; an accepted response does not establish exclusive name ownership across regions.
Catalog sync follows external renames and lost responses. Reload Settings after an uncertain error before retrying.

A database with producer-reported `previousName` and `conflictOf` shows a **Renamed after a name conflict** banner in its database pages. It names the lost and current labels, explains that identity/data/files/backups/keys are preserved, and directs customers to update connection strings or choose another name in Settings. Catalog sync refreshes this metadata even when the label itself is unchanged, and removes the banner when the producer clears it. The banner uses catalog metadata, never a guessed name suffix.
