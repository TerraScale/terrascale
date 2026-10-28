# Database Settings rename

Settings requires organization management access, a ready customer database and
exact current-name confirmation. It sends a name-only PATCH through ts-dotnet,
using the database ULID and home region. Producer errors retain the form draft;
uncertain outcomes ask the customer to reload before retrying.

The database ID, data, placement, backups and keys stay. Name-based connection
strings must change. There is no alias after convergence. Cross-region concurrent
claims can both succeed; the smallest ULID keeps the name and the other database
retains its data under a conflict name. Producer-reported previousName/conflictOf
show a banner; catalog sync refreshes and clears it, including same-label updates.

API-key create sends the database ULID to ts-auth. Returned restrictions match
by database ID and show the current display name, or “Deleted database” when
that database is absent locally. Legacy name restrictions are marked unusable.
Key creation and rotation keep their hosted step-up confirmation; rename uses
management authorization and current-name confirmation without step-up.

The dashboard uses main’s TerraScale.DotNet 0.0.20 package. HTTP fake journeys
verify the consumer contract; they do not qualify live convergence.
