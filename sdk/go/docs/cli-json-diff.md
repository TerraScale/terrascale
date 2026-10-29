# Migration diff JSON

`migrate diff --json` returns `{base,branch,changes:[{kind,name,before,after}]}`.
Before/after are typed entities: collections, table columns with declared types,
or indexes with collection and field paths. Null before means added; null after
means removed. Unchanged schemas return `changes:[]`, not status prose in a string.

The comparison covers only these SDK metadata fields. It does not compare defaults,
constraints, schema validation or TTL, and does not generate executable SQL.
All original command forms now have typed JSON output; new command hooks must use
the same emit helpers and preserve the original writer.
