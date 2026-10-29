# PostgreSQL table-existence query

The PostgreSQL listener recognizes the complete table-existence query emitted by
`NpgsqlDatabaseCreator.CreateHasTablesCommand` in the Npgsql EF provider 10.0.3.
It returns one PostgreSQL boolean indicating whether this database has any SQL
tables. This supports that introspection operation; it does not establish support
for arbitrary PostgreSQL catalog queries or the complete EF provider.

The listener executes the equivalent query against the SQL session's catalog.
Uncommitted CREATE and DROP, transaction rollback and savepoint rollback have the
same visibility as ordinary SQL. Describe reports the fixed boolean shape
without caching a table-existence result before Execute. Execution retains normal
authorization, admission, metering and read-charge ownership.

Only the complete token sequence is recognized, with normal unquoted identifier
case folding and whitespace handling. Different predicates or string values are
refused rather than silently ignored. Catalog classification uses tokens and the
existing known PostgreSQL relation set; strings mentioning catalog names and
ordinary user tables such as `pg_orders` remain ordinary SQL.

Fast listener integration coverage exercises empty/nonempty schemas, transaction
and savepoint visibility, prepared execution, boolean decoding and altered-query
refusals. Current CI results remain the qualification gate.
