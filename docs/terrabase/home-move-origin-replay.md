# Origin writes across a home move

A local acknowledgement means that the origin's durable push stream and its
required regional quorum hold the transaction. Moving the home does not change
that obligation. The subject's home and epoch describe the placement observed
when the local transaction began; they do not limit which current home can apply
it.

Each home consumes every discovered origin stream, including its own, and checks
the database's current placement before applying a transaction. Consumption by a
region that is not home never sends an applied notice or advances the database's
applied position. Messages remain until a replica pull proves that the home holds
them. On restart, consumers revisit retained messages; `_ts_applied` deduplicates
both replay and concurrent delivery. Application queues preserve origin/database
order across changes to the subject's home.

Promotion decrypts and drains retained pushes addressed to the previous home
before answering success. Continuous consumption also covers transactions that
publish to an old subject after promotion. Replay uses the same envelope decoder
as normal delivery. A stream acknowledgement floor is insufficient proof of
application because it includes messages consumed while this region was not home.

An all-regions lease placement may have an empty explicit replica list. SQL HTTP
and WebSocket read admission and the local commit path honor `AllRegions`, so a
placed region keeps serving local reads after its database's home moves.
