//! Focused tests of the LSM storage engine (P1).
//!
//! These prove the engine's own contracts: bounded durable bytes per
//! write, atomic-batch recovery, torn-tail truncation, idempotent replay,
//! MVCC snapshot reads, rotation and flush, manifest recovery, and the
//! compaction checkpoint ordering. The full engine suite exercises the
//! same [`StoragePort`] seam through the backend selector.

use crate::{
    engine::{Engine, EngineConfig},
    regions::RegionCode,
    storage::{
        self, StoragePort, UsageFactPosition, UsageStorageError,
        compaction::{CompactionPolicy, CompactionRequest},
        faults::FaultPoint,
        frontier::{
            LedgerFrontierActivationError, LedgerFrontierActivationIdentity,
            LedgerFrontierActivationRequest, LedgerFrontierStatus,
        },
        intent::{
            CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite,
            UnknownCause,
        },
        lsm::LsmStorage,
    },
    types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    },
    usage::{
        MeasurementWindow, UsageAdmissionContext, UsageFactId, UsageRecordMeter, UsageRecordRequest,
    },
};

fn location(tag: &str) -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "terrabase-lsm-{tag}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn document(collection: &str, id: &str, text: &str) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::String(text.to_owned()),
    )
}

fn usage_admission(operation: &str, accepted_at_millis: u64) -> UsageAdmissionContext {
    UsageAdmissionContext::try_new(
        "project-a",
        "database-a",
        "partition-a",
        RegionCode::GRU,
        UsageFactId::try_new(operation).expect("valid usage identity"),
        accepted_at_millis,
        1,
        1,
    )
    .expect("valid usage admission")
}

fn usage_record_fact(
    scope_tenant: &TenantId,
    fact_id: &str,
    recorded_at_millis: u64,
    integer_units: i128,
) -> crate::usage::UsageFact {
    UsageRecordRequest::try_new(
        UsageRecordMeter::PublicEgressBytes,
        "project-a",
        "database-a",
        "partition-a",
        RegionCode::GRU,
        UsageFactId::try_new(fact_id).expect("valid fact identity"),
        recorded_at_millis,
        1,
        integer_units,
    )
    .expect("valid usage record")
    .fact_for(scope_tenant.clone())
    .expect("tenant-bound fact")
}

fn put_intent(
    tenant: &TenantId,
    operation: &str,
    sequence: u64,
    document: Document,
) -> CommitIntent {
    CommitIntent {
        tenant: tenant.clone(),
        operation: OperationId::try_new(operation).expect("valid operation"),
        digest: storage::write_set_digest(
            tenant,
            &[StagedWrite::Put { document: document.clone(), expected: ExpectedVersion::Any }],
        ),
        snapshot_sequence: crate::types::StorageSequence::new(sequence),
        durability: LocalDurability::Synced,
        usage_admission: None,
        writes: vec![StagedWrite::Put { document, expected: ExpectedVersion::Any }],
    }
}

fn delete_intent(
    tenant: &TenantId,
    operation: &str,
    sequence: u64,
    id: &str,
    expected: ExpectedVersion,
) -> CommitIntent {
    CommitIntent {
        tenant: tenant.clone(),
        operation: OperationId::try_new(operation).expect("valid operation"),
        digest: storage::write_set_digest(
            tenant,
            &[StagedWrite::Delete {
                collection: Some(CollectionName::try_new("orders").expect("valid collection")),
                id: DocumentId::try_new(id).expect("valid document"),
                expected,
            }],
        ),
        snapshot_sequence: crate::types::StorageSequence::new(sequence),
        durability: LocalDurability::Synced,
        usage_admission: None,
        writes: vec![StagedWrite::Delete {
            collection: Some(CollectionName::try_new("orders").expect("valid collection")),
            id: DocumentId::try_new(id).expect("valid document"),
            expected,
        }],
    }
}

fn activate(storage: &mut LsmStorage, operation: &str) -> LedgerFrontierActivationIdentity {
    let LedgerFrontierStatus::Inactive(expected) = storage.ledger_frontier_status() else {
        panic!("a fresh LSM location is in reversible expand mode");
    };
    let identity = LedgerFrontierActivationIdentity {
        activation_id: OperationId::try_new(operation).expect("valid activation operation"),
        external_fence_generation: 1,
        external_record_digest: [0x5a; 32],
    };
    let receipt = storage
        .activate_ledger_frontier(&LedgerFrontierActivationRequest {
            identity: identity.clone(),
            expected,
        })
        .expect("LSM frontier activates");
    assert_eq!(receipt.identity, identity);
    identity
}

fn wal_frame_ends(dir: &std::path::Path) -> Vec<u64> {
    let wal = super::wal::WalLog::open_unrepaired(&dir.join("wal")).expect("WAL opens read-only");
    wal.scan().expect("WAL scans").frame_ends().to_vec()
}

fn hex(bytes: &[u8]) -> String {
    use std::fmt::Write as _;

    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        write!(output, "{byte:02x}").expect("writing to a String cannot fail");
    }
    output
}

#[test]
fn active_format_encoding_matches_the_pinned_golden_vector() {
    let active = super::ActiveFormat {
        store_id: crate::storage::frontier::StoreId::from_bytes([
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d,
            0x0e, 0x0f,
        ]),
        activation: LedgerFrontierActivationIdentity {
            activation_id: OperationId::try_new("activate-vector").unwrap(),
            external_fence_generation: 7,
            external_record_digest: [0x5a; 32],
        },
    };
    assert_eq!(
        hex(&super::encode_active_format(&active)),
        "544256324c534d3154424c534d41435402000000000000000100000000000000000102030405060708090a0b0c0d0e0f0f0061637469766174652d766563746f7207000000000000005a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a0000000000000000610bcf9ad29d289edfd4afc72534ec567a72a8e025df1c84c0e6a8f1fac2a323"
    );
}

