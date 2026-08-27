# The SE-11 end-to-end client live-update tracer

The capstone demonstrable (ticket #13): a commit lands on the engine,
the backbone fans it out, the engine's Loro delivery layer
materializes it, the C# host forwards it over `terrascale.sync.v2`,
and both embedded SDK cores — TypeScript and .NET — apply delta
updates, with no polling anywhere.

This directory holds the tracer harness; `scripts/se11/run.py` is the
orchestrator that builds and supervises the full topology (the real
engine process from `crates/terrabase-tbep` through a throwaway cargo
project under the run's temp directory — the SE-08 `EngineHarness`
pattern — plus the real `host/TerraBase.Api` over loopback) and drives
both tracers through the scenario family. The evidence receipts land
under `evidence/sync/se11/`; the packet record with the
acceptance-criteria dispositions, the reproduction sequence, and the
recorded findings is `evidence/sync/se11-live-update-tracer.md`.

| Path | What it is |
| --- | --- |
| `ts/tracer.mjs` | The TypeScript core: one node process embedding the sibling TerraScale.TypeScript checkout's built `dist` (read-only), driven by a scripted plan, emitting a uniform JSONL observation log. |
| `dotnet/TerraScale.Tracer/` | The .NET core: the same plan vocabulary and log contract over the sibling TerraScale.DotNet client (project-referenced read-only; `TERRASCALE_DOTNET_SDK` overrides the default workspace-relative path). |

Both tracers consume the same plan vocabulary (`connect`, `mutate`,
`pull`, `resync`, `waitCheckpoint`, `waitStream`, `gate`, `signal`,
`watch`, `dispose`, `final`) and emit the same event vocabulary
(`opened`, `stream-opened`, `status`, `snapshot-adopted`,
`delta-applied`, `duplicate-delta`, `mutation`, `error`, `final`), so
the orchestrator can diff the two cores' transition sequences and
assert their identity against the SE-04 golden vectors.

`waitStream` is the deterministic gate every measured write waits
behind: both SDK cores open their push channel without awaiting it
(the .NET live loop subscribes only after setup and its initial pull,
and has no fallback pull before its first trigger; the TS `connect()`
fires the stream factory and returns), so a commit landing between the
session setup and the subscription is undeliverable. Each tracer
observes its own `/v1/streams` subscription on its own wire — the .NET
tracer wraps its `HttpClient` in an observing `DelegatingHandler`, the
TS tracer passes an observing `Fetch` that delegates one-to-one to the
global fetch; neither touches a byte of any request or response — and
emits a `stream-opened` observation the step waits on. The host
registers the subscriber and the engine's backbone subscription before
it writes the stream's first event, so the observation being visible
on the client means every later trigger fans out to it.

Everything here is correctness evidence, never a performance claim:
loopback hosts, temp-dir sockets, backend-context fixtures only
(`X-TB-Principal`/`X-TB-Tenant` — the local boundary of
PD-2026-08-26-11 has no authentication surface by design). Payloads
stay opaque; the tracers record payload byte identities (SHA-256
digests), never payload contents. No secrets, no live systems, no
pushes; the sibling checkouts are consumed read-only.
