# Migration diff JSON

`migrate diff --json` returns `{base,branch,changes:[{kind,name,before,after}]}`.
Before/after are `sqlite_schema` entities `{kind,name,table,sql}` for tables,
indexes, views and triggers. Null before means added; null after means removed.
Unchanged schemas return `changes:[]`, not status prose in a string.

The comparison covers the defining SQL text only and does not generate executable
migrations. All command forms have typed JSON output; new command hooks must use
the same emit helpers and preserve the original writer.
