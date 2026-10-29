# Metadata and backup JSON

`--json` returns typed SDK objects for usage, schema get/set/delete, index create,
backup create/restore/restore-at and import status/cancel. Index and backup lists
return arrays, including `[]`. Index drop returns `{name,dropped:true}`.
TTL get returns `{path}`; set/delete add `collection`. An empty path means no TTL.
The backup mutation output reports the server result, not proof of subsequent
restore integrity. All failures retain the shared safe error/exit contract.
