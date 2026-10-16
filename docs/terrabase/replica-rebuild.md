# Replica copy faults and rebuilds

Production recovery uses the fault ledger, coalesced trigger, and certified
source-pinned refill. A fresh receiver must complete keyed bucket coverage
from one intact source before the ledger can publish trust.
A copy awaiting certified refill remains unavailable for ordinary writes and
replication sourcing, with its recovery record and retained side copy intact.
Document writes that reach a refused copy answer `503 write_outcome_unknown`
through the conservative write-error path. A refusal before BEGIN executes
nothing, but a durability-timeout response can follow a committed write;
clients must treat outcome-unknown responses as uncertain. Other endpoints
that propagate the copy refusal directly map it to `503 copy_faulted` without
exposing the internal fault reason.

Reads are not gated while a replacement awaits certification. They can see
the fresh, incomplete copy and return 404 for data still awaiting refill;
write, acknowledgement and replication-source refusal do not imply read refusal.

TerraBase keeps each node's copy-fault and rebuild records in `_tb_rebuild` in
the SYSTEM database. These records use FULL commits with replication capture
suspended. They do not replicate as catalog changes. Whole SYSTEM snapshots
still contain them; recovery discards a different node's refill proof before
using a restored record locally. This is a greenfield storage-format change,
with no migration from earlier formats.

Before a tenant copy takes work, a FULL SYSTEM intent records its incarnation
and process replica identity. Database copying records this intent before
opening its raw source reader. Source I/O or corruption faults that copy before
the copy operation returns; a private target failure leaves source trust intact. After an unclean exit, startup converts uncertain
intents to copy faults before admitting work. This deliberately refuses copies
that were being used even when their contents might have survived: a crash
before the fault record must not silently restore trust. Failure to persist a
fault, or a SYSTEM storage fault, fails the node closed. SYSTEM is never rebuilt
or replaced in process by this recovery path.

A clean shutdown retains ordinary restart recovery. An abrupt process kill
can leave the auth storage copy uncertain, so ts-auth remains unready until
that copy completes certified recovery. Recovery needs an intact placed peer;
without one, the copy remains refused. Fast covers clean restart with the same stored key and crash refusal
without an intact peer, checking the original key-bearing main/WAL files stay
unchanged at the retained recovery path.

A writer rechecks known faults before native COMMIT and before returning its
result. A commit that landed while damage was detected returns no successful
acknowledgement; its durable intent preserves recovery work. Group commit must
also preserve this refusal rather than treating it as harmless cleanup failure.
Known tenant refusals hold that database while healthy databases continue on
the shared peer stream. A node-wide storage fault ends the stream.

Recovery scans durable work at startup. A capacity-one notification wakes it
after a fault record commits; repeated notifications coalesce. One rebuild runs
per host. Refused attempts remain durable and retry on a wake or the fixed retry
interval. There is no in-memory backlog of database rebuild requests. Recovery
uses the process Bulk memory budget for its bounded maintenance pass.

Startup opens SYSTEM and recovers its intents before later hosted services or
listeners start. The asynchronous worker waits for application startup and a
durable wake or retry tick before scanning, so it cannot compete with catalog
startup for storage admission. A blocked or damaged SYSTEM path refuses startup. Empty usage
and query flushes leave the already-open SYSTEM store unchanged.

Refused native sessions deinitialize after their owned operations stop instead
of performing a graceful close that can checkpoint the retained file. Late
readers keep their own sessions until disposal; their refusal belongs to the
old incarnation even after its replacement is published. Independently owned
backup tap readers keep that same refusal through reader replacement and late
disposal, preserving the retained main file and WAL.

The rebuild drains the old copy, preserves its file and log at an
engine-controlled side path, and publishes a fresh incarnation. It refills from
one placed source, pins that source incarnation on every page and final digest,
and verifies keyed coverage of all buckets. Duplicate or reordered page keys,
a refused source, a changed source incarnation, or incomplete stream completion
refuse the attempt. At most eight digest refresh rounds run per source; only
changed buckets are reread, with one admitted page at a time. Receiver memory
refusal ends the pass before another source is tried.

Every repair checks the receiver's persisted incarnation inside the same writer
transaction that applies its states. Invalid persisted identity or keyed stored
state marks the exact local copy damaged; another valid incarnation is a
conditional mismatch. Verified retained states join after refill. A FULL tenant
certificate barrier and its verification read must both finish disposal before
SYSTEM certification makes ordinary writes eligible. Completion removes side
copies only while that persisted barrier and local copy remain valid. Refusal
keeps the record and retained files, and restart resumes the persisted phase.
Ordinary anti-entropy refill supplies no certificate and advances no source ACK
for this recovery path.

The ledger also preserves an uncertain replacement as one additional side
copy. A further uncertain replacement beyond this bound refuses recovery and
keeps every existing file. A restored local SYSTEM certificate without its
matching tenant barrier restores refusal before ordinary writes resume.

Fast integration coverage targets durable faults, coalescing, restart recovery,
conditional repair, source pins, stored identity and keyed-state validation,
completion publication, memory refusal, and commits that encounter known faults.
The owned local two-region corruption/rebuild/convergence scenario runs in Slow.
These scenarios do not qualify arbitrary disk corruption or the broader
power-loss durability matrix.
The existing causal-cut refusal stays in place when a rebuilt sender has no
peer rows; its liveness follow-up is [issue #219](https://github.com/TerraScale/terrascale/issues/219).
