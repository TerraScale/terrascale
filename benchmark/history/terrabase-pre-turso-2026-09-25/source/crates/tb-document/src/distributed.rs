//! Certified, catalog-only open for a distributed document shard.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex, OnceLock};

use sha2::{Digest, Sha256};
use tb_core::{
    AdmissionError, Attribution, ItemReservation, KeyBound, KeyRange, MemoryBudget, MemoryCategory,
    ReadSnapshot, Reservation, TransactionId,
};
use tb_format::{
    DocumentKey, decode_document_key, decode_state_pointer, decode_state_with_limits,
    encode_catalog_key,
};
use tb_replication::{
    AcceptedAuthority, AuthorityVersion, CertifiedShardView, ReplicaReceiver, ReplicationLimits,
};
use tb_storage::{
    DistributedDocumentMode, Mutation, ScanRow, Storage, StorageConfig, StorageError,
};

use crate::distributed_state::audit_distributed;
use crate::{DocumentError, DocumentStoreConfig, key};

const SCAN_PAGE_ROWS: usize = 256;
const WAL_HEADER_AND_TRAILER: usize = 60;
const WAL_PUT_OVERHEAD: usize = 9;
const MUTABLE_ENTRY_OVERHEAD: usize = 128;
const DIRECTORY_FIXED_BYTES: usize = 16;
const DIRECTORY_ENTRY_FIXED_BYTES: usize = 24;
const MAX_INSTALL_RETRY_MS: u64 = 60_000;
const INSTALL_NONCE_DOMAIN: &[u8] = b"TBDOC-C0-INSTALL-v1\0";

/// A certified, durable C0 catalog with document source and receiver methods.
/// Opening it alone does not establish receiver readiness or authorize an ACK.
pub struct DistributedDocumentStore {
    pub(crate) storage: Storage,
    pub(crate) config: DocumentStoreConfig,
    pub(crate) budget: MemoryBudget,
    pub(crate) writer: Mutex<DistributedWriterState>,
    pub(crate) authority: Arc<AcceptedAuthority>,
    pub(crate) inboxes: OnceLock<BTreeMap<tb_core::OriginId, Mutex<ReplicaReceiver>>>,
}

pub(crate) struct DistributedWriterState {
    pub(crate) local_sequence: u64,
    pub(crate) applied_prefix: BTreeMap<tb_core::OriginId, u64>,
    pub(crate) inboxes_reconciled: bool,
    pub(crate) poisoned: bool,
}

/// Open the authority-bound document shard, installing only missing signed C0
/// definitions. Catalog readiness does not imply document or applied readiness.
///
/// # Errors
/// Rejects a mismatched or v1 authority, any visible non-certified row, or an
/// installation that cannot be admitted and durably committed.
pub fn open_distributed(
    authority: Arc<AcceptedAuthority>,
    config: DocumentStoreConfig,
    budget: MemoryBudget,
) -> Result<DistributedDocumentStore, DocumentError> {
    config.replication.validate()?;
    let view = authority.view();
    validate_binding(view, &config)?;
    let mode = DistributedDocumentMode::new(
        view.cluster(),
        view.tenant(),
        view.database(),
        view.shard(),
        view.epoch(),
        view.configuration().to_bytes(),
        view.local_origin(),
    );
    let storage = Storage::open_distributed_document(config.storage.clone(), budget.clone(), mode)?;
    let initial = storage.snapshot();
    audit_distributed(
        &storage,
        &config.storage,
        &budget,
        view,
        config.replication,
        initial,
        false,
    )?;
    install_missing_catalogs(&storage, &config.storage, &budget, view, initial)?;
    let audited = audit_distributed(
        &storage,
        &config.storage,
        &budget,
        view,
        config.replication,
        storage.snapshot(),
        true,
    )?;
    Ok(DistributedDocumentStore {
        storage,
        config,
        budget,
        writer: Mutex::new(DistributedWriterState {
            local_sequence: audited.local_sequence,
            applied_prefix: audited.applied_prefix,
            inboxes_reconciled: false,
            poisoned: false,
        }),
        authority,
        inboxes: OnceLock::new(),
    })
}

fn validate_binding(
    view: &CertifiedShardView,
    config: &DocumentStoreConfig,
) -> Result<(), DocumentError> {
    if view.version() != AuthorityVersion::V2 {
        return Err(DocumentError::Corrupt(
            "distributed documents require v2 authority",
        ));
    }
    if config.storage.path != view.documents_path()
        || config.storage.tenant != view.tenant()
        || config.database != view.database()
        || config.shard != view.shard()
        || config.epoch != view.epoch()
        || config.configuration != view.configuration()
        || config.origin != view.local_origin()
    {
        return Err(DocumentError::Corrupt(
            "distributed document binding mismatch",
        ));
    }
    for (binding, _) in view.catalogs() {
        view.checked_active_catalog(binding.collection)
            .map_err(|_| DocumentError::Corrupt("signed active catalog missing"))?;
    }
    Ok(())
}

fn attribution(config: &StorageConfig) -> Attribution {
    Attribution {
        tenant: config.tenant,
        worker: config.worker,
        category: MemoryCategory::Query,
    }
}

