# GAP-D01 WS-PARITY proposals and rider dispositions

Status: documentation-only close-out note for ticket TerraScaleV2 #58
(GAP-D01). This note proposes parity dispositions for the integrator; it
changes no row status, no Proof ID list, and no overlay. Evidence identity:
the engine-side session is `src/session` (contract
`docs/sessions/session-contract.md`, ownership decision
`docs/sessions/gapd01-ownership-decision.md`), focused in-crate tests in
`src/session/tests.rs` (24 tests) and public-vocabulary tests in
`tests/session/session_public_construction.rs` (6 tests). V1 evidence is
quoted against the pinned sweep commit `fa99b925…`
(`docs/parity/ps-01-documents-sweep.md:16-17`). No parity or performance
claim is made here; no benchmark lane was run.

## Proposed dispositions (for the integrator's WS-PARITY adjudication)

| Row | V2 evidence from this packet | Proposed direction |
|---|---|---|
| PB-2000 lifecycle | `Session::open` options set; no-tracking and cluster-wide refusals are typed | implemented-with-proof at the session seam once adjudicated |
| PB-2001 SaveChanges unit of work | `save_changes` orders deletes→puts→deferred into one engine transaction; committed-state clearing (`successful_save_clears_committed_state_and_evicts_deleted_documents`); rejected saves keep state (`store_new_refuses_an_existing_document_at_commit_and_keeps_session_state`) | as above |
| PB-2003 identity forms (engine half) | `store_generated` allocates from the identity family in the save transaction; veto leaks nothing (`a_vetoed_save_leaks_no_generated_identity`) | as above; convention extraction remains SDK-side, HiLo block distribution GAP-F9 |
| PB-2005 load and batch | `load`/`load_many` identity map, read-your-writes, known-missing (`loads_resolve_by_collection_key_and_batches_preserve_request_order`, `absent_loads_register_known_missing_and_stay_loaded`) | as above |
| PB-2008 store/overwrite | `store`/`store_new`/`store_checked` expected-version mapping (`checked_store_sends_the_expected_version`); **accepted difference**: a store queued after a delete subsumes it (last-command-wins) where V1 throws `InvalidOperationException` (`InMemoryDocumentSessionOperations.cs:847-848`) — dispositioned in session-contract.md R3 (`delete_then_store_commits_the_put_an_accepted_difference_from_v1`) | as above |
| PB-2009 delete | `delete`/`delete_expected` deferred deletes; committed eviction; a delete cancels a pending put exactly as V1's `Delete` does (`InMemoryDocumentSessionOperations.cs:730-763`) | as above |
| PB-2010 refresh/evict | `refresh`, `evict`, `clear`, `is_loaded`, `is_known_missing` (`identity_map_pins_the_loaded_instance_until_refresh_evict_or_save`) | as above |
| PB-2011 tracking/change detection | identity map pinning, `has_changed`/`has_changes`, `ignore_changes_for`, no-tracking refusal set (`no_tracking_reads_answer_but_refuse_every_write_path`, `ignore_changes_for_keeps_a_document_tracked_but_outside_the_save`) | as above |
| PB-2012 WhatChanged | field-level old/new/change-type with V1's vocabulary, idempotent re-inspection (`what_changed_reports_field_level_old_new_and_change_types`) | as above |
| PB-2013 defer/batch | typed deferred family commands, V1 batch order, deferred-vs-delete save-time refusal (`save_stages_deletes_then_puts_then_deferred_commands_in_v1_batch_order`, `deferred_command_on_a_session_deleted_document_refuses_the_whole_save`) | as above |
| PB-2016 mode contract | `SessionTransactionMode` selection; `ClusterWide` fails closed at open (`cluster_wide_mode_fails_closed_at_open`); sharded variants stay #51 | as above, engine-side mode contract only |
| PB-2019 lazy reads | pending set, one-batch execution, batch counter, batch-at-save (`pending_lazy_reads_execute_as_one_batch_and_batch_again_at_save`, `evict_and_clear_remove_pending_lazy_reads`) | as above |
| PB-2023 session stream | `stream_documents` over W4-STREAM, `load_starting_with` paging (`stream_documents_serves_canonical_batches_with_terminal_completion`, `load_starting_with_pages_by_prefix_and_match`) | as above |
| PB-2027 hooks | pre-commit before-store (mutate + veto), before-delete (veto), after-save observation; conversion hooks accepted-difference (`before_store_mutates_the_staged_value_and_before_delete_vetoes_atomically`, `after_save_observes_each_commit_once_with_operation_and_sequence`) | as above for lifecycle hooks; conversion hooks accepted-difference |
| PB-2021 request cache | none here — SDK-side per PD-2026-08-26-12 | no engine disposition proposed |

