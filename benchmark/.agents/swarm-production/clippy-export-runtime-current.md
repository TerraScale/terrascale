# Export API exact-gate correction

Captured 2026-09-11 from `/tmp/terrabase-server-exact-10-20260911.log`.

The failing test was `export_api::tests::placement_and_encryption_are_fail_closed`
at `crates/terrabase-server/src/export_api.rs:2879`. The fixture selected
`TransferFormat::Jsonl` with `ExportScope::All`, but `validate_request` correctly
rejects converted formats with non-document scope before it evaluates placement.
Consequently the observed error was `InvalidRequest("converted export formats
require documents-only scope")`, not `PlacementMismatch`. This was a test
fixture defect, not a product regression. The fixture now uses
`ExportScope::Documents`, allowing the assertion to exercise the intended
placement boundary while retaining the separate missing-dependency/encryption
fail-closed assertion.
