# Backup manifest provenance consumers handoff

Updated only the assigned consumer modules:

- `src/backup/restore/mod.rs`: encrypted local discovery now accepts only
  `BackupCaptureKind::WholeDatabase`; routed participant manifests cannot enter
  standalone chain/timestamp resolution. Existing `ManifestFacts` fixtures
  explicitly set whole-database provenance.
- `src/backup/worker/remote.rs`: direct remote point discovery applies the same
  whole-database check and maps a participant manifest to `FleetError::Capture`.
- `src/backup/worker/inventory.rs`: local inventory consumes the hardened
  restore discovery helper; no separate decode path exists.

Envelope participant decoding remains in the envelope path and is not routed
through standalone discovery. No compatibility default, framing change, or
artifact deletion was introduced.

Validation passed:

- `rustfmt --check src/backup/restore/mod.rs src/backup/worker/inventory.rs src/backup/worker/remote.rs`
- `git diff --check`

Cargo compilation/tests were not run; the shared build owner handles the
temporary cross-agent compile window and focused selectors.

Follow-up boundary: `resolve_encrypted_points_chain` now rejects any
already-decoded participant or mixed-kind point set as `ManifestInvalid`.
This helper is called only by standalone local/remote point resolution and
chain verification; authenticated routed envelopes use their participant
path instead. Added test: `encrypted_chain_rejects_participant_and_mixed_capture_kinds`.

Updated restore source SHA-256:
`49adbfd7252a837d161e4e5869edc55ef9ea674755e5bef1b7a6a71970b8d71e`
