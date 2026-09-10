# Import retry repair

## Scope and cleanup trace

Production entrypoint: api.rs routes imports create/list/get/upload/resume to api_imports.rs. ImportRuntime owns ImportStore SQLite metadata and encrypted import_runtime.rs uploads. execute_import decodes verified input, saves pending batch metadata, invokes EngineImportWriteTarget, and advances the cursor after a proven commit. EngineImportWriteTarget uses ordinary engine transactions, attach_write_budget, and durability confirmation. All three import modules remain live; no independently retired path was identified before implementation.

Removed export_formats::batch_operation_id and its deterministic-only test after replacing their sole production caller. The similarly named private engine ingest helper is independent and remains in use. No shared API/storage/runtime files were edited.

## Concrete defect and repair

SQLite src/storage/sqlite/mod.rs insert_rejection_row durably binds operation identities to rejection. Import retries previously derived the same batch identity from upload identity/digest/committed ordinal. A proven rejected batch could therefore never succeed after its refusal condition cleared.

api_imports.rs commit_batch now creates a fresh ULID attempt identity and persists it through ImportStore::begin_batch before submitting the write. Pending identities survive crashes and must resolve before another attempt can be submitted. A proven rejection clears the pending identity; the next attempt receives a new identity. The import operation identity and completed cursor remain unchanged. Trait documentation makes explicit that Err proves no submission; uncertain commits return Unknown.

## Verification

- rustfmt --edition 2024 on both changed Rust files: passed.
- git diff --check on both changed Rust files: passed.
- Cargo execution delegated to tb_checks; no Cargo/build was run by this worker.
- Selector api::imports::tests includes new rejected_batch_resumes_after_restart_with_new_attempt_identity: models the storage rejection ledger, drops/reopens actual ImportRuntime/SQLite, verifies fresh attempt and one committed record under the same import identity.
- Existing unknown_resume_resolves_before_any_second_write now drops/reopens runtime from running with a persisted pending batch, verifies recovery to unknown with the identical pending identity, and resolves a receipt without any second write submission.
- Existing http_import_encrypts_writes_and_survives_runtime_restart covers real API/engine integration.

## Remaining gaps and limits

No claim of production qualification or invoice reconciliation. Tests at ImportWriteTarget seam prove submission counts, with existing HTTP integration separately covering actual metered write path. Per-operation cancellation endpoint is absent; current cancellation is global runtime shutdown only. Native format claims history/causal metadata while ImportRecord and write adapter import collection/id/value via ordinary puts; lossless history/causal restore needs producer/storage coordination. Neither broader contract gap was modified in this bounded fix.

## Review correction: crash before target submission

The reviewer found a liveness gap: begin_batch could persist a pending ID before target submission, and a crash then left a never-submitted identity resolving Pending forever. The executor now reconstructs the pending batch from the verified upload, validates its exact saved cursor and count, and invokes a dedicated replay method with the original pending identity. It refuses mismatched or missing boundaries without retiring the ID.

The production replay adapter submits identical document puts under that identity. Every noncommitted return, including preparation errors and digest conflicts, resolves the original ledger again. Only a proven durable rejection can retire the pending identity. This matters because a fresh schema or budget refusal is not proof that an earlier uncertain attempt failed. SQLite checks operation identity before publication and metering, so even a changed budget-bound digest cannot produce a second write under the same identity.

Added pending_before_submission_replays_original_identity_after_restart using real EngineImportWriteTarget and real runtime/SQLite close/reopen. It verifies the original pre-submission pending identity receives a committed engine receipt and the import completes.

Read-only causal review passed corrected api_imports.rs SHA256 263ec08c22452c33ea82bbe9fbae0b5d947d41853c07a1294ad79ac120685f2f. rustfmt and scoped diff check passed. Updated Rust selectors sent to tb_checks; no Rust build executed by this worker.

## Actual-engine fixture correction

The coordinated Rust run initially passed both seam regressions and failed the two actual-engine cases with Conflict. Diagnostic rebuilding confirmed engine rejection (not import preparation). New owner-config Engine::open fencing starts storage at closed origin epoch zero; the shared admitted database fixture accepted Writer epoch one in Member metadata but did not perform production admission's explicit activate_origin_epoch call. Both import actual-engine tests now explicitly activate that accepted epoch before writing, preserving production fencing. No shared fixture or production authorization change was made. Updated test run remains with tb_checks.
