# Database restrictions are ULIDs only

Follows [database-ulid-addressing.md](database-ulid-addressing.md), which held strict rejection of name claims until
ts-auth stored and emitted immutable ids.

- A token's `database` claim must be the database's canonical ULID. A name claim fails authentication (401). A name
  restriction would reach a later database created under the same name after a delete; an id never resolves again.
- A key whose database was deleted reaches nothing: its id resolves to no database, so every route answers 404
  `unknown_database`, the same as a restriction to another database. ts-auth cannot see deletions, so such keys stay
  listed until revoked.
- A database-restricted token cannot create a database: there is no id to match before the database exists.
- Release order: ts-auth (ULID-only `database` on API keys and the GitHub Actions binding, name-valued keys refused)
  ships first; this change ships after it is live.
