# Local sealing preparation

Replica preparation loads the database's durable custody key and the organization's
monthly write allowance before opening the regional copy. Preparing allowance
checks does not consume units or override a cap. Every SQL operation still checks
its allowance before execution. This removes first-insert custody and usage
fan-out from the writer's critical section.

Local transaction sealing uses the cached custody key only while its ULID timestamp
is within the configured rotation period. After selection is a minute old, sealing
requests a background refresh without waiting for it. A deduplicated queue holds at
most 64 scopes and eight workers refresh selections. Full queues defer refresh
until another request; failed refreshes retain only a still-unexpired key. A miss
or expired key waits for authoritative selection and fails if custody is unavailable.

Cold selection still checks permanent deletion authority, persists wrapped keys
before use, and rejects selection racing with revocation. The existing synchronous
peer revocation evicts both selection and decode caches before database deletion
can succeed. Background refresh does not extend key validity or the deletion
acknowledgement bound. Backups continue to use authoritative current selection.

`terrabase.keys.refresh` records background selection duration with bounded
`result=success|failed` labels. SQL `Server-Timing` separates `local_allowance`
and `local_native` within `local_execute`, alongside existing `local_seal`, HLC,
publish, partner-hold, and local-commit spans. Nested spans must not be added to
their parent durations.
