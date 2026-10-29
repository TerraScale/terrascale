# Migration and completion JSON

Migration new returns `{path}` after creating the file. Status returns an array
of `{name,state}` entries: pending, applied, changed or applied_missing_file.
Migration up returns `{database,applied:[<migration names>]}` after success;
`applied:[]` means no pending migration was committed by this invocation.
Each migration commits separately. A later failure can leave earlier migrations
applied; inspect status before retrying. No rollback of the entire run is implied.

Completion returns `{shell,script}`: script is the generated code asset for that
shell. Ordinary completion output still writes the script directly for redirection.