#[test]
fn lsm_activation_is_explicit_idempotent_and_rejected_by_the_pinned_legacy_reader() {
    let dir = location("activation");
    let mut storage = LsmStorage::open(&dir).expect("legacy location opens");
    let legacy = std::fs::read(dir.join("format")).expect("legacy marker reads");
    assert_eq!(legacy, b"TBV2LSM1", "the persisted legacy token stays byte exact");

    let LedgerFrontierStatus::Inactive(stale) = storage.ledger_frontier_status() else {
        panic!("legacy location has a shadow frontier");
    };
    let scope_tenant = tenant("acme");
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-before-activation",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    let stale_request = LedgerFrontierActivationRequest {
        identity: LedgerFrontierActivationIdentity {
            activation_id: OperationId::try_new("activate-stale").unwrap(),
            external_fence_generation: 1,
            external_record_digest: [1; 32],
        },
        expected: stale,
    };
    assert_eq!(
        storage.activate_ledger_frontier(&stale_request),
        Err(LedgerFrontierActivationError::StaleObservation)
    );

    let identity = activate(&mut storage, "activate-lsm");
    let active_bytes = std::fs::read(dir.join("format")).expect("active marker reads");
    assert!(active_bytes.starts_with(b"TBV2LSM1"));
    let pinned_n_minus_one_accepts =
        std::str::from_utf8(&active_bytes).is_ok_and(|text| text.trim() == "TBV2LSM1");
    assert!(!pinned_n_minus_one_accepts, "the exact N-1 reader rejects activated bytes");
    let LedgerFrontierStatus::Active { observation, activation } = storage.ledger_frontier_status()
    else {
        panic!("the activated handle reports the boundary");
    };
    assert_eq!(observation.format_major, 2);
    assert_eq!(activation, identity);
    let retry =
        LedgerFrontierActivationRequest { identity: identity.clone(), expected: observation };
    assert_eq!(storage.activate_ledger_frontier(&retry).unwrap().identity, identity);
    drop(storage);
    let reopened = LsmStorage::open(&dir).expect("activated location reopens");
    assert!(matches!(reopened.ledger_frontier_status(), LedgerFrontierStatus::Active { .. }));
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn activation_crash_boundaries_preserve_checkpointed_data_for_n_minus_one_or_reopen_active() {
    let pre_format_faults = [
        FaultPoint::ActivationManifestTempCreated,
        FaultPoint::ActivationManifestWritten,
        FaultPoint::ActivationManifestSynced,
        FaultPoint::ActivationManifestRenamed,
        FaultPoint::ActivationManifestDirectorySynced,
        FaultPoint::ActivationFormatTempCreated,
        FaultPoint::ActivationFormatWritten,
        FaultPoint::ActivationFormatSynced,
    ];
    let post_format_faults =
        [FaultPoint::ActivationFormatRenamed, FaultPoint::ActivationFormatDirectorySynced];
    for fault in pre_format_faults.into_iter().chain(post_format_faults) {
        let dir = location(&format!("activation-crash-{fault:?}"));
        let scope_tenant = tenant("acme");
        let mut storage = LsmStorage::open(&dir).unwrap();
        assert!(matches!(
            storage.commit(put_intent(
                &scope_tenant,
                "op-before-activation-crash",
                0,
                document("orders", "order-1", "checkpointed"),
            )),
            CommitResult::Committed(_)
        ));
        storage
            .compact(storage::compaction::StorageCompaction {
                policy: CompactionPolicy::Reclaim,
                floor: None,
                progress: None,
            })
            .expect("legacy checkpoint completes before activation");
        let legacy_manifest = std::fs::read(dir.join("MANIFEST")).unwrap();
        assert!(legacy_manifest.starts_with(b"TBLSMMF1"));
        let LedgerFrontierStatus::Inactive(expected) = storage.ledger_frontier_status() else {
            panic!("checkpointed legacy location exposes its shadow frontier");
        };
        let identity = LedgerFrontierActivationIdentity {
            activation_id: OperationId::try_new(&format!("activate-crash-{fault:?}")).unwrap(),
            external_fence_generation: 7,
            external_record_digest: [0x37; 32],
        };
        storage.arm_test_fault(fault);
        assert_eq!(
            storage.activate_ledger_frontier(&LedgerFrontierActivationRequest {
                identity: identity.clone(),
                expected,
            }),
            Err(LedgerFrontierActivationError::StorageUnavailable),
            "{fault:?}"
        );
        assert_eq!(
            std::fs::read(dir.join("MANIFEST")).unwrap(),
            legacy_manifest,
            "the pinned N-1 manifest stays byte exact at {fault:?}"
        );
        drop(storage);

        let mut reopened = LsmStorage::open(&dir)
            .unwrap_or_else(|error| panic!("{fault:?} must reopen, got {error:?}"));
        let state = reopened.published_state().0;
        assert!(
            reopened
                .read_document_in_collection_at(
                    state,
                    &scope_tenant,
                    &CollectionName::try_new("orders").unwrap(),
                    &DocumentId::try_new("order-1").unwrap(),
                )
                .is_some(),
            "checkpointed data survives {fault:?}"
        );
        match reopened.ledger_frontier_status() {
            LedgerFrontierStatus::Inactive(observation) => {
                let receipt = reopened
                    .activate_ledger_frontier(&LedgerFrontierActivationRequest {
                        identity: identity.clone(),
                        expected: observation,
                    })
                    .unwrap_or_else(|error| panic!("{fault:?} retry failed: {error:?}"));
                assert_eq!(receipt.identity, identity);
            }
            LedgerFrontierStatus::Active { activation, .. } => {
                assert_eq!(activation, identity, "post-rename recovery binds the same record");
            }
            LedgerFrontierStatus::Unavailable => panic!("{fault:?} reopened unavailable"),
            LedgerFrontierStatus::UnsupportedBackend => {
                panic!("{fault:?} reopened on the wrong backend")
            }
        }
        std::fs::remove_dir_all(dir).unwrap();
    }
}

#[test]
fn format_activation_failures_poison_the_same_handle_until_reopen() {
    for fault in [
        FaultPoint::ActivationFormatTempCreated,
        FaultPoint::ActivationFormatWritten,
        FaultPoint::ActivationFormatSynced,
        FaultPoint::ActivationFormatRenamed,
        FaultPoint::ActivationFormatDirectorySynced,
    ] {
        let dir = location(&format!("activation-same-handle-{fault:?}"));
        let scope_tenant = tenant("acme");
        let mut storage = LsmStorage::open(&dir).unwrap();
        let LedgerFrontierStatus::Inactive(expected) = storage.ledger_frontier_status() else {
            panic!("a fresh LSM location exposes its shadow frontier");
        };
        let identity = LedgerFrontierActivationIdentity {
            activation_id: OperationId::try_new(&format!("activate-same-handle-{fault:?}"))
                .unwrap(),
            external_fence_generation: 9,
            external_record_digest: [0x49; 32],
        };
        let request = LedgerFrontierActivationRequest { identity: identity.clone(), expected };
        storage.arm_test_fault(fault);
        assert_eq!(
            storage.activate_ledger_frontier(&request),
            Err(LedgerFrontierActivationError::StorageUnavailable),
            "{fault:?}"
        );
        assert_eq!(storage.ledger_frontier_status(), LedgerFrontierStatus::Unavailable);
        assert_eq!(
            storage.activate_ledger_frontier(&request),
            Err(LedgerFrontierActivationError::StorageUnavailable),
            "a poisoned retry cannot return an activation receipt at {fault:?}"
        );
        let format = std::fs::read(dir.join("format")).unwrap();
        let renamed = matches!(
            fault,
            FaultPoint::ActivationFormatRenamed | FaultPoint::ActivationFormatDirectorySynced
        );
        if renamed {
            assert!(
                super::decode_active_format(&format).is_ok(),
                "the active format is visible after {fault:?}"
            );
        } else {
            assert_eq!(
                format,
                super::FORMAT_MARKER.as_bytes(),
                "the live format stays legacy at {fault:?}"
            );
        }
        let wal_before = std::fs::read(dir.join("wal")).unwrap();
        let frontier_before = std::fs::read(dir.join("wal.frontier")).unwrap();
        let mut after_failed_activation = put_intent(
            &scope_tenant,
            "op-after-failed-activation",
            0,
            document("orders", "order-1", "must-not-publish"),
        );
        after_failed_activation.durability = LocalDurability::Written;
        assert_eq!(
            storage.commit(after_failed_activation),
            CommitResult::Rejected(CommitRejection::Storage),
            "{fault:?}"
        );
        assert_eq!(std::fs::read(dir.join("wal")).unwrap(), wal_before, "{fault:?}");
        assert_eq!(std::fs::read(dir.join("wal.frontier")).unwrap(), frontier_before, "{fault:?}");
        drop(storage);

        let mut reopened = LsmStorage::open(&dir).expect("the activation boundary reopens");
        match reopened.ledger_frontier_status() {
            LedgerFrontierStatus::Active { activation, .. } => {
                assert!(renamed, "only a renamed format reopens active at {fault:?}");
                assert_eq!(activation, identity, "{fault:?}");
            }
            LedgerFrontierStatus::Inactive(expected) => {
                assert!(!renamed, "a renamed format cannot reopen legacy at {fault:?}");
                reopened
                    .activate_ledger_frontier(&LedgerFrontierActivationRequest {
                        identity: identity.clone(),
                        expected,
                    })
                    .unwrap_or_else(|error| {
                        panic!("{fault:?} retry after reopen failed: {error:?}")
                    });
            }
            status => panic!("{fault:?} reopened with {status:?}"),
        }
        assert!(matches!(
            reopened.commit(put_intent(
                &scope_tenant,
                "op-after-reopen",
                0,
                document("orders", "order-1", "publishes-after-reopen"),
            )),
            CommitResult::Committed(_)
        ));
        std::fs::remove_dir_all(dir).unwrap();
    }
}