## Rider dispositions (ticket close-out table)

| Rider | Disposition |
|---|---|
| GAP-D05 identity conventions | Implemented here (engine half): save-time no-explicit-ID allocation from the identity family in the same transaction, `generated_ids` reporting. Folded elsewhere: entity-id convention extraction (SDK tickets, language-typed); HiLo block distribution and the concurrent-session allocation guard (GAP-F9). |
| GAP-D06 WhatChanged | Implemented here: engine-authoritative `what_changed` with field-level old/new values, dotted paths, array-index diffs, and V1's eight-value change-type set (`FieldChangeKind`). |
| GAP-D08 Defer and batch commands | Implemented here: `defer(SessionCommand::Family(..))` typed queue; deletes→puts→deferred staging order; deferred-vs-delete save-time refusal; atomic same-transaction commit via the family stage seam. |
| GAP-D12 request cache / aggressive caching | Folded elsewhere (SDK-side) under PD-2026-08-26-12, exactly as the ticket constrained. The engine keeps its freshness primitives (publication events; GAP-D02's HEAD/conditional-load once it lands). |
| GAP-D14 lazy and deferred reads | Implemented here: `lazy_load` pending set, `execute_all_pending_lazy_operations` one-batch execution, execution at save, `lazy_batches_executed` observable, evict/clear removal. |
| GAP-D15 session stream surface | Implemented here: `stream_documents` (W4-STREAM cursor + bounded batches + terminal completion) and `load_starting_with` (prefix/match, skip `start`, take `page_size`). Backbone delivery stays #5. |
| GAP-D16 session event and conversion hooks | Split: lifecycle hooks implemented here with a cannot-partially-commit contract (hooks fire before anything stages); conversion hooks accepted-difference (typed documents — no session conversion step exists; SDKs map entities under PD-12); request-success/topology-update events folded to SE-08 (PB-2022 executor). |

## Findings recorded for other owners (not fixed here — out of this
packet's write scope)

1. **W4-STREAM terminal-batch item loss at the exact-fill boundary**
   (`src/streaming/sources.rs`, document source, the `short_window` branch):
   when a document-stream batch has already delivered exactly `limit` items
   and the next lookahead window is short, the item that triggered
   `batch_full` inside the short window is never delivered — the loop breaks
   before pushing it, then `short_window` completes the stream. Observed
   with a tenant holding one cross-collection document and a per-collection
   selector (batch limit 2, documents `orders/040`, `users/010..030`:
   delivered `010,020`, terminal, `030` lost); the same shape follows for a
   single collection when the remaining window length equals the limit.
   The session surface works around it by construction (its tests page on
   users-only tenants or limits covering the page). Owner: the streaming
   seam (W4-STREAM) — recommend a repair ticket with the failing shape as
   its red test. [Fixed by #87 (fleet/w4s-2), merged 2026-08-27; the
   matrix and caveat removals live with that packet. — integrator]
2. **Concurrent-session identity allocation window** (recorded in
   `docs/sessions/gapd01-ownership-decision.md`, class 12): two sessions
   whose snapshots predate each other's commits can hand out overlapping
   generated-id ranges; the guard is GAP-F9's block-distribution work.
3. **Family deferred-command operation identity**: the family stage seam
   requires the request's operation identity to equal the transaction's;
   this packet adds a crate-internal `rebase_operation` on
   `FamilyWriteRequest` (and its payloads) so session-deferred commands
   adopt the save's identity at save time. No public contract changed.

## What this packet does not claim

- No V1/V2 parity claim: rows stay `unresolved` until the integrator
  adjudicates the proposals above and records Proof IDs.
- No performance claim of any kind; the benchmark lane was not run.
- The C# host session facade, TBEP session frames, and MCP exposure are
  SE-08/AI-01 work; cluster-wide execution is SH-05/#51; includes are
  GAP-D11; conditional load/HEAD are GAP-D02/D03.
