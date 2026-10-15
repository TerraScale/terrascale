# Completed import sources

A completed import reports `source.staged: false`: its source is no longer available as input to the terminal job. `source.receivedBytes` still reports bytes physically present on the node.

The runner persists completion before deleting the staged file. A concurrent GET or a crash between those operations must not make a completed job report an available source. Startup and periodic cleanup remove files left after completion. Failed and cancelled imports keep the existing retention and resume behavior.