#[test]
fn activated_wal_rejects_exact_final_commit_and_rejection_frame_loss() {
    let commit_dir = location("active-final-commit-loss");
    let scope_tenant = tenant("acme");
    let mut storage = LsmStorage::open(&commit_dir).unwrap();
    activate(&mut storage, "activate-commit-loss");
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-commit-loss",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    drop(storage);
    let ends = wal_frame_ends(&commit_dir);
    assert!(ends.len() >= 2, "Synced commit carries its batch and durability flip");
    let shortened = ends[ends.len() - 2];
    let wal_path = commit_dir.join("wal");
    let mut bytes = std::fs::read(&wal_path).unwrap();
    bytes.truncate(usize::try_from(shortened).unwrap());
    std::fs::write(&wal_path, &bytes).unwrap();
    let manifest_before = std::fs::read(commit_dir.join("MANIFEST.active")).unwrap();
    assert_eq!(LsmStorage::open(&commit_dir).err(), Some(super::LsmOpenError::FormatMismatch));
    assert_eq!(std::fs::read(&wal_path).unwrap(), bytes, "refusal does not rewrite the WAL");
    assert_eq!(std::fs::read(commit_dir.join("MANIFEST.active")).unwrap(), manifest_before);

    let rejection_dir = location("active-final-rejection-loss");
    let mut storage = LsmStorage::open(&rejection_dir).unwrap();
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-seed-rejection",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    activate(&mut storage, "activate-rejection-loss");
    let before_rejection = storage.wal_len();
    assert!(matches!(
        storage.commit(delete_intent(
            &scope_tenant,
            "op-conflict-loss",
            storage.published_state().0.get(),
            "order-1",
            ExpectedVersion::Exact(99),
        )),
        CommitResult::Rejected(storage::intent::CommitRejection::Conflict)
    ));
    drop(storage);
    let bytes = std::fs::read(rejection_dir.join("wal")).unwrap();
    assert!(bytes.len() as u64 > before_rejection);
    std::fs::write(rejection_dir.join("wal"), &bytes[..usize::try_from(before_rejection).unwrap()])
        .unwrap();
    assert_eq!(LsmStorage::open(&rejection_dir).err(), Some(super::LsmOpenError::FormatMismatch));
    std::fs::remove_dir_all(commit_dir).unwrap();
    std::fs::remove_dir_all(rejection_dir).unwrap();
}

#[test]
fn activated_wal_rejects_legal_length_corruption_and_fnv_valid_substitution() {
    for case in ["length", "substitution"] {
        let dir = location(case);
        let scope_tenant = tenant("acme");
        let mut storage = LsmStorage::open(&dir).unwrap();
        activate(&mut storage, &format!("activate-{case}"));
        assert!(matches!(
            storage.commit(put_intent(
                &scope_tenant,
                "op-corrupt",
                0,
                document("orders", "order-1", "first"),
            )),
            CommitResult::Committed(_)
        ));
        drop(storage);
        let ends = wal_frame_ends(&dir);
        let frame_end = usize::try_from(*ends.last().unwrap()).unwrap();
        let frame_start = usize::try_from(ends[ends.len() - 2]).unwrap();
        let mut bytes = std::fs::read(dir.join("wal")).unwrap();
        if case == "length" {
            let length =
                u32::from_le_bytes(bytes[frame_start + 12..frame_start + 16].try_into().unwrap());
            bytes[frame_start + 12..frame_start + 16]
                .copy_from_slice(&length.saturating_add(1).to_le_bytes());
        } else {
            let payload = &mut bytes[frame_start + 16..frame_end];
            let operation_at = payload
                .windows(b"op-corrupt".len())
                .position(|window| window == b"op-corrupt")
                .expect("the ledger key carries the operation identity");
            payload[operation_at + 1] = b'q';
            let checksum = (crate::storage::codec::fnv1a(payload) & 0xFFFF_FFFF)
                | (u64::from(0x544C_534D_u32) << 32);
            bytes[frame_start + 4..frame_start + 12].copy_from_slice(&checksum.to_le_bytes());
        }
        std::fs::write(dir.join("wal"), &bytes).unwrap();
        assert_eq!(
            LsmStorage::open(&dir).err(),
            Some(super::LsmOpenError::FormatMismatch),
            "{case}"
        );
        assert_eq!(std::fs::read(dir.join("wal")).unwrap(), bytes);
        std::fs::remove_dir_all(dir).unwrap();
    }
}

