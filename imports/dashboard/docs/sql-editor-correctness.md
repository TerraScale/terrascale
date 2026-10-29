# SQL editor correctness

The SQL editor retains compound operators (`||`, `<<`, `>>`, `==`, `->`, `->>`)
and adjacent lexical fragments such as blob literals and named parameters when
formatting. Formatting does not execute SQL or validate dialect support.

Run, Check input and Save query accept URL-encoded forms containing up to 16 KiB
of decoded UTF-8 SQL. The transport ceiling accounts for percent encoding and the
existing 4 KiB history field. The decoded SQL limit remains authoritative;
oversize SQL is rejected before execution or persistence.

Read-only CTE classification uses the command following the CTE definitions;
column names such as `replace` do not require write access. Known introspection
PRAGMAs (`table_info`, `table_xinfo`, `index_list`, `index_info`,
`foreign_key_list`, `read_uncommitted`, and the `foreign_keys` getter) request
read access. PRAGMA assignments and unknown forms require write access.
TerraBase remains responsible for validating and authorizing the SQL itself.

New saved queries use ULIDs. Saved-query links work with and without a selected
collection. Database-specific queries are listed and restored only in their original database;
project templates remain available across the project. No stored records are rewritten and no migration is introduced.

Regression coverage lives in `SqlEditorCorrectnessJourneyTests` (Fast HTTP
journeys) and `SqlFormattingBrowserJourneyTests` (Slow browser journey).
