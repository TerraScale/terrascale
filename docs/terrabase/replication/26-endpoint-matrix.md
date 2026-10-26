# Endpoint replication matrix

`Fast.Replication.EndpointReplicationTests` starts three real servers (gru, sjc and fra), with peer streams and
independent data directories. Each source endpoint writes in each region; all five endpoints in all three regions
must read the inserted value, its replacement and its deletion. SQL HTTP, Postgres and Hrana also originate table
creation and a committed transaction containing two inserts and an update.

The shared data model is a SQL table with a ULID `TEXT PRIMARY KEY`. Its rows are documents addressable by REST and
Wire; ordinary document collections without a SQL table definition are not relational tables. Integer and composite
SQL primary keys are outside the document API's ULID address contract.

Wire operation `3` adds unconditional deletion: the GET address followed by one durability byte (`0` local,
`1` remote), with no JSON. Success is `200` followed by the same durability and ETag fields as PUT. Authentication,
write scope, ordering, validation, tombstones, metering and remote confirmation use the existing document write path.
Deleting an absent document returns `404`; a body after the durability byte returns `400 invalid_frame`.

Existing version 1 GET and PUT frames are unchanged. Older nodes return `400 unknown_operation` for operation `3`;
clients must surface that refusal during a mixed deployment. No peer protocol or persisted format changes.

These tests do not yet prove ALTER TABLE, deterministic conflict winners, transaction visibility during replication,
rollback or production latency. ALTER TABLE is currently refused by the SQL grammar. Those remain RT6 acceptance work.