#[test]
fn activated_wal_repairs_a_valid_unanchored_suffix_and_truncates_only_a_torn_tail() {
    use std::io::Write as _;

    let dir = location("active-unanchored-suffix");
    let scope_tenant = tenant("acme");
    let mut storage = LsmStorage::open(&dir).unwrap();
    activate(&mut storage, "activate-unanchored");
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-one",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    drop(storage);
    let good = std::fs::metadata(dir.join("wal")).unwrap().len();
    std::fs::OpenOptions::new()
        .append(true)
        .open(dir.join("wal"))
        .unwrap()
        .write_all(&[0x4d, 0x53, 0x4c, 0x54, 0xaa])
        .unwrap();
    let reopened = LsmStorage::open(&dir).expect("anchored prefix permits torn-tail repair");
    assert_eq!(reopened.wal_len(), good);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn activated_frontier_repairs_one_slot_and_rejects_hostile_slot_states() {
    use super::wal_frontier::test_support::{FRONTIER_BYTES, SLOT_BYTES, resign_wal_slot};

    let repair_dir = location("frontier-slot-repair");
    let mut storage = LsmStorage::open(&repair_dir).unwrap();
    activate(&mut storage, "activate-slot-repair");
    drop(storage);
    let path = repair_dir.join("wal.frontier");
    let mut bytes = std::fs::read(&path).unwrap();
    assert_eq!(bytes.len(), FRONTIER_BYTES);
    bytes[0] ^= 1;
    std::fs::write(&path, bytes).unwrap();
    drop(LsmStorage::open(&repair_dir).expect("one damaged slot repairs"));
    let mut repaired = std::fs::read(&path).unwrap();
    repaired[SLOT_BYTES] ^= 1;
    std::fs::write(&path, repaired).unwrap();
    drop(LsmStorage::open(&repair_dir).expect("the formerly damaged slot now carries repair"));

    for case in [
        "wrong-size",
        "both-damaged",
        "foreign-store",
        "unknown-schema",
        "generation-gap",
        "wrong-epoch",
    ] {
        let dir = location(case);
        let mut storage = LsmStorage::open(&dir).unwrap();
        activate(&mut storage, &format!("activate-{case}"));
        drop(storage);
        let path = dir.join("wal.frontier");
        let mut bytes = std::fs::read(&path).unwrap();
        match case {
            "wrong-size" => {
                bytes.pop();
            }
            "both-damaged" => {
                bytes[0] ^= 1;
                bytes[SLOT_BYTES] ^= 1;
            }
            "foreign-store" => {
                bytes[16..32].fill(0xa1);
                bytes[SLOT_BYTES + 16..SLOT_BYTES + 32].fill(0xa1);
                resign_wal_slot(&mut bytes, 0);
                resign_wal_slot(&mut bytes, 1);
            }
            "unknown-schema" => {
                bytes[8..10].copy_from_slice(&2_u16.to_le_bytes());
                resign_wal_slot(&mut bytes, 0);
            }
            "generation-gap" => {
                bytes[32..40].copy_from_slice(&10_u64.to_le_bytes());
                resign_wal_slot(&mut bytes, 0);
            }
            "wrong-epoch" => {
                bytes[SLOT_BYTES + 40..SLOT_BYTES + 48].copy_from_slice(&9_u64.to_le_bytes());
                resign_wal_slot(&mut bytes, 1);
            }
            _ => unreachable!(),
        }
        std::fs::write(&path, bytes).unwrap();
        assert_eq!(
            LsmStorage::open(&dir).err(),
            Some(super::LsmOpenError::FormatMismatch),
            "{case}"
        );
        std::fs::remove_dir_all(dir).unwrap();
    }
    std::fs::remove_dir_all(repair_dir).unwrap();
}

#[test]
#[allow(
    clippy::too_many_lines,
    reason = "one table-driven fault matrix validates every result frontier"
)]
fn active_result_frontier_failures_are_uncertain_or_storage_and_poison_until_reopen() {
    for fault in [
        FaultPoint::ReadyLedgerSync,
        FaultPoint::StorageFrontierSync,
        FaultPoint::ReadyFrontierSync,
    ] {
        let dir = location(&format!("active-ready-fault-{fault:?}"));
        let scope_tenant = tenant("acme");
        let mut storage = LsmStorage::open(&dir).unwrap();
        activate(&mut storage, &format!("activate-ready-{fault:?}"));
        storage.arm_test_fault(fault);
        let uncertain =
            put_intent(&scope_tenant, "op-uncertain", 0, document("orders", "order-1", "first"));
        assert_eq!(
            storage.commit(uncertain.clone()),
            CommitResult::Unknown(UnknownCause::ResponseInterrupted),
            "{fault:?}"
        );
        let wal_after_failure = std::fs::read(dir.join("wal")).unwrap();
        let manifest_after_failure = std::fs::read(dir.join("MANIFEST.active")).unwrap();
        let frontier_after_failure = std::fs::read(dir.join("wal.frontier")).unwrap();
        assert_eq!(
            storage.commit(put_intent(
                &scope_tenant,
                "op-after-poison",
                0,
                document("orders", "order-2", "second"),
            )),
            CommitResult::Rejected(CommitRejection::Storage)
        );
        assert!(
            storage
                .compact(storage::compaction::StorageCompaction {
                    policy: CompactionPolicy::Reclaim,
                    floor: None,
                    progress: None,
                })
                .is_err()
        );
        assert_eq!(std::fs::read(dir.join("wal")).unwrap(), wal_after_failure);
        assert_eq!(std::fs::read(dir.join("MANIFEST.active")).unwrap(), manifest_after_failure);
        assert_eq!(std::fs::read(dir.join("wal.frontier")).unwrap(), frontier_after_failure);
        drop(storage);

        let mut reopened = LsmStorage::open(&dir).expect("valid suffix repairs on recovery");
        let operation = OperationId::try_new("op-uncertain").unwrap();
        assert_eq!(
            reopened.resolve_operation(&scope_tenant, &operation),
            Some(OperationResolution::Committed {
                sequence: crate::types::StorageSequence::new(1),
                durability: LocalDurability::Written,
            }),
            "{fault:?}"
        );
        assert!(matches!(reopened.commit(uncertain), CommitResult::Committed(_)));
        std::fs::remove_dir_all(dir).unwrap();
    }

    let published_dir = location("active-published-frontier-fault");
    let scope_tenant = tenant("acme");
    let mut published = LsmStorage::open(&published_dir).unwrap();
    activate(&mut published, "activate-published-fault");
    published.arm_test_fault(FaultPoint::PublishedFrontierSync);
    assert_eq!(
        published.commit(put_intent(
            &scope_tenant,
            "op-published-fault",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Unknown(UnknownCause::PublishedLedgerSync)
    );
    drop(published);
    let reopened = LsmStorage::open(&published_dir).expect("flip suffix repairs");
    assert_eq!(
        reopened.resolve_operation(
            &scope_tenant,
            &OperationId::try_new("op-published-fault").unwrap(),
        ),
        Some(OperationResolution::Committed {
            sequence: crate::types::StorageSequence::new(1),
            durability: LocalDurability::Synced,
        })
    );

    let rejection_dir = location("active-rejection-frontier-fault");
    let mut rejected = LsmStorage::open(&rejection_dir).unwrap();
    assert!(matches!(
        rejected.commit(put_intent(
            &scope_tenant,
            "op-seed",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    activate(&mut rejected, "activate-rejection-fault");
    rejected.arm_test_fault(FaultPoint::RejectedFrontierSync);
    assert_eq!(
        rejected.commit(delete_intent(
            &scope_tenant,
            "op-rejected-fault",
            rejected.published_state().0.get(),
            "order-1",
            ExpectedVersion::Exact(99),
        )),
        CommitResult::Rejected(CommitRejection::Storage)
    );
    assert_eq!(
        rejected
            .resolve_operation(&scope_tenant, &OperationId::try_new("op-rejected-fault").unwrap(),),
        None
    );
    drop(rejected);
    let reopened = LsmStorage::open(&rejection_dir).expect("rejection suffix repairs");
    assert_eq!(
        reopened
            .resolve_operation(&scope_tenant, &OperationId::try_new("op-rejected-fault").unwrap(),),
        Some(OperationResolution::Rejected(CommitRejection::Conflict))
    );
    std::fs::remove_dir_all(published_dir).unwrap();
    std::fs::remove_dir_all(rejection_dir).unwrap();
}

#[test]
fn activated_checkpoint_recovers_every_manifest_epoch_truncate_and_sync_boundary() {
    for fault in [
        FaultPoint::CompactionCheckpointPublish,
        FaultPoint::CompactionCheckpointPublished,
        FaultPoint::CompactionWalFrontierFirstSlot,
        FaultPoint::CompactionWalFrontierSecondSlot,
        FaultPoint::CompactionWalTruncate,
        FaultPoint::CompactionWalSync,
    ] {
        let dir = location(&format!("active-checkpoint-{fault:?}"));
        let scope_tenant = tenant("acme");
        let mut storage = LsmStorage::open(&dir).unwrap();
        activate(&mut storage, &format!("activate-checkpoint-{fault:?}"));
        assert!(matches!(
            storage.commit(put_intent(
                &scope_tenant,
                "op-checkpoint",
                0,
                document("orders", "order-1", "first"),
            )),
            CommitResult::Committed(_)
        ));
        storage.arm_test_fault(fault);
        assert!(
            storage
                .compact(storage::compaction::StorageCompaction {
                    policy: CompactionPolicy::Reclaim,
                    floor: None,
                    progress: None,
                })
                .is_err(),
            "{fault:?}"
        );
        drop(storage);

        let mut reopened = LsmStorage::open(&dir)
            .unwrap_or_else(|error| panic!("{fault:?} must recover, got {error:?}"));
        let state = reopened.published_state().0;
        assert!(
            reopened
                .read_document_in_collection_at(
                    state,
                    &scope_tenant,
                    &CollectionName::try_new("orders").unwrap(),
                    &DocumentId::try_new("order-1").unwrap(),
                )
                .is_some(),
            "{fault:?}"
        );
        assert!(matches!(
            reopened
                .resolve_operation(&scope_tenant, &OperationId::try_new("op-checkpoint").unwrap(),),
            Some(OperationResolution::Committed { .. })
        ));
        assert!(matches!(
            reopened.commit(put_intent(
                &scope_tenant,
                "op-after-recovery",
                state.get(),
                document("orders", "order-2", "second"),
            )),
            CommitResult::Committed(_)
        ));
        drop(reopened);
        drop(LsmStorage::open(&dir).expect("the next-epoch append reopens"));
        std::fs::remove_dir_all(dir).unwrap();
    }
}

#[test]
fn activated_checkpoint_requires_its_manifest_and_every_named_segment() {
    for damage in ["manifest-missing", "manifest-corrupt", "segment-missing", "segment-corrupt"] {
        let dir = location(damage);
        let scope_tenant = tenant("acme");
        let mut storage = LsmStorage::open(&dir).unwrap();
        activate(&mut storage, &format!("activate-{damage}"));
        assert!(matches!(
            storage.commit(put_intent(
                &scope_tenant,
                "op-checkpoint",
                0,
                document("orders", "order-1", "first"),
            )),
            CommitResult::Committed(_)
        ));
        storage
            .compact(storage::compaction::StorageCompaction {
                policy: CompactionPolicy::Reclaim,
                floor: None,
                progress: None,
            })
            .expect("checkpoint completes");
        drop(storage);
        let manifest_path = dir.join("MANIFEST.active");
        if damage == "manifest-missing" {
            std::fs::remove_file(&manifest_path).unwrap();
        } else if damage == "manifest-corrupt" {
            let mut bytes = std::fs::read(&manifest_path).unwrap();
            let middle = bytes.len() / 2;
            bytes[middle] ^= 1;
            std::fs::write(&manifest_path, bytes).unwrap();
        } else {
            let segment = std::fs::read_dir(&dir)
                .unwrap()
                .filter_map(Result::ok)
                .map(|entry| entry.path())
                .find(|path| {
                    path.file_name().and_then(|name| name.to_str()).is_some_and(|name| {
                        name.starts_with("seg-")
                            && std::path::Path::new(name)
                                .extension()
                                .is_some_and(|extension| extension == "lsm")
                    })
                })
                .expect("checkpoint names a segment");
            if damage == "segment-missing" {
                std::fs::remove_file(segment).unwrap();
            } else {
                let mut bytes = std::fs::read(&segment).unwrap();
                bytes[24] ^= 1;
                std::fs::write(segment, bytes).unwrap();
            }
        }
        assert_eq!(
            LsmStorage::open(&dir).err(),
            Some(super::LsmOpenError::FormatMismatch),
            "{damage}"
        );
        std::fs::remove_dir_all(dir).unwrap();
    }
}

#[test]
fn legacy_checkpoint_resets_its_disposable_shadow_before_later_writes() {
    let dir = location("legacy-checkpoint-shadow-reset");
    let scope_tenant = tenant("acme");
    let mut storage = LsmStorage::open(&dir).unwrap();
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-before-checkpoint",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    storage
        .compact(storage::compaction::StorageCompaction {
            policy: CompactionPolicy::Reclaim,
            floor: None,
            progress: None,
        })
        .expect("legacy checkpoint completes");
    assert!(!storage.frontier_poisoned, "successful reset keeps the shadow usable");
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-after-checkpoint",
            storage.published_state().0.get(),
            document("orders", "order-2", "second"),
        )),
        CommitResult::Committed(_)
    ));
    drop(storage);
    let reopened = LsmStorage::open(&dir).expect("post-checkpoint WAL reopens");
    assert_eq!(reopened.documents_at(reopened.published_state().0, &scope_tenant).len(), 2);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn legacy_truncate_sync_failure_resets_shadow_for_same_handle_retry() {
    let dir = location("legacy-checkpoint-sync-failure");
    let scope_tenant = tenant("acme");
    let mut storage = LsmStorage::open(&dir).unwrap();
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-before-checkpoint",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    storage.arm_test_fault(FaultPoint::CompactionWalSync);
    assert!(
        storage
            .compact(storage::compaction::StorageCompaction {
                policy: CompactionPolicy::Reclaim,
                floor: None,
                progress: None,
            })
            .is_err()
    );
    assert!(!storage.frontier_poisoned);
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-after-failed-checkpoint",
            storage.published_state().0.get(),
            document("orders", "order-2", "second"),
        )),
        CommitResult::Committed(_)
    ));
    assert!(storage.flush().is_ok());
    drop(storage);
    let reopened = LsmStorage::open(&dir).expect("same-handle retry reopens");
    assert_eq!(reopened.documents_at(reopened.published_state().0, &scope_tenant).len(), 2);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn legacy_shadow_reset_failure_is_reported_by_compaction_and_recovers_on_reopen() {
    let dir = location("legacy-shadow-reset-failure");
    let scope_tenant = tenant("acme");
    let mut storage = LsmStorage::open(&dir).unwrap();
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-before-reset-failure",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    storage.arm_test_fault(FaultPoint::CompactionLegacyFrontierReset);
    assert!(
        storage
            .compact(storage::compaction::StorageCompaction {
                policy: CompactionPolicy::Reclaim,
                floor: None,
                progress: None,
            })
            .is_err(),
        "a failed shadow reset cannot return a successful compaction receipt"
    );
    assert!(storage.frontier_poisoned);
    drop(storage);
    let reopened = LsmStorage::open(&dir).expect("legacy reopen rebuilds its disposable shadow");
    assert_eq!(reopened.documents_at(reopened.published_state().0, &scope_tenant).len(), 1);
    assert!(!matches!(reopened.ledger_frontier_status(), LedgerFrontierStatus::Unavailable));
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn activated_retained_frontier_rejects_exact_fact_and_bound_loss_and_repairs_one_slot() {
    for loss in ["bound", "fact-and-bound"] {
        let dir = location(&format!("retained-loss-{loss}"));
        let scope_tenant = tenant("acme");
        let mut storage = LsmStorage::open(&dir).unwrap();
        activate(&mut storage, &format!("activate-retained-{loss}"));
        assert!(matches!(
            storage.commit(put_intent(
                &scope_tenant,
                "op-put",
                0,
                document("orders", "order-1", "first"),
            )),
            CommitResult::Committed(_)
        ));
        assert!(matches!(
            storage.commit(delete_intent(
                &scope_tenant,
                "op-delete",
                storage.published_state().0.get(),
                "order-1",
                ExpectedVersion::Exact(1),
            )),
            CommitResult::Committed(_)
        ));
        assert!(matches!(
            storage.commit(put_intent(
                &scope_tenant,
                "op-head",
                storage.published_state().0.get(),
                document("orders", "order-2", "second"),
            )),
            CommitResult::Committed(_)
        ));
        storage
            .compact(CompactionRequest::new(CompactionPolicy::ReclaimHistory).lower(None))
            .expect("active history reclaim completes");
        drop(storage);
        let retained_path = dir.join("retained");
        let scan = crate::storage::retained::RetainedIndex::scan(&retained_path);
        assert!(scan.frame_ends().len() >= 2);
        let truncate_to =
            if loss == "bound" { scan.frame_ends()[scan.frame_ends().len() - 2] } else { 0 };
        let mut bytes = std::fs::read(&retained_path).unwrap();
        bytes.truncate(usize::try_from(truncate_to).unwrap());
        std::fs::write(&retained_path, &bytes).unwrap();
        assert_eq!(
            LsmStorage::open(&dir).err(),
            Some(super::LsmOpenError::FormatMismatch),
            "{loss}"
        );
        assert_eq!(std::fs::read(&retained_path).unwrap(), bytes);
        std::fs::remove_dir_all(dir).unwrap();
    }

    let repair_dir = location("retained-slot-repair");
    let mut storage = LsmStorage::open(&repair_dir).unwrap();
    activate(&mut storage, "activate-retained-slot");
    drop(storage);
    let frontier_path = repair_dir.join("retained.frontier");
    let mut bytes = std::fs::read(&frontier_path).unwrap();
    bytes[0] ^= 1;
    std::fs::write(&frontier_path, bytes).unwrap();
    drop(LsmStorage::open(&repair_dir).expect("one retained slot repairs"));
    let mut bytes = std::fs::read(&frontier_path).unwrap();
    bytes[0] ^= 1;
    bytes[super::wal_frontier::SLOT_BYTES] ^= 1;
    std::fs::write(&frontier_path, bytes).unwrap();
    assert_eq!(LsmStorage::open(&repair_dir).err(), Some(super::LsmOpenError::FormatMismatch));
    std::fs::remove_dir_all(repair_dir).unwrap();
}

#[test]
fn retained_frontier_failure_stops_before_history_floor_and_retry_finishes_after_reopen() {
    let dir = location("retained-frontier-failure");
    let scope_tenant = tenant("acme");
    let mut storage = LsmStorage::open(&dir).unwrap();
    activate(&mut storage, "activate-retained-failure");
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-put",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    assert!(matches!(
        storage.commit(delete_intent(
            &scope_tenant,
            "op-delete",
            storage.published_state().0.get(),
            "order-1",
            ExpectedVersion::Exact(1),
        )),
        CommitResult::Committed(_)
    ));
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-head",
            storage.published_state().0.get(),
            document("orders", "order-2", "second"),
        )),
        CommitResult::Committed(_)
    ));
    storage.arm_test_fault(FaultPoint::RetainedFrontierSync);
    assert!(
        storage
            .compact(CompactionRequest::new(CompactionPolicy::ReclaimHistory).lower(None))
            .is_err()
    );
    assert_eq!(storage.servable_history_start(), Some(crate::types::StorageSequence::new(1)));
    drop(storage);

    let mut reopened = LsmStorage::open(&dir).expect("retained suffix repairs");
    assert_eq!(reopened.servable_history_start(), Some(crate::types::StorageSequence::new(1)));
    assert!(reopened.retained_state().bound.is_some(), "evidence survived but floor did not move");
    reopened
        .compact(CompactionRequest::new(CompactionPolicy::ReclaimHistory).lower(None))
        .expect("retry declares the floor after the frontier is witnessed");
    assert!(reopened.servable_history_start().is_some_and(|floor| floor.get() > 1));
    drop(reopened);
    drop(LsmStorage::open(&dir).expect("completed reclaim reopens"));
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn buffered_conflict_resolution_survives_an_activated_checkpoint() {
    let dir = location("buffered-conflict-checkpoint");
    let scope_tenant = tenant("acme");
    let mut storage = LsmStorage::open(&dir).unwrap();
    assert!(matches!(
        storage.commit(put_intent(
            &scope_tenant,
            "op-seed",
            0,
            document("orders", "order-1", "first"),
        )),
        CommitResult::Committed(_)
    ));
    activate(&mut storage, "activate-buffered-conflict");
    let mut conflict = delete_intent(
        &scope_tenant,
        "op-buffered-conflict",
        storage.published_state().0.get(),
        "order-1",
        ExpectedVersion::Exact(99),
    );
    conflict.durability = LocalDurability::Buffered;
    assert_eq!(storage.commit(conflict), CommitResult::Rejected(CommitRejection::Conflict));
    storage
        .compact(storage::compaction::StorageCompaction {
            policy: CompactionPolicy::Reclaim,
            floor: None,
            progress: None,
        })
        .expect("checkpoint persists the rejection entry");
    drop(storage);
    let reopened = LsmStorage::open(&dir).expect("checkpoint reopens");
    assert_eq!(
        reopened.resolve_operation(
            &scope_tenant,
            &OperationId::try_new("op-buffered-conflict").unwrap(),
        ),
        Some(OperationResolution::Rejected(CommitRejection::Conflict))
    );
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn commit_reads_back_and_survives_reopen() {
    let dir = location("reopen");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let result =
        engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    assert!(matches!(result, CommitResult::Committed(_)));
    assert!(engine.usage_facts_impl().is_empty(), "ordinary mutations mint no billing fact");
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    let found = reopened.read_document_in_collection_at(
        reopened.published_state().0,
        &scope_tenant,
        &CollectionName::try_new("orders").expect("valid collection"),
        &DocumentId::try_new("order-1").expect("valid document"),
    );
    assert!(found.is_some(), "the committed document survives a reopen");
    let document = found.unwrap();
    assert_eq!(document.metadata().version(), 1);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn accepted_mutation_usage_is_atomic_idempotent_and_recoverable() {
    let dir = location("usage-atomic");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let mut intent =
        put_intent(&scope_tenant, "op-usage", 0, document("orders", "order-1", "payload"));
    intent.usage_admission = Some(usage_admission("op-usage", 1_700_000_000_000));
    let first = engine.commit(intent.clone());
    assert!(matches!(first, CommitResult::Committed(_)));
    assert_eq!(engine.usage_facts_impl().len(), 1);
    let fact = engine.usage_facts_impl().pop().expect("fact");
    assert_eq!(fact.sequence(), 1);
    assert_eq!(fact.fact().scope().tenant_id(), &scope_tenant);
    assert_eq!(fact.fact().identity().id().as_str(), "op-usage");
    assert_eq!(engine.commit(intent), first, "retry returns the same commit and does not mint");
    assert_eq!(engine.usage_facts_impl().len(), 1);
    drop(engine);

    let reopened = LsmStorage::open(&dir).unwrap();
    assert_eq!(reopened.usage_facts_impl().len(), 1, "the fact is WAL durable with the mutation");
    assert_eq!(reopened.usage_facts_impl()[0], fact);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn mutation_usage_rejects_untrusted_identity_durability_and_context_reuse() {
    let dir = location("usage-mutation-validation");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();

    let mut mismatched =
        put_intent(&scope_tenant, "op-mismatch", 0, document("orders", "order-1", "payload"));
    mismatched.usage_admission = Some(usage_admission("another-request", 1_700_000_000_010));
    assert_eq!(engine.commit(mismatched), CommitResult::Rejected(CommitRejection::Validation));

    let mut unsynced =
        put_intent(&scope_tenant, "op-unsynced", 0, document("orders", "order-2", "payload"));
    unsynced.durability = LocalDurability::Written;
    unsynced.usage_admission = Some(usage_admission("op-unsynced", 1_700_000_000_011));
    assert_eq!(engine.commit(unsynced), CommitResult::Rejected(CommitRejection::Validation));

    let mut accepted =
        put_intent(&scope_tenant, "op-bound", 0, document("orders", "order-3", "payload"));
    accepted.usage_admission = Some(usage_admission("op-bound", 1_700_000_000_012));
    assert!(matches!(engine.commit(accepted.clone()), CommitResult::Committed(_)));
    let mut changed = accepted;
    changed.digest ^= 1;
    changed.usage_admission = Some(usage_admission("op-bound", 1_700_000_000_013));
    assert_eq!(engine.commit(changed), CommitResult::Rejected(CommitRejection::OperationIdReuse));
    assert_eq!(engine.usage_facts_impl().len(), 1);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn standalone_usage_is_synced_idempotent_scoped_and_pageable_after_reopen() {
    let dir = location("usage-standalone");
    let tenant_a = tenant("tenant-a");
    let tenant_b = tenant("tenant-b");
    let mut engine = LsmStorage::open(&dir).unwrap();

    let first_fact = usage_record_fact(&tenant_a, "shared-fact", 1_000, 10);
    let (first, replayed) = engine.record_usage(&tenant_a, first_fact.clone()).unwrap();
    assert!(!replayed);
    assert_eq!(first.sequence(), 1);
    let (replay, replayed) = engine.record_usage(&tenant_a, first_fact).unwrap();
    assert!(replayed);
    assert_eq!(replay, first);
    assert_eq!(
        engine.record_usage(&tenant_a, usage_record_fact(&tenant_a, "shared-fact", 1_000, 11)),
        Err(UsageStorageError::IdentityConflict)
    );

    let second = engine
        .record_usage(&tenant_a, usage_record_fact(&tenant_a, "later-fact", 2_000, 20))
        .unwrap()
        .0;
    let other_tenant = engine
        .record_usage(&tenant_b, usage_record_fact(&tenant_b, "shared-fact", 1_000, 10))
        .unwrap()
        .0;
    assert_ne!(other_tenant.sequence(), first.sequence());

    let snapshot = engine.published_state().0;
    let page_one = engine.usage_facts_page(&tenant_a, snapshot, None, None, 1).expect("first page");
    assert_eq!(page_one, vec![first.clone()]);
    let after = UsageFactPosition::of(&page_one[0]);
    let page_two =
        engine.usage_facts_page(&tenant_a, snapshot, Some(&after), None, 2).expect("second page");
    assert_eq!(page_two, vec![second.clone()]);
    let window = MeasurementWindow::interval(1_500, 2_500).unwrap();
    assert_eq!(
        engine.usage_facts_page(&tenant_a, snapshot, None, Some(window), 10).unwrap(),
        vec![second.clone()]
    );
    drop(engine);

    let reopened = LsmStorage::open(&dir).unwrap();
    assert_eq!(
        reopened.usage_facts_page(&tenant_a, snapshot, None, None, 10).unwrap(),
        vec![first, second]
    );
    assert_eq!(
        reopened.usage_facts_page(&tenant_b, snapshot, None, None, 10).unwrap(),
        vec![other_tenant]
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn legacy_usage_entries_disable_billing_without_blocking_location_open() {
    let dir = location("usage-legacy-unavailable");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let entry = super::internal::Entry {
        key: super::internal::InternalKey::meta("usage/00000000000000000001:old", 1),
        value: b"obsolete-nonauthoritative-fact".to_vec(),
    };
    let append = engine.append_wal_batch(std::slice::from_ref(&entry)).unwrap();
    engine.witness_wal_append(&append, true, FaultPoint::ReadyFrontierSync).unwrap();
    drop(engine);

    let reopened = LsmStorage::open(&dir).expect("legacy usage does not hide documents");
    assert_eq!(
        reopened.usage_facts_page(&scope_tenant, reopened.published_state().0, None, None, 10,),
        Err(UsageStorageError::UsageUnavailable)
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn refused_or_partial_mutation_mints_no_usage_fact() {
    let dir = location("usage-refused");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let invalid = CommitIntent {
        tenant: scope_tenant.clone(),
        operation: OperationId::try_new("op-invalid").unwrap(),
        digest: 1,
        snapshot_sequence: crate::types::StorageSequence::new(0),
        durability: LocalDurability::Synced,
        usage_admission: None,
        writes: Vec::new(),
    };
    assert!(matches!(engine.commit(invalid), CommitResult::Rejected(_)));
    assert!(engine.usage_facts_impl().is_empty());

    engine.arm_test_fault(crate::storage::FaultPoint::WalAppend);
    let mut partial =
        put_intent(&scope_tenant, "op-partial", 0, document("orders", "order-2", "payload"));
    partial.usage_admission = Some(usage_admission("op-partial", 1_700_000_000_001));
    assert!(matches!(engine.commit(partial), CommitResult::Rejected(_)));
    assert!(engine.usage_facts_impl().is_empty());
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    assert!(reopened.usage_facts_impl().is_empty());
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn sequence_overflow_refuses_without_minting_or_advancing() {
    let dir = location("usage-sequence-overflow");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.sequence_watermark = u64::MAX;
    let mut intent = put_intent(
        &scope_tenant,
        "op-overflow",
        u64::MAX,
        document("orders", "order-1", "payload"),
    );
    intent.usage_admission = Some(usage_admission("op-overflow", 1_700_000_000_002));
    assert!(matches!(engine.commit(intent), CommitResult::Rejected(_)));
    assert_eq!(engine.sequence_watermark, u64::MAX);
    assert!(engine.usage_facts_impl().is_empty());
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn durable_bytes_per_write_are_bounded_by_the_write_set() {
    let dir = location("amplification");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    // Seed enough documents that a full-root store would amplify: the
    // baseline run rewrote ~300 KB per ~300-byte write.
    let count: u64 = 64;
    for index in 0..count {
        let result = engine.commit(put_intent(
            &scope_tenant,
            &format!("seed-{index}"),
            0,
            document("orders", &format!("order-{index:04}"), &"x".repeat(300)),
        ));
        assert!(matches!(result, CommitResult::Committed(_)));
    }
    let wal_bytes = engine.wal_len();
    // The frame count equals the commit count; each frame carries its
    // document plus the ledger and history records. The bound: fewer than
    // 3 KiB of durable bytes per ~300-byte document, versus the ~300 KiB
    // full-root baseline — orders of magnitude, by construction.
    let per_write = wal_bytes / count;
    assert!(
        per_write < 3 * 1024,
        "durable bytes per write must be bounded by the write set, saw {per_write}"
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn idempotent_retry_replays_the_recorded_outcome() {
    let dir = location("idempotent");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let intent = put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first"));
    let first = engine.commit(intent.clone());
    let second = engine.commit(intent);
    match (first, second) {
        (CommitResult::Committed(a), CommitResult::Committed(b)) => {
            assert_eq!(a.sequence, b.sequence, "the retry resolves to the recorded outcome");
            assert_eq!(a.root, b.root);
        }
        other => panic!("both attempts must commit: {other:?}"),
    }
    let mut tampered =
        put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "different"));
    tampered.digest = tampered.digest.wrapping_add(1);
    assert!(matches!(
        engine.commit(tampered),
        CommitResult::Rejected(storage::intent::CommitRejection::OperationIdReuse)
    ));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn recovery_selects_root_head_by_sequence_not_operation_key() {
    let dir = location("root-head-sequence");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();

    // The first operation sorts after the second one, while its publication
    // sequence is lower. Recovery must follow sequence order, not the ledger
    // map's (tenant, operation) key order.
    let first =
        engine.commit(put_intent(&scope_tenant, "op-z", 0, document("orders", "order-1", "first")));
    let first = match first {
        CommitResult::Committed(receipt) => receipt,
        other => panic!("first commit must succeed: {other:?}"),
    };
    let second = engine.commit(put_intent(
        &scope_tenant,
        "op-a",
        first.sequence.get(),
        document("orders", "order-2", "second"),
    ));
    let second = match second {
        CommitResult::Committed(receipt) => receipt,
        other => panic!("second commit must succeed: {other:?}"),
    };
    drop(engine);

    let reopened = LsmStorage::open(&dir).unwrap();
    assert_eq!(
        reopened.published_state().1,
        second.root,
        "recovery must restore the root from the latest committed sequence"
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn synced_durability_flip_survives_compaction_and_reopen() {
    let dir = location("synced-flip-reopen");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    let operation = OperationId::try_new("op-synced").expect("valid operation");

    let result = engine.commit(put_intent(
        &scope_tenant,
        operation.as_str(),
        0,
        document("orders", "order-1", "synced"),
    ));
    assert!(matches!(result, CommitResult::Committed(_)));
    assert!(matches!(
        engine.resolve_operation(&scope_tenant, &operation),
        Some(crate::storage::intent::OperationResolution::Committed {
            durability: LocalDurability::Synced,
            ..
        })
    ));

    engine
        .compact(storage::compaction::StorageCompaction {
            policy: CompactionPolicy::Reclaim,
            floor: None,
            progress: None,
        })
        .expect("compaction completes");
    drop(engine);

    let reopened = LsmStorage::open(&dir).unwrap();
    assert!(matches!(
        reopened.resolve_operation(&scope_tenant, &operation),
        Some(crate::storage::intent::OperationResolution::Committed {
            durability: LocalDurability::Synced,
            ..
        })
    ));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn delete_then_read_answers_absent_across_reopen() {
    let dir = location("delete");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    let published = engine.published_state().0;
    let result = engine.commit(delete_intent(
        &scope_tenant,
        "op-2",
        published.get(),
        "order-1",
        ExpectedVersion::Exact(1),
    ));
    assert!(matches!(result, CommitResult::Committed(_)));
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    let state = reopened.published_state().0;
    assert!(
        reopened
            .read_document_in_collection_at(
                state,
                &scope_tenant,
                &CollectionName::try_new("orders").expect("valid collection"),
                &DocumentId::try_new("order-1").expect("valid document"),
            )
            .is_none()
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn mvcc_snapshot_reads_isolate_concurrent_writers() {
    let dir = location("mvcc");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    let snapshot = engine.published_state().0;
    engine.commit(put_intent(
        &scope_tenant,
        "op-2",
        snapshot.get(),
        document("orders", "order-1", "second"),
    ));
    let at_snapshot = engine
        .read_document_in_collection_at(
            snapshot,
            &scope_tenant,
            &CollectionName::try_new("orders").expect("valid collection"),
            &DocumentId::try_new("order-1").expect("valid document"),
        )
        .expect("the snapshot still observes the first write");
    assert_eq!(
        at_snapshot.metadata().version(),
        1,
        "a snapshot read sees the state at its sequence"
    );
    let current = engine
        .read_document_in_collection_at(
            engine.published_state().0,
            &scope_tenant,
            &CollectionName::try_new("orders").expect("valid collection"),
            &DocumentId::try_new("order-1").expect("valid document"),
        )
        .unwrap();
    assert_eq!(current.metadata().version(), 2);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn rotation_flush_and_manifest_recovery_round_trip() {
    let dir = location("rotation");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.tuning.max_memtable_entries = 8;
    engine.tuning.max_memtable_bytes = usize::MAX;
    for index in 0..64_u64 {
        let result = engine.commit(put_intent(
            &scope_tenant,
            &format!("op-{index}"),
            0,
            document("orders", &format!("order-{index:04}"), "payload"),
        ));
        assert!(matches!(result, CommitResult::Committed(_)));
    }
    assert!(
        engine.segment_count_at_level(0) > 0,
        "rotation flushed frozen memtables into segments"
    );
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    let state = reopened.published_state().0;
    let documents = reopened.documents_at(state, &scope_tenant);
    assert_eq!(documents.len(), 64, "every document survives the manifest recovery");
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn expected_version_conflict_is_recorded_and_idempotent() {
    let dir = location("conflict");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    let published = engine.published_state().0;
    let stale = engine.commit(delete_intent(
        &scope_tenant,
        "op-stale",
        0,
        "order-1",
        ExpectedVersion::Exact(99),
    ));
    assert!(matches!(stale, CommitResult::Rejected(storage::intent::CommitRejection::Conflict)));
    let retried = engine.commit(delete_intent(
        &scope_tenant,
        "op-stale",
        0,
        "order-1",
        ExpectedVersion::Exact(99),
    ));
    assert!(matches!(retried, CommitResult::Rejected(storage::intent::CommitRejection::Conflict)));
    let still_present = engine.read_document_in_collection_at(
        published,
        &scope_tenant,
        &CollectionName::try_new("orders").expect("valid collection"),
        &DocumentId::try_new("order-1").expect("valid document"),
    );
    assert!(still_present.is_some());
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn compaction_checkpoint_truncates_the_wal_and_keeps_data() {
    let dir = location("compact");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    for index in 0..16_u64 {
        engine.commit(put_intent(
            &scope_tenant,
            &format!("op-{index}"),
            0,
            document("orders", &format!("order-{index:04}"), "payload"),
        ));
    }
    let wal_before = engine.wal_len();
    let (receipt, facts) = engine
        .compact(storage::compaction::StorageCompaction {
            policy: CompactionPolicy::Reclaim,
            floor: None,
            progress: None,
        })
        .expect("the compaction pass completes");
    assert_eq!(receipt.wal_bytes_before(), wal_before);
    assert!(receipt.wal_bytes_after() < wal_before, "the checkpoint truncates the WAL");
    assert!(receipt.checkpoint_sequence().is_some());
    assert_eq!(facts.bound, None, "Reclaim does not touch history");
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    let state = reopened.published_state().0;
    assert_eq!(reopened.documents_at(state, &scope_tenant).len(), 16);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn history_reclaim_extracts_evidence_and_declares_the_bound() {
    let dir = location("reclaim");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    let published = engine.published_state().0;
    engine.commit(delete_intent(
        &scope_tenant,
        "op-2",
        published.get(),
        "order-1",
        ExpectedVersion::Exact(1),
    ));
    let after_delete = engine.published_state().0;
    engine.commit(put_intent(
        &scope_tenant,
        "op-3",
        after_delete.get(),
        document("orders", "order-2", "second"),
    ));
    let request = CompactionRequest::new(CompactionPolicy::ReclaimHistory);
    let lowered = request.lower(None);
    let (receipt, facts) = engine.compact(lowered).expect("the pass completes");
    assert!(matches!(
        receipt.history_reclaim(),
        crate::storage::compaction::HistoryReclaimOutcome::Reclaimed { .. }
    ));
    assert!(receipt.evidence_extracted() >= 1, "the delete produced retained evidence");
    assert!(facts.bound.is_some());
    let state = engine.retained_state();
    assert!(state.bound.is_some(), "the bound is durably declared");
    assert!(!state.facts.is_empty(), "the deletion fact is retained");
    drop(engine);
    let reopened = LsmStorage::open(&dir).unwrap();
    assert!(reopened.servable_history_start().is_some(), "the floor survives the reopen");
    assert_eq!(
        reopened.tenant_commits(&scope_tenant, crate::types::StorageSequence::new(0), 64).len(),
        1,
        "history below the bound is not served"
    );
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn torn_wal_tail_truncates_and_keeps_verified_commits() {
    let dir = location("torn");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    engine.commit(put_intent(&scope_tenant, "op-1", 0, document("orders", "order-1", "first")));
    drop(engine);
    // Simulate the torn tail of an interrupted append: a real frame's
    // little-endian magic followed by a cut header.
    let wal_path = dir.join("wal");
    let mut bytes = std::fs::read(&wal_path).unwrap();
    bytes.extend_from_slice(&[0x4D, 0x53, 0x4C, 0x54, 0x00]);
    std::fs::write(&wal_path, &bytes).unwrap();
    let mut reopened = LsmStorage::open(&dir).unwrap();
    let state = reopened.published_state().0;
    assert!(
        reopened
            .read_document_in_collection_at(
                state,
                &scope_tenant,
                &CollectionName::try_new("orders").expect("valid collection"),
                &DocumentId::try_new("order-1").expect("valid document"),
            )
            .is_some()
    );
    // The next commit lands and reopens cleanly past the truncation.
    let result = reopened.commit(put_intent(
        &scope_tenant,
        "op-2",
        state.get(),
        document("orders", "order-2", "second"),
    ));
    assert!(matches!(result, CommitResult::Committed(_)));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn foreign_directory_fails_closed() {
    let dir = location("foreign");
    std::fs::write(dir.join("unexpected"), b"not a terrabase location").unwrap();
    assert!(matches!(
        LsmStorage::open(&dir),
        Err(crate::storage::lsm::LsmOpenError::FormatMismatch)
    ));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn engine_opens_on_the_lsm_backend_and_serves_documents() {
    let dir = location("engine");
    let config = EngineConfig::durable(&dir)
        .unwrap()
        .with_storage_backend(crate::engine::StorageBackend::Lsm);
    let engine = Engine::open(config).unwrap();
    drop(engine);
    // The marker identifies the location as an LSM location.
    let marker = std::fs::read_to_string(dir.join("format")).unwrap();
    assert_eq!(marker.trim(), "TBV2LSM1");
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn demoted_documents_are_read_from_cold_tier_after_reopen() {
    let dir = location("tier-read-reopen");
    let scope_tenant = tenant("acme");
    let mut engine = LsmStorage::open(&dir).unwrap();
    crate::storage::lsm::tests_support::seed_segments(&mut engine, 8);
    let before = engine.documents_at(engine.published_state().0, &scope_tenant);
    assert_eq!(before.len(), 32);
    let mut policy = crate::storage::lsm::tier::TierMovePolicy::default_policy();
    policy.nvme_budget_bytes = 1;
    let outcome = engine.mover_pass(&policy, "home-a").expect("LSM tier mover");
    assert!(matches!(outcome, crate::storage::lsm::tier::MoverOutcome::Completed(_)));
    drop(engine);

    let reopened = LsmStorage::open(&dir).unwrap();
    let after = reopened.documents_at(reopened.published_state().0, &scope_tenant);
    assert_eq!(after.len(), before.len(), "demotion must not silently omit documents");
    assert!(after.iter().any(|document| document.id().as_str() == "order-0031"));
    let collection = CollectionName::try_new("orders").unwrap();
    let id = DocumentId::try_new("order-0031").unwrap();
    assert!(
        reopened
            .read_document_in_collection_at(
                reopened.published_state().0,
                &scope_tenant,
                &collection,
                &id
            )
            .is_some()
    );
    std::fs::remove_dir_all(&dir).unwrap();
}
