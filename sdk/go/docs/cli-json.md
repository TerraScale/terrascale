# CLI JSON output

`--json` (also `-json`) selects machine output and may appear before or after
the command. `--json=false` retains ordinary output. Flag values and operands
after `--` are preserved.

All original command forms support JSON, with objects for finite results, arrays
for resource lists and NDJSON record/complete/error envelopes for unbounded streams.
See the cli-json-auth, cli-json-streams, cli-json-metadata, cli-json-documents,
cli-json-functions, cli-json-migrations and cli-json-diff documents for field shapes.

Finite success emits one JSON value followed by a newline: metadata objects, arrays
for lists (including `[]`), `{name,deleted:true}` for database deletion, `{version}` for
version and `{commands}` mapping command names to accepted operand counts for help.
Errors are `{error:{code,message,status?,requestId?}}`; HTTP metadata is included
when the SDK supplies it. Messages exclude arbitrary response bodies, URLs and
credentials. An uncertain write must be inspected before retrying.

Exit status remains 0 for success, 1 for operational failure and 2 for invalid
arguments. Stdout contains only JSON; consent prompts use stderr. Writer failures
return nonzero and may leave incomplete output, which must not be accepted as a
successful result. Database lists aggregate pages; they are not a snapshot.
