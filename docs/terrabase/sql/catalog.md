# SQLite catalog

Schema tools read SQLite's catalog back: drizzle-kit (`push`, `pull`, `studio`), SQLAlchemy reflection (`MetaData.reflect`,
`inspect()`) and Alembic autogenerate. TerraBase answers it read-only, derived from what the SQL tables already keep
(`src/TerraBase/Features/Sql/SqlCatalog.cs`).

| Relation | Rows |
| --- | --- |
| `sqlite_master`, `sqlite_schema` | `type`, `name`, `tbl_name`, `rootpage` (0), `sql`: each SQL table and its indexes |
| `sqlite_temp_master`, `sqlite_temp_schema` | none: there are no temporary tables |
| `pragma_table_info(t)`, `pragma_table_xinfo(t)` | one per column; `hidden` is 0 |
| `pragma_index_list(t)` | `seq`, `name`, `unique`, `origin` (`c` for CREATE INDEX, `u` for a UNIQUE constraint, `pk`), `partial` (0) |
| `pragma_index_info(i)` | `seqno`, `cid`, `name` |
| `pragma_foreign_key_list(t)` | `id`, `seq`, `table`, `from`, `to`, `on_update`, `on_delete`, `match` (`NONE`) |

- A table's `sql` is its `CREATE TABLE` statement rendered from the syntax tree when it was created (without
  `IF NOT EXISTS`), kept in the table definition with its FOREIGN KEY clauses. FOREIGN KEYs are still not enforced.
- An index's `sql` is rendered from the index: `CREATE [UNIQUE] INDEX name ON t (columns)`. UNIQUE constraints and a
  primary key that is not an INTEGER rowid show as `sqlite_autoindex_<t>_<n>` with no `sql`, as in SQLite.
- `PRAGMA [main.]table_info|table_xinfo|index_list|index_info|foreign_key_list(name)` answer the same rows.
- In a read, a catalog relation becomes a CTE of literal rows at the front of the statement, so the row guard, the
  render-back check and the engine see an ordinary CTE. `pragma_x(e) AS p` in an inner join or a comma join becomes
  `pragma_x AS p … WHERE p.arg = (e)`, SQLite's hidden `arg` column; a pragma function in an outer join is refused.
- `[name]` quotes are read as `"name"` when the name holds no quote character, as backticks are.
- Text values join quotes in with `char(39)`: SqlParserCS renders a doubled quote inside a literal as one quote.
