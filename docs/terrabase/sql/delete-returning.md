# DELETE RETURNING without a filter

`DELETE FROM t RETURNING *` deletes every row and returns each row's values before
deletion. An empty table returns no rows, with the same column metadata. The
existing RETURNING subset applies: table columns or `*`, with optional column
aliases and table-name qualifiers. Returned row order is unspecified.

HTTP SQL executes the statement within the request's transaction: a later error
rolls back the deletion and every other statement in that request. Hrana v2/v3
supports the statement through execute, stored SQL, describe and sequence.
Describe reports RETURNING columns without deleting rows; explicit transactions
retain the deletion across requests through replay and support commit/rollback.
Writes still require `documents:write`.

The client SQL boundary retains SqlParserCS 0.6.5 and extends its SQLite dialect
through the public statement hook. Its default DELETE parser mistakes an
unfiltered, unaliased RETURNING clause for an implicit table alias. This form now
uses the parser's object-name and select-item grammar to construct the same DELETE
AST used by validation and execution. Both single statements and script splitting
use the dialect. Comments, quoted names, aliases, delimiters and validation retain
their parser semantics; no SQL text replacement is involved.

This corrects accepted SQLite syntax within the [relational SQL contract](relational-sql.md)
without changing API shapes, supported RETURNING expressions, storage format or
requiring a migration.
