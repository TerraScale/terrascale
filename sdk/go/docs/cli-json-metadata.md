# Metadata and backup JSON

`--json` returns typed SDK objects for usage and backup create/restore/restore-at.
Backup lists return arrays, including `[]`. The backup mutation output reports the
server result, not proof of subsequent restore integrity. All failures retain the
shared safe error/exit contract.