pub(crate) fn scan_all_visible(
    storage: &Storage,
    config: &StorageConfig,
    budget: &MemoryBudget,
    snapshot: ReadSnapshot,
    mut inspect: impl FnMut(&ScanRow) -> Result<(), DocumentError>,
) -> Result<(), DocumentError> {
    let mut row_limit = config.max_scan_rows.min(SCAN_PAGE_ROWS);
    if row_limit == 0 {
        return Err(DocumentError::Capacity("scan row limit"));
    }
    let mut start = KeyBound::Unbounded;
    loop {
        let range = KeyRange::new(start, KeyBound::Unbounded)?;
        let (page, _items) = loop {
            let items = match budget.try_reserve_items(attribution(config), row_limit) {
                Ok(items) => items,
                Err(AdmissionError::ItemLimitExceeded { .. }) if row_limit > 1 => {
                    row_limit = row_limit.div_ceil(2);
                    continue;
                }
                Err(error) => return Err(error.into()),
            };
            match storage.scan(snapshot, &range, row_limit) {
                Ok(page) => break (page, items),
                Err(StorageError::Capacity("scan output bytes")) if row_limit > 1 => {
                    row_limit = row_limit.div_ceil(2);
                }
                Err(error) => return Err(error.into()),
            }
        };
        let rows = page.rows();
        for row in rows {
            inspect(row)?;
        }
        if rows.len() < row_limit {
            return Ok(());
        }
        let last = rows
            .last()
            .ok_or(DocumentError::Corrupt("scan page did not advance"))?
            .key
            .clone();
        start = KeyBound::Excluded(last);
    }
}

struct BatchBounds {
    frame: usize,
    mutable: usize,
    directory: usize,
}

impl BatchBounds {
    const fn empty() -> Self {
        Self {
            frame: WAL_HEADER_AND_TRAILER,
            mutable: 0,
            directory: DIRECTORY_FIXED_BYTES,
        }
    }

    fn with_put(&self, key_len: usize, value_len: usize) -> Result<Self, DocumentError> {
        let bytes = key_len
            .checked_add(value_len)
            .ok_or(DocumentError::Capacity("C0 batch bytes"))?;
        Ok(Self {
            frame: self
                .frame
                .checked_add(WAL_PUT_OVERHEAD + bytes)
                .ok_or(DocumentError::Capacity("C0 WAL frame"))?,
            mutable: self
                .mutable
                .checked_add(MUTABLE_ENTRY_OVERHEAD + bytes)
                .ok_or(DocumentError::Capacity("C0 mutable bytes"))?,
            directory: self
                .directory
                .checked_add(DIRECTORY_ENTRY_FIXED_BYTES + key_len * 2)
                .ok_or(DocumentError::Capacity("C0 directory bytes"))?,
        })
    }

    fn fits(&self, config: &StorageConfig) -> bool {
        self.frame <= config.format.max_frame_bytes
            && self.mutable <= config.max_mutable_bytes
            && self.directory <= config.format.max_directory_bytes
    }
}

fn install_missing_catalogs(
    storage: &Storage,
    config: &StorageConfig,
    budget: &MemoryBudget,
    view: &CertifiedShardView,
    initial: ReadSnapshot,
) -> Result<(), DocumentError> {
    let max_operations = config
        .format
        .max_operations
        .min(tb_core::MAX_WAL_OPERATIONS);
    if max_operations == 0 {
        return Err(DocumentError::Capacity("C0 batch operations"));
    }
    let mut mutations = Vec::new();
    let mut bounds = BatchBounds::empty();
    let mut permits: Vec<(Reservation, ItemReservation)> = Vec::new();
    for (binding, bytes) in view.catalogs() {
        let catalog_key = key(encode_catalog_key(binding.collection, binding.generation)?)?;
        if storage.get(initial, &catalog_key)?.is_some() {
            continue;
        }
        let next = loop {
            let next = bounds.with_put(catalog_key.as_bytes().len(), bytes.len())?;
            if !next.fits(config) || mutations.len() == max_operations {
                if mutations.is_empty() {
                    return Err(DocumentError::Capacity(
                        "one signed C0 exceeds batch limits",
                    ));
                }
                flush_catalog_batch(storage, config, view, &mut mutations, &mut permits)?;
                bounds = BatchBounds::empty();
                continue;
            }
            match admit_catalog_row(budget, config, catalog_key.as_bytes().len(), bytes.len()) {
                Ok(permit) => {
                    permits
                        .try_reserve(1)
                        .map_err(|_| DocumentError::Capacity("C0 permit allocation"))?;
                    mutations
                        .try_reserve(1)
                        .map_err(|_| DocumentError::Capacity("C0 batch allocation"))?;
                    permits.push(permit);
                    break next;
                }
                Err(DocumentError::Admission(
                    AdmissionError::LimitExceeded { .. } | AdmissionError::ItemLimitExceeded { .. },
                )) if !mutations.is_empty() => {
                    flush_catalog_batch(storage, config, view, &mut mutations, &mut permits)?;
                    bounds = BatchBounds::empty();
                }
                Err(error) => return Err(error),
            }
        };
        let mut value = Vec::new();
        value
            .try_reserve_exact(bytes.len())
            .map_err(|_| DocumentError::Capacity("C0 value allocation"))?;
        value.extend_from_slice(bytes);
        mutations.push(Mutation::Put {
            key: catalog_key,
            value,
        });
        bounds = next;
    }
    if !mutations.is_empty() {
        flush_catalog_batch(storage, config, view, &mut mutations, &mut permits)?;
    }
    Ok(())
}

