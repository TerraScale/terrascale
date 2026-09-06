# Streaming invariant failure brief

## Reproduction

The already-run workspace test command was:

```text
nice -n 19 env CARGO_BUILD_JOBS=8 cargo test --workspace --locked
```

It failed at `streaming::tests::payload_less_family_kinds_never_mint_change_payloads`:

```text
src/streaming/tests.rs:3587:9:
family:validation/ produced at least one event

test result: FAILED. 1128 passed; 1 failed; 3 ignored
```

The exact captured output is in `/tmp/terrascale-engine-test-current.log`.

## Counterexample and cause

The fixture commits a real validation write at `src/streaming/tests.rs:3540-3553`.
`ValidationWrite` reaches `validation_stage` in `src/families/store.rs:904-925`,
which calls `Records::write` with `family:validation/<collection>`.

`Records::write` in `src/families/state.rs:203-214` recognizes that key through
`private_control_record` (`src/families/state.rs:1465-1467`) and routes it to
`Transaction::put_private_metadata`, rather than `Transaction::put`.

Private metadata is deliberately excluded from the published document write
list: the module-level contract in `src/families/state.rs:1-9` says validation
and migration controls are private tenant-scoped MVCC metadata and do not
become customer documents or journal events. The SQLite commit path builds
`PublishedWrite` values only while iterating `intent.writes` at
`src/storage/sqlite/mod.rs:2780-2840`; metadata changes are staged separately
by `private_metadata::stage_controls` at line 2765.

Therefore `expand_commit_for` (`src/streaming/subscription.rs:757-793`) never
sees the validation change, so it cannot produce a `ChangeEvent` for
`family:validation/`. This is a concrete missing event, not a false payload:
the test fails on `events.is_empty()` before checking `payload().is_none()`.

The same architectural boundary applies to `family:migration/`, although the
current seven-prefix fixture does not include that prefix. The other fixture
families use customer documents in `terrabase.families` and do enter
`PublishedWrite`; `payloads::derive` then correctly returns `None` for their
payload-less identifiers.

## Correctness decision required

There are two coherent contracts, and the repair must choose one explicitly:

1. If validation controls are intended to remain private/unbilled and absent
   from the customer change stream, remove them from this payload-less
   *family event* fixture and add a separate assertion that a validation
   commit changes no journal event. That preserves the documented storage and
   streaming boundary while testing the actual invariant.
2. If every family mutation, including validation, must emit a change event,
   add an explicit private-control publication path to the journal and define
   its event identity/charging/filter semantics. Simply changing
   `payloads::derive` cannot fix this counterexample because no `PublishedWrite`
   exists to expand, and converting controls into customer documents would
   violate the private metadata contract.

Do not weaken the existing assertion by allowing an empty validation event
set while retaining the claim that all seven kinds produce events; that would
hide the documented private-control boundary.

## Verification limits

No source or test implementation was changed for this brief. No new build was
started after the completed workspace test because the build lane was released
pending root integration. Parent agents had concurrent edits in the worktree
during the recorded test run; the failure above is from the captured run.
