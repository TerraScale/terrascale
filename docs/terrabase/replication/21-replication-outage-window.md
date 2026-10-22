# 21. Replication outage window

A runtime migration or destructive storage reset needs its own reviewed targets,
backup evidence and recovery procedure. The OCI/ts-orch migration does not establish
that an outage or reset is safe. Preserve data and verify the actual serving fleet
before scheduling an operation. See [OCI hosting](../../oci.md).