fn admit_catalog_row(
    budget: &MemoryBudget,
    config: &StorageConfig,
    key_len: usize,
    value_len: usize,
) -> Result<(Reservation, ItemReservation), DocumentError> {
    let row_bytes = key_len
        .checked_add(value_len)
        .and_then(|bytes| {
            bytes.checked_add(
                2 * (std::mem::size_of::<Mutation>()
                    + std::mem::size_of::<Reservation>()
                    + std::mem::size_of::<ItemReservation>())
                    + 64,
            )
        })
        .ok_or(DocumentError::Capacity("C0 row work bytes"))?;
    let write = Attribution {
        category: MemoryCategory::WriteBuffer,
        ..attribution(config)
    };
    let bytes = budget.try_reserve(write, row_bytes)?;
    let item = budget.try_reserve_items(write, 1)?;
    Ok((bytes, item))
}

fn flush_catalog_batch(
    storage: &Storage,
    config: &StorageConfig,
    view: &CertifiedShardView,
    mutations: &mut Vec<Mutation>,
    permits: &mut Vec<(Reservation, ItemReservation)>,
) -> Result<(), DocumentError> {
    commit_catalog_batch(storage, config, view, mutations)?;
    *mutations = Vec::new();
    *permits = Vec::new();
    Ok(())
}

fn commit_catalog_batch(
    storage: &Storage,
    config: &StorageConfig,
    view: &CertifiedShardView,
    mutations: &[Mutation],
) -> Result<(), DocumentError> {
    let window = config.max_retry_window_ms.min(MAX_INSTALL_RETRY_MS);
    if window == 0 {
        return Err(DocumentError::Capacity("C0 retry window"));
    }
    let expires = storage
        .now_unix_ms()
        .checked_add(window)
        .ok_or(DocumentError::Capacity("C0 retry expiry"))?;
    let mut digest = Sha256::new();
    digest.update(INSTALL_NONCE_DOMAIN);
    digest.update(view.cluster());
    digest.update(view.tenant().to_bytes());
    digest.update(view.database().to_bytes());
    digest.update(view.shard().get().to_le_bytes());
    digest.update(view.epoch().get().to_le_bytes());
    digest.update(view.configuration().to_bytes());
    digest.update(view.local_origin().replica_id.to_bytes());
    digest.update(view.local_origin().incarnation);
    digest.update(expires.to_le_bytes());
    for mutation in mutations {
        let Mutation::Put { key, value } = mutation else {
            return Err(DocumentError::Corrupt("non-C0 installer mutation"));
        };
        digest.update(
            u64::try_from(key.as_bytes().len())
                .map_err(|_| DocumentError::Capacity("C0 key length"))?
                .to_le_bytes(),
        );
        digest.update(key.as_bytes());
        digest.update(
            u64::try_from(value.len())
                .map_err(|_| DocumentError::Capacity("C0 value length"))?
                .to_le_bytes(),
        );
        digest.update(value);
    }
    let hash: [u8; 32] = digest.finalize().into();
    let mut nonce = [0; 16];
    nonce.copy_from_slice(&hash[..16]);
    storage.commit_batch(
        storage.snapshot(),
        TransactionId::new(expires, nonce),
        mutations,
    )?;
    Ok(())
}

pub(crate) fn reject_distributed_local_state(
    storage: &Storage,
    config: &StorageConfig,
    budget: &MemoryBudget,
    limits: ReplicationLimits,
) -> Result<(), DocumentError> {
    scan_all_visible(storage, config, budget, storage.snapshot(), |row| match row
        .key
        .as_bytes()
        .first()
    {
        Some(0xD5 | 0xD6) => Err(DocumentError::Corrupt(
            "distributed document namespace in local shard",
        )),
        Some(0xD0) => {
            if !matches!(
                decode_document_key(row.key.as_bytes())?,
                DocumentKey::Primary { .. }
            ) {
                return Err(DocumentError::Corrupt("invalid local primary key"));
            }
            if decode_state_pointer(row.key.as_bytes(), &row.value).is_ok() {
                return Err(DocumentError::Corrupt("v2 document pointer in local shard"));
            }
            let decode_bytes = row
                .value
                .len()
                .checked_mul(2)
                .and_then(|bytes| bytes.checked_add(4_096))
                .ok_or(DocumentError::Capacity("local state decode bytes"))?;
            let _decode_permit = budget.try_reserve(attribution(config), decode_bytes)?;
            decode_state_with_limits(&row.value, limits.max_origins, limits.max_siblings)?;
            Ok(())
        }
        _ => Ok(()),
    })
}
