# Process crash recovery

Database creation flushes the initialized database and wrapped key, publishes the key and syncs the directory hierarchy, then publishes the database and syncs its directory. Startup removes unfinished creation files and sidecars whose database is absent.

Deletion removes the database and syncs its directory before removing its wrapped key and other sidecars. It syncs those removals and any removed parent directories before completing. Startup completes sidecar cleanup after an interrupted deletion.

A write intent left by an interrupted process triggers a database open, journal recovery and integrity check. An unclean exit alone does not fault the copy. Previously recorded storage faults remain faults; an unreadable database, failed integrity check or mismatched incarnation also requires recovery.

Connections explicitly use WAL journaling. Point-in-time rewind commits with full synchronization. Change capture without a backup tap can discard completed transactions while respecting active shape, replica and rewrite retention floors.

The local crash probe runs the built service with generated credentials and isolated temporary data:

```sh
export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH"
export DOTNET_ROOT="$HOME/.local/share/mise/dotnet-root" DOTNET_PROCESSOR_COUNT=4
python3 e2e/env/engine-crash.py
```

It requires Linux, a C compiler, curl with HTTP/2, and the Release build of TerraBase. It kills a node after an acknowledged write, during key publication and during database deletion, then restarts the real service and checks the retained rows and file pairs. The filesystem probe pauses the service at a completed operating-system call before sending SIGKILL. This verifies process-crash behavior; it does not emulate a power failure.
