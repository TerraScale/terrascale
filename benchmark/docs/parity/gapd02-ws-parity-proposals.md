# WS-PARITY proposals from GAP-D02 (ticket #59)

Proposals only — the parity overlay and its generated inventory are
integrator-owned; this builder mutates none of them (charter shared-file
rules). Each proposal cites the decision record and evidence. Decision
record: `docs/engine/metadata-change-vector-contract.md`.

## PB-2004 document-metadata-and-last-modified

Proposed disposition: `seam-implemented` for the engine metadata half, with
the decision D4 mapping recorded for the rest.

- The V1 change identity is replaced by the typed `DocumentVersionContract`
  (version + storage sequence + D-3 stamp) served at the engine read seam
  (`src/engine/metadata.rs`); `@last-modified` is explicitly **not** a
  contract component (D-3 orders on the hybrid logical clock, not wall
  time); the new-document marker maps to the existing
  `ExpectedVersion::New` and the ignore-changes marker to
  `ExpectedVersion::Any` (decision D4 table).
- Evidence: `src/engine/metadata_tests.rs`
  (`head_reports_the_published_version_contract`,
  `head_survives_reopen_with_the_same_contract`),
  `tests/engine/document_metadata_contract.rs`
  (`version_contract_reports_its_three_components`,
  `version_contract_rejects_impossible_facts`).
- Remaining delta (keep the row honest): the V1 metadata *dictionary*
  surface (`GetMetadataFor`-style session accessors) belongs to the
  session ticket GAP-D01 (#58); V2 deliberately has no user-writable
  engine metadata dictionary.

## PB-2006 exists-and-head

Proposed disposition: `seam-implemented` at the engine read seam.

- `Transaction::head(collection, id, HeadCondition)` answers V1's HEAD
  contract typed: `Missing` (V1 404), `Unchanged` (V1 304 on exact
  contract equality), `Current(contract)` (V1 200 + ETag) — decision D2,
  citing V1 `DocumentHandlerProcessorForHead.cs` at the pinned commit.
- Evidence: `head_reports_missing_for_absent_and_deleted_keys`,
  `head_conditional_answers_the_v1_three_way_outcome`,
  `head_reads_the_published_view_not_staged_writes`,
  `head_walks_past_other_tenants_commits`
  (`src/engine/metadata_tests.rs`).
- Remaining delta: distinguishing a deleted key from a never-existing one
  is tombstone-awareness (PB-2002/GAP-D09), recorded as a limit, not a
  silent difference.

## PB-2007 conditional-load-and-change-vector

Proposed disposition: `seam-implemented` for the If-Match form; the V1
conditional-GET cache shape is an accepted difference routed to the
request-cache row.

- `Transaction::load_if_match` serves only an exact contract and rejects
  everything else with the typed `ConditionalLoadError` vocabulary
  (`Missing`, `Stale { current }`, `Engine`) — decision D3. The
  delete/re-create ABA case proves the sequence component
  (`conditional_load_rejects_the_recreated_document_stale_vector`).
- Evidence: `conditional_load_serves_exact_contracts_and_rejects_stale_vectors`
  and the ABA test above (`src/engine/metadata_tests.rs`);
  `conditional_load_error_vocabulary_is_typed`
  (`tests/engine/document_metadata_contract.rs`).
- Accepted difference (proposal): V1's serve-empty-when-unchanged cache
  semantics stay with PB-2021 (GAP-D12 request cache), not the engine
  seam; the HEAD/304 behavior composes the same effect (decision D3).

## PB-5021 replication-change-vectors

Proposed disposition: unchanged accepted-difference (new V2 identity) for
the replication core, with the GAP-R5 mapping half now landed.

- The ps-04 row already proposed accepted-difference + "GAP-R5 mapping for
  migration (BK-03 #44)". GAP-D02 folded GAP-R5 (PD-2026-08-26-14 D3) and
  this packet delivers the mapping: a V1 vector of any arity reduces to
  recorded, content-free provenance (`V1ChangeVectorMapping`: arity,
  dominant entry under V1's own etag order, order-canonical digest) and no
  V1 fact becomes a live V2 version, sequence, or stamp (decision D1,
  rejected alternatives recorded).
- Evidence: `v1_change_vector_parses_the_v1_grammar`,
  `v1_change_vector_dominant_entry_follows_the_v1_etag_order`,
  `v1_change_vector_mapping_records_provenance_not_live_identity`,
  `v1_change_vector_parse_fails_closed`
  (`tests/engine/document_metadata_contract.rs`).
- Integrator action recorded in the decision record: wire the mapping into
  #44's document conversion so migrated documents carry the recorded
  provenance.

## PB-5031 replication-local-progress

Proposed disposition: unchanged accepted-difference (identity), with the
GAP-R5 citation satisfied by this packet. The V1 change-vector wait API
remains replaced by the acknowledgement/convergence seams (ps-04's
proposal stands); nothing in this packet changes the replication lane, and
the mapping half of the row's GAP-R5 dependency is now landed as for
PB-5021.
