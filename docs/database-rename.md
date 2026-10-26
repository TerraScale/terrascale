# Rename a database

Open a database's Overview, choose Settings, enter a unique new name, and type
the current name exactly to confirm. Names match `[a-z][a-z0-9_-]{0,62}`, excluding
`con`, `prn`, `aux`, `nul`, `com[0-9]` and `lpt[0-9]`. Input is case-sensitive and
never trimmed. Organization management access and a ready database are required.

Renaming changes connection strings immediately. The old name stops resolving;
there is no alias or redirect. Update the database name in your application
configuration. The dashboard URL keeps the immutable database ULID. The database's
data and placement are preserved. New restricted API keys send the database ULID
to ts-auth; the dashboard displays the current name without changing that restriction.

The ts-dotnet project client PATCHes the database ULID with only `{"name":"new-name"}`; TerraBase validates names and uniqueness.
Catalog sync follows external renames and lost responses. Reload Settings after an uncertain error before retrying.
