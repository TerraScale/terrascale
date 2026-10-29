# JSON streams

`--json` streams use NDJSON: `{type:"record",data:<typed value>}` for each item,
then exactly one `{type:"complete"}` on clean EOF. Empty streams still complete.
A failure after command selection uses `{type:"error",error:{code,message,status?,requestId?}}`
and exit 1 (or 2 for invalid arguments). A writer failure may prevent the terminal
record: missing complete always means incomplete output. Records already emitted
remain partial results; consume with backpressure, never buffer the entire stream.

For `db changes`, SIGINT emits an interrupted error and exits 1 in JSON mode;
ordinary output retains its existing graceful SIGINT behavior. Completion certifies
only stream termination. Document listings across pages are not snapshots.
