# Address databases by immutable ULID

Public project database routes accept either the database's canonical uppercase
ULID or its existing validated name. Both resolve to the same database identity,
placement and data. PostgreSQL database selectors and the binary protocol also
accept the ULID. Project and organization authorization remain unchanged.

A signed `database` restriction may identify that database by ULID. Authorization
resolves the request and restriction within the owning project, compares their
database IDs, and pins admitted restricted requests before placement checks.
Tokens restricted to another database receive 404 without its placement details.
Existing name restrictions continue to work during this staged rollout.

This prerequisite does not permit renaming: PATCH bodies containing `name` still
return 400. No catalog name mutation, name-conflict reconciliation or storage
format change is included.

DECISION: deploy additive ULID addressing before switching auth's stored key
restrictions to ULIDs. Reason: the currently serving auth issuer still signs
name restrictions; rejecting those before normalization would interrupt keys.
After live TerraBase ID support is confirmed, deploy the separate auth change
that resolves approved names once and stores immutable IDs. Rename and strict
rejection of name claims remain held until that conversion and non-destructive
name-conflict recovery are proven. This is a release sequence, with no feature
flag or runtime switch.

DECISION: name mutations remain leaderless. Concurrent regional requests may
both accept; convergence keeps the smallest ULID on the contested name and
preserves the other database under its previous or deterministic conflict name,
with an event. Consensus admission is deferred to TS-1085.
