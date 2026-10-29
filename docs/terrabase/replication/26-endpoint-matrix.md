# Endpoint replication matrix

`Fast.Replication.EndpointReplicationTests` defines a three-server integration matrix for gru, sjc and fra, with peer
streams and independent data directories. REST documents, SQL HTTP and Postgres originate writes in each region.
REST documents, REST queries, SQL HTTP and Postgres read the inserted value, its replacement and its deletion.
SQL HTTP and Postgres also originate table creation and a committed transaction containing two inserts and an update.

The shared data model is a SQL table with a ULID `TEXT PRIMARY KEY`. Its rows are documents addressable by REST;
ordinary document collections without a SQL table definition are not relational tables. Integer and composite SQL
primary keys are outside the document API's ULID address contract. REST writes do not enforce SQL column constraints
or affinities.

The concurrent-write case checks that writes from every region converge through every reader and that a later write
wins. These test definitions do not establish production latency or ALTER TABLE support. CI results for the exact
head remain the execution evidence.
