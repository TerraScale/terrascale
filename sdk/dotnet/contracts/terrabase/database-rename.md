# Database rename contract source

Producer: TerraScale/terrascale `fix/ts-1050`, revision `25071c937f1a2c9254e24e6be8a753910aac45f7`.
Read `docs/terrabase/database-rename.md`, `UpdateManagedDatabaseEndpoint.cs` and
`ManagedDatabaseBody.cs` in `src/TerraBase/Features/Management`.

The name-only PATCH returns a managed database with optional nullable
`previousName` and `conflictOf`. Other fields and error behavior are documented
in `docs/database-rename.md`. The frozen older producer export is unchanged.
HTTP fixtures prove transport parsing, not live availability or convergence.
