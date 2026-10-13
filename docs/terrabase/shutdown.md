# Graceful shutdown on Fly

TerraBase targets a 20-second shutdown. `src/TerraBase/fly.toml` sends SIGTERM and gives the process 30 seconds before
SIGKILL. These are code constants, not operator settings. CI deploys that file through `scripts/fly/deploy.sh`.

1. `ApplicationStopping` makes readiness fail and rejects new work, including requests on existing connections.
   Kestrel stops its listeners first. SSE and established gRPC streams observe application stopping; any connection
   still alive after two seconds is aborted, including a peer that never finishes its initial hello frame.
2. The host stops hosted services in reverse registration order, within an eight-second cooperative deadline.
   Continuous backup cancels active jobs and joins them before allowing up to four seconds for final shipping,
   bounded additionally by the host deadline. An exhausted final-shipping deadline is logged; unsent changes remain
   in local storage and the next backup snapshot can include them. The remote recovery window may therefore lag.
3. After requests and background workers release their leases, the storage engine closes sessions and checkpoints
   its files. The entry point observes actual native cleanup before completing, then disposes the remaining services.
   The existing storage close grace is ten seconds. Step logs report worker exits, host stop and native storage close.

The budgets require cancellation-aware work and responsive local I/O. The entry point deliberately joins live workers
and native cleanup rather than disposing dependencies under them or reporting a successful close with open sessions.
A stuck native call or a worker that ignores cancellation can still exceed the target; Fly supplies the hard stop at
30 seconds. The shutdown logs identify unfinished stages. This change does not repair F7 or assert that catalog commits
survive SIGKILL; catalog anchoring and crash durability remain R10a's responsibility.

Fast integration coverage keeps four changes streams and two peer streams open during shutdown, checks native session
closure and data-directory release, and restarts on the same files to read an acknowledged document. A stalled final
backup upload is cancelled without consuming the storage-close allowance. Another test opens a peer stream without
sending hello, exercising the connection deadline rather than the streams' cooperative stopping tokens.

Local process measurements and their limits are recorded in `benchmark/bench/2026-10-13-f8-shutdown.md`.
