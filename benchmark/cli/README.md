# TerraBaseV2 operator CLI (C#)

The database's operator CLI, in C# per the workspace stack policy
(charter PD-2026-08-26-01/02; ADR 0005): status (engine + topology
summary in the exact replication vocabulary), document
browse/read/write/delete, TQL query execution, the
`changes --follow` tail over the Change Event Backbone with checkpoint
resume, and session/admin diagnostics. Every command is a typed
operation. The CLI drives the API host's typed HTTP surface (SE-08) by
default; its documented **direct mode** drives the same typed
operation set against a local engine process through the SE-14
`TerraBase.Protocol` client over the required UDS binding
(PD-2026-08-26-11 D1) — never a raw socket, never a C ABI. JSON
surfaces use `System.Text.Json` only. The workspace-pinned .NET SDK
(`global.json`, same pin as the host) builds it.

## Projects

| Project | Role |
| --- | --- |
| `TerraBase.Cli` | The CLI: grammar, configuration, the host-mode and direct-mode surfaces, the output conventions, the `changes` tail with checkpoint resume. |
| `TerraBase.Cli.Tests` | The SE-13 test family (TUnit): grammar/configuration and machine-output contract rows, live API host fixture rows (the real engine process under the real ASP.NET host — the SE-08 harness pattern), the dropped-connection checkpoint-resume row, and the direct-TBEP rows. |
| `TerraBase.Cli.slnx` | The family's solution metadata. |

## Command grammar

```
terrabase [options] <command> [operands] [command options]

options (flag over environment variable over default):
  --mode host|tbep        host (default): the API host's typed HTTP surface
                          tbep: the same typed operation set on a local engine process
  --url URL               the host's loopback listener (default http://127.0.0.1:8420)
  --socket PATH           the engine's UDS socket in tbep mode (default /run/terrabase/engine.tbep.sock)
  --principal NAME        the backend-supplied principal (default terrabase-cli)
  --tenant NAME           the backend-supplied tenant (default local)
  --output human|json     the output convention; --json is the machine shorthand

commands:
  status                       the engine and topology summary
  document read COLLECTION DOCUMENT
  document write COLLECTION DOCUMENT BODY [--idempotency-key KEY] [--expected any|VERSION]
  document delete COLLECTION DOCUMENT [--idempotency-key KEY] [--expected any|VERSION]
  document browse COLLECTION [--max-items N]          (a typed TQL execution: from COLLECTION)
  query TQL-TEXT [--max-items N]      query cancel QUERY-ID
  changes [--follow] [--from SEQUENCE] [--collections A,B] [--idle MS] [--max-events N]
  sessions                     audit [--limit N]            metrics
  engine health|status|topology|shutdown [--reason operator|supervisor]
  help

environment: TERRABASE_MODE, TERRABASE_HOST_URL, TERRABASE_TBEP_SOCKET,
TERRABASE_PRINCIPAL, TERRABASE_TENANT, TERRABASE_OUTPUT

exit codes: 0 completed, 1 usage, 2 typed refusal, 3 unavailable
```

The grammar fails closed: an unknown option, an unknown command, a
missing operand, an option outside its command, or a malformed operand
or option value is a usage error (exit 1) that runs nothing — never a
crash and never a guessed invocation. Typed values are validated before
anything is sent: `--max-items` is a whole number, `--idle`,
`--max-events`, and `--limit` are positive whole numbers, `--from` and
`QUERY-ID` are sequence numbers, `--expected` is `any` or an exact
version in both modes, and the `document write` body is one JSON
document (a non-JSON body is refused with its bounded reason, and
`--idle`/`--max-events` are refused before the subscription opens, not
after it). `document write` takes the body as plain JSON (a file's
worth of it as one shell argument) and converts it exactly into the
host's typed value form; `--idempotency-key` pins the retry identity of
a mutation (a generated key otherwise — scripts should pin their own).
`--expected any|VERSION` maps the typed expected-version conditional.

## The two modes

**Host mode (default).** Every command rides the API host's typed HTTP
surface with the backend-supplied context headers the host's local
gate checks (`X-TB-Principal`, `X-TB-Tenant` — context labels, not
credentials; PD-2026-08-26-11 D1). `changes` consumes the host's SSE
projection of the Change Event Backbone; `sessions`, `audit`, and
`metrics` read the host's tracked registry, lifecycle audit log, and
counters.

