# Database name conflict metadata

Producer source: TerraScale/terrascale commit `72f745af1` (merged PR689), `src/TerraBase/Features/Management/ManagedDatabaseBody.cs` and `EngineManagementService.Convergence.cs`.

The managed database response includes nullable `previousName` and `conflictOf`. When a cross-region name claim loses, `name` is the retained database's current conflict label, `previousName` is the contested label, and `conflictOf` is the immutable id of its winner. The database retains its id and data. Normal records may omit these fields or return null.

Names are unique per project after convergence; concurrent cross-region claims resolve to one winner. Metadata read by immutable id reports the current local observation. These DTO/HTTP fixture checks establish transport parsing, not global convergence or live producer qualification. The rename operation remains held until its owning API is available.
