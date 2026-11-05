# Address databases by immutable ULID

Public project database routes resolve the canonical uppercase ULID or validated database name to one immutable database identity. PostgreSQL database selectors accept that ID as well. Resolution preserves project and organization authorization.

A scoped key binds to the immutable database ID. Knowing a name or ID never grants authority. A request outside the restriction receives a non-disclosing refusal without placement details. Guarded lifecycle operations compare the expected ID so a same-name replacement cannot be renamed, forked or deleted accidentally.

See [database rename](database-rename.md), [ID contract](id-contract.md) and [management](management-api.md). Names and their mutation rules follow the current SQL catalog implementation; no compatibility or migration promise is implied.