**Direct mode (`--mode tbep`).** The same typed operation set against
a local engine process over the SE-14 client: the channel binds once
to the backend-supplied context, then serves typed calls and the
backbone subscription's unsolicited notifications on one connection.
When that connection is lost the surface rebuilds it (re-Connect and
re-Bind over the same socket) once per failed call, a following tail's
parked read is released with the drop, and the tail re-subscribes from
its checkpoint — so an engine that dies under `changes --follow` is
survived: the reconnect notice prints, and an engine that takes the
same socket back up is followed from where the tail stood. Honest
limits: the sync-session registry, the audit log, and the metrics are
the API host's tracked state — `sessions` answers the engine's own
counters with `available: false`, and `audit`/`metrics` fail closed as
host-surface commands.

## The `changes` tail

`changes` replays the retained window from `--from` (inclusive) and,
without `--follow`, ends at its idle bound (`--idle` milliseconds of
quiet, then a clean unsubscribe and exit 0). `--follow` keeps the tail
open; `--max-events N` bounds it for scripts. When the connection
drops under a following tail — a transport loss, a stream end without
a close frame, or a resume-able `engine-generation-lost` close — the
CLI reconnects from its checkpoint: the last seen change-event
sequence, so the missed range is re-delivered exactly once, with no
duplicate (the host's resume contract: strictly after the last seen
event). The operator's interrupt (Ctrl-C) stops a tail gracefully
(exit 0). Human lines use the glossary vocabulary exactly —
`change event ...`, the content-free `Change Notification ...`,
`reconnect checkpoint=N ...`.

## Output conventions

**Machine output** (`--json` or `--output json`) is stable and
versioned: one JSON object per line on standard output —
`cliVersion` (currently `1`; additive changes bump it), `command`,
`mode`, then exactly one payload property: `result` (a completed
command), `event` (one change event or Change Notification of a
tail), or `notice` (a lifecycle fact: opened, reconnect, idle-close,
drop, closed). Typed errors are error envelopes on standard error:
`{"cliVersion":1,"command":"...","mode":"...","error":{"code":"...","detail":"..."}}`
with exit 2 (typed refusal) or exit 3 (unavailable). No credential
exists on this local surface and none is logged: the envelope never
echoes the backend-supplied context, and no Authorization material
ever appears (tested).

**Human output** (default) uses the glossary vocabulary exactly:
Engine, TopologyView, Region, Member, Failure Domain, Sync Session,
Change Event Backbone, Change Notification, TQL. A typed error in
human mode is one bounded human line on standard error
(`terrabase: <command> <code>: <detail>`), never a raw JSON envelope;
the machine error envelope belongs to the machine convention. The
topology summary is truthful: when the engine publishes no
TopologyView at its binding, the summary says `not reported` — it
never invents a Region or Member.

## Truthful limits (the named integrator action)

The engine-backed document/query/family operations are refused by the
real engine until the engine-side `TbepHost` wiring lands (the named
integrator action of the SE-14 record): the CLI's `document` and
`query` commands complete their typed round trips and surface the
engine's truthful typed refusal (`operation_refused`, exit 2) —
exactly the behavior the SE-08 family pinned. The sync-delivery
family, backbone streams, topology/admin reads, sessions, and
supervision are served by the real engine today, and the test family
exercises every command against the live host fixture.

## Configuration and packaging

Configuration precedence is fixed: flag over environment variable
over default (the environment variables are documented above). The
principal/tenant are the backend-supplied context of a local operator
session — labels the local gate checks, never secrets — and the CLI
never logs them.

Packaging: the projects build with the workspace-pinned .NET SDK
(`dotnet build cli/TerraBase.Cli.slnx`, warnings-as-errors). For an
operator install, publish the framework-dependent executable and put
it on `PATH` as `terrabase`:

```
dotnet publish cli/TerraBase.Cli -c Release -o <publish-dir>
ln -s <publish-dir>/TerraBase.Cli <bin-dir>/terrabase
```

No license keys, activation, or entitlement gates (the repository's
freely-usable rule); the package license metadata and release gate
resolve before any public release, as the charter requires.

## Running

```
cd cli
dotnet run --project TerraBase.Cli -- help
dotnet run --project TerraBase.Cli -- status                     # host mode
dotnet run --project TerraBase.Cli -- --mode tbep --socket /path/to/engine.sock status
dotnet run --project TerraBase.Cli.Tests                          # the SE-13 family (the .NET 10+ SDK
                                                                  # rejects VSTest mode, so the family
                                                                  # runs its own test executable)
```

The live-host rows build the repository's actual engine server into a
throwaway cargo project outside the repository (nothing in the Rust
scope is touched; the harness home is shared with the SE-08 family's,
so the engine is built once per worktree — `~1 minute` cold; set
`TERRABASE_TEST_CARGO_ONLINE=1` to allow a networked fetch retry).
