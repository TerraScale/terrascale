# Browser probes and empty file buckets

The public client listener allows cross-origin anonymous `GET` requests to
`/healthz`, `/readyz` and `/backupz`. The policy accepts the region-pin header,
exposes region/request-ID/retry headers, and does not allow credentials. Probe
bodies retain only public status, version and region. Internal listeners and
protected management routes do not gain browser access.

Deleting an empty file bucket must work whether its empty file collection is
registered or absent. System-catalog collection removal does not read client
unique-constraint declarations: the system catalog has no `_unique` collection.
Nonempty buckets continue to refuse deletion until file cleanup finishes.
Client database uniqueness behavior and storage format are unchanged.

Upload-signing identity remains independent of backup credentials. This fix does
not provision an object-store identity, change the storage provider or claim
that production file transfers have been requalified.
