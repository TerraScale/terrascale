//! Private, locally synced inbox for complete incoming transaction groups.
//!
//! The journal has no public receive or acknowledgment API. A future semantic
//! codec must validate every document/WAL mapping before it may call `append`.
//! A chunk is merely staged; only a synced marker makes a group durable.

use std::error::Error;
use std::fmt;

use sha2::{Digest, Sha256};
use tb_core::{
    Attribution, DatabaseId, Key, KeyBound, KeyRange, MembershipEpoch, MemoryBudget,
    MemoryCategory, OriginId, ReadSnapshot, ReplicaId, Reservation, ShardId, TransactionId,
    WorkReservation,
};
use tb_format::{FormatLimits, decode_wal_frame};
use tb_storage::{Mutation, Storage, StorageConfig, StorageError};

use crate::cursor::{CursorError, CursorLimits, CursorScope, OfferOutcome, OriginCursor};
use crate::event::{
    ConfigurationId, EventDigest, EventId, MAX_DISTRIBUTED_METADATA_BYTES, MAX_WAL_GROUP_BYTES,
};

const MARKER_PREFIX: &[u8; 4] = b"RJ1M";
const MARKER_END: &[u8; 4] = b"RJ1N";
const CHUNK_PREFIX: &[u8; 4] = b"RJ1C";
const CHUNK_END: &[u8; 4] = b"RJ1D";
const MARKER_MAGIC: &[u8; 8] = b"TBJMARK1";
const CHUNK_MAGIC: &[u8; 8] = b"TBJCHNK1";
const CHUNK_BYTES: usize = 512 * 1024;
const MAX_CANONICAL_BYTES: usize = 8 + MAX_DISTRIBUTED_METADATA_BYTES + MAX_WAL_GROUP_BYTES;
const MAX_CHUNKS: usize = MAX_CANONICAL_BYTES.div_ceil(CHUNK_BYTES);
const CHUNK_HEADER_BYTES: usize = 8 + 8 + 32 + 32 + 2 + 4;

/// Only a semantic event decoder inside this crate may create this input in
/// production. Merely decoding a WAL frame does not make it a document event.
#[allow(dead_code, reason = "awaits authoritative document codec")]
pub(crate) struct VerifiedJournalGroup<'a> {
    pub(crate) id: EventId,
    pub(crate) epoch: MembershipEpoch,
    pub(crate) configuration: ConfigurationId,
    pub(crate) transaction_id: TransactionId,
    pub(crate) digest: EventDigest,
    pub(crate) metadata: &'a [u8],
    pub(crate) wal_frame: &'a [u8],
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct ChunkDescriptor {
    len: u32,
    sha256: [u8; 32],
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct Marker {
    id: EventId,
    epoch: MembershipEpoch,
    configuration: ConfigurationId,
    transaction_id: TransactionId,
    digest: EventDigest,
    metadata_len: u32,
    wal_len: u32,
    chunks: Vec<ChunkDescriptor>,
}

/// Exact bytes recovered from one contiguous durable inbox marker. The value
/// retains both its byte and item admission until replay preparation ends.
pub struct OwnedDurableGroup {
    canonical: Vec<u8>,
    metadata_len: usize,
    id: EventId,
    epoch: MembershipEpoch,
    configuration: ConfigurationId,
    transaction_id: TransactionId,
    digest: EventDigest,
    pub(crate) view_instance: u64,
    pub(crate) receiver_open_token: u64,
    _permit: WorkReservation,
}

impl OwnedDurableGroup {
    /// Exact `u32_le(metadata length) || metadata || u32_le(WAL length) || WAL` bytes.
    #[must_use]
    pub fn canonical_bytes(&self) -> &[u8] {
        &self.canonical
    }

    /// Exact verified distributed metadata bytes.
    #[must_use]
    pub fn metadata(&self) -> &[u8] {
        &self.canonical[4..4 + self.metadata_len]
    }

    /// Exact complete source WAL frame bytes.
    #[must_use]
    pub fn wal_frame(&self) -> &[u8] {
        &self.canonical[8 + self.metadata_len..]
    }

    #[must_use]
    pub const fn id(&self) -> EventId {
        self.id
    }

    #[must_use]
    pub const fn epoch(&self) -> MembershipEpoch {
        self.epoch
    }

    #[must_use]
    pub const fn configuration(&self) -> ConfigurationId {
        self.configuration
    }

    #[must_use]
    pub const fn transaction_id(&self) -> TransactionId {
        self.transaction_id
    }

    #[must_use]
    pub const fn digest(&self) -> EventDigest {
        self.digest
    }
}

/// Failure at the private incoming persistence boundary.
#[derive(Debug)]
pub(crate) enum JournalError {
    InvalidGroup(&'static str),
    Capacity(&'static str),
    Corrupt(&'static str),
    ConflictingIdentity,
    NeedsRecovery,
    Storage(StorageError),
    Cursor(CursorError),
    Core(tb_core::CoreError),
    Admission(tb_core::AdmissionError),
    Format(tb_format::FormatError),
}

impl fmt::Display for JournalError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidGroup(reason) => write!(formatter, "invalid incoming group: {reason}"),
            Self::Capacity(reason) => write!(formatter, "incoming journal capacity: {reason}"),
            Self::Corrupt(reason) => write!(formatter, "incoming journal corruption: {reason}"),
            Self::ConflictingIdentity => write!(formatter, "incoming event identity collision"),
            Self::NeedsRecovery => write!(formatter, "incoming journal requires reopen"),
            Self::Storage(error) => write!(formatter, "incoming journal storage: {error}"),
            Self::Cursor(error) => write!(formatter, "incoming journal cursor: {error}"),
            Self::Core(error) => write!(formatter, "incoming journal key: {error}"),
            Self::Admission(error) => write!(formatter, "incoming journal admission: {error}"),
            Self::Format(error) => write!(formatter, "incoming journal WAL format: {error}"),
        }
    }
}

impl Error for JournalError {}

impl From<StorageError> for JournalError {
    fn from(error: StorageError) -> Self {
        Self::Storage(error)
    }
}

impl From<CursorError> for JournalError {
    fn from(error: CursorError) -> Self {
        Self::Cursor(error)
    }
}

impl From<tb_core::CoreError> for JournalError {
    fn from(error: tb_core::CoreError) -> Self {
        Self::Core(error)
    }
}

impl From<tb_core::AdmissionError> for JournalError {
    fn from(error: tb_core::AdmissionError) -> Self {
        Self::Admission(error)
    }
}

impl From<tb_format::FormatError> for JournalError {
    fn from(error: tb_format::FormatError) -> Self {
        Self::Format(error)
    }
}

/// Exactly one certified origin stream in a private storage directory.
///
/// The owner serializes admission; no other writer may use its Storage path.
/// The lock order is journal ownership, then Storage's internal lock. Opening
/// completes recovery before publishing this value to another thread.
#[allow(dead_code, reason = "awaits authoritative document codec")]
pub(crate) struct ReplicaJournal {
    storage: Storage,
    cursor: OriginCursor,
    scope: CursorScope,
    budget: MemoryBudget,
    attribution: Attribution,
    max_disk_bytes: u64,
    max_scan_rows: usize,
    retry_window_ms: u64,
    _cursor_permit: Reservation,
    staged: Option<(EventId, EventDigest)>,
    needs_recovery: bool,
}

#[allow(dead_code, reason = "awaits authoritative document codec")]
impl ReplicaJournal {
    /// Inject a stored-chunk fault through the normal durable storage route.
    /// Only the independent Slow integration suite enables this seam.
    #[cfg(feature = "slow-test-hooks")]
    pub(crate) fn corrupt_first_durable_chunk_for_slow_test(
        &mut self,
        sequence: u64,
    ) -> Result<(), JournalError> {
        if sequence == 0 || sequence > self.cursor.durable_prefix() {
            return Err(JournalError::InvalidGroup("test durable sequence"));
        }
        let id = EventId::new(
            self.scope.database,
            self.scope.shard,
            self.scope.origin,
            sequence,
        )
        .map_err(|_| JournalError::Corrupt("test durable identity"))?;
        let snapshot = self.storage.snapshot();
        let marker_key = marker_key(id)?;
        let marker_value = self
            .storage
            .get(snapshot, &marker_key)?
            .ok_or(JournalError::Corrupt("test durable marker missing"))?;
        let marker = decode_marker(marker_value.bytes())?;
        if marker.chunks.is_empty() {
            return Err(JournalError::Corrupt("test durable chunks missing"));
        }
        drop(marker_value);
        let chunk_key = chunk_key(id, 0)?;
        let chunk_value = self
            .storage
            .get(snapshot, &chunk_key)?
            .ok_or(JournalError::Corrupt("test first chunk missing"))?;
        let mut changed = chunk_value.bytes().to_vec();
        if changed.len() <= CHUNK_HEADER_BYTES {
            return Err(JournalError::Corrupt("test first chunk has no payload"));
        }
        changed[CHUNK_HEADER_BYTES] ^= 1;
        drop(chunk_value);
        self.commit_put(chunk_key, changed)
    }

    /// Append only a budgeted semantic proof. Membership and schema authority
    /// must still be checked by the runtime before this private operation.
    pub(crate) fn append_verified(
        &mut self,
        proof: &crate::metadata::VerifiedIncomingGroup<'_>,
    ) -> Result<u64, JournalError> {
        self.append_group(&proof.journal_group())
    }

    pub(crate) fn open(
        config: StorageConfig,
        budget: MemoryBudget,
        scope: CursorScope,
        cursor_limits: CursorLimits,
    ) -> Result<Self, JournalError> {
        if config.format.max_key_bytes < CHUNK_PREFIX.len() + 64 + 2 {
            return Err(JournalError::Capacity("journal key limit"));
        }
        if config.format.max_value_bytes < CHUNK_BYTES + CHUNK_HEADER_BYTES {
            return Err(JournalError::Capacity("journal chunk value limit"));
        }
        if config.format.max_frame_bytes
            < 52 + 9 + CHUNK_PREFIX.len() + 64 + 2 + CHUNK_BYTES + CHUNK_HEADER_BYTES + 8
        {
            return Err(JournalError::Capacity("journal WAL frame limit"));
        }
        let attribution = Attribution {
            tenant: config.tenant,
            worker: config.worker,
            category: MemoryCategory::Replication,
        };
        let max_disk_bytes = config.max_disk_bytes;
        // Storage charges the complete key/value plus its 128-byte row
        // overhead, and errors rather than returning a short page at the byte
        // cap. Derive a page size that can always fit configured legal rows.
        let worst_scan_row = config
            .format
            .max_key_bytes
            .checked_add(config.format.max_value_bytes)
            .and_then(|bytes| bytes.checked_add(128))
            .ok_or(JournalError::Capacity("journal scan row bytes"))?;
        let max_scan_rows = config
            .max_scan_rows
            .min(config.max_scan_bytes / worst_scan_row)
            .min(128);
        if max_scan_rows == 0 {
            return Err(JournalError::Capacity("journal scan byte limit"));
        }
        let retry_window_ms = config.max_retry_window_ms.min(60_000);
        if retry_window_ms == 0 {
            return Err(JournalError::Capacity("internal retry window"));
        }
        let cursor_limits = cursor_limits.validate()?;
        let cursor_bytes = cursor_limits
            .max_pending
            .checked_mul(128)
            .ok_or(JournalError::Capacity("cursor memory"))?;
        let cursor_permit = budget.try_reserve(attribution, cursor_bytes)?;
        let storage = Storage::open(config, budget.clone())?;
        let cursor = OriginCursor::new(scope, cursor_limits)?;
        let mut journal = Self {
            storage,
            cursor,
            scope,
            budget,
            attribution,
            max_disk_bytes,
            max_scan_rows,
            retry_window_ms,
            _cursor_permit: cursor_permit,
            staged: None,
            needs_recovery: false,
        };
        journal.recover()?;
        Ok(journal)
    }

    /// Return the synced contiguous prefix reconstructed from verified markers.
    pub(crate) fn durable_prefix(&self) -> u64 {
        self.cursor.durable_prefix()
    }

    pub(crate) fn needs_recovery(&self) -> bool {
        self.needs_recovery
    }

    /// Read only a sequence within the recovered contiguous durable prefix.
    /// A missing marker inside that prefix is corruption, never an absent event.
    pub(crate) fn read_durable_exact(
        &self,
        sequence: u64,
        max_bytes: usize,
        view_instance: u64,
        receiver_open_token: u64,
    ) -> Result<Option<OwnedDurableGroup>, JournalError> {
        if self.needs_recovery {
            return Err(JournalError::NeedsRecovery);
        }
        if sequence == 0 || max_bytes == 0 || max_bytes > MAX_CANONICAL_BYTES {
            return Err(JournalError::InvalidGroup("durable read bounds"));
        }
        if sequence > self.cursor.durable_prefix() {
            return Ok(None);
        }
        let id = EventId::new(
            self.scope.database,
            self.scope.shard,
            self.scope.origin,
            sequence,
        )
        .map_err(|_| JournalError::Corrupt("durable sequence identity"))?;
        let snapshot = self.storage.snapshot();
        let key = marker_key(id)?;
        let value = self
            .storage
            .get(snapshot, &key)?
            .ok_or(JournalError::Corrupt("durable marker is missing"))?;
        let marker = decode_marker(value.bytes())?;
        if marker.id != id {
            return Err(JournalError::Corrupt("marker key and identity differ"));
        }
        self.validate_scope(marker.id, marker.epoch, marker.configuration)?;
        let (canonical, permit) = self.load_group(&marker, snapshot, max_bytes)?;
        Ok(Some(OwnedDurableGroup {
            canonical,
            metadata_len: marker.metadata_len as usize,
            id: marker.id,
            epoch: marker.epoch,
            configuration: marker.configuration,
            transaction_id: marker.transaction_id,
            digest: marker.digest,
            view_instance,
            receiver_open_token,
            _permit: permit,
        }))
    }

    /// Persist one semantically verified group. A successful result is local
    /// inbox durability only; issuing a network ACK remains a future caller's
    /// responsibility after authorization and membership checks.
    fn append_group(&mut self, group: &VerifiedJournalGroup<'_>) -> Result<u64, JournalError> {
        if self.needs_recovery {
            return Err(JournalError::NeedsRecovery);
        }
        self.validate_scope(group.id, group.epoch, group.configuration)?;
        let (canonical, _permit) = self.canonical_bytes(group)?;
        let marker_key = marker_key(group.id)?;
        let snapshot = self.storage.snapshot();
        if let Some(stored) = self.storage.get(snapshot, &marker_key)? {
            let marker = decode_marker(stored.bytes())?;
            if marker.id != group.id || marker.digest != group.digest {
                return Err(JournalError::ConflictingIdentity);
            }
            let (persisted, _persisted_permit) =
                self.load_group(&marker, snapshot, MAX_CANONICAL_BYTES)?;
            if persisted != canonical {
                return Err(JournalError::ConflictingIdentity);
            }
            return Ok(self.cursor.durable_prefix());
        }

        if let Some((staged_id, staged_digest)) = self.staged {
            if staged_id != group.id {
                return Err(JournalError::Capacity("unreconciled staged event"));
            }
            if staged_digest != group.digest {
                return Err(JournalError::ConflictingIdentity);
            }
        } else {
            self.check_disk_headroom(canonical.len())?;
        }
        match self.cursor.offer_identity(
            group.id,
            group.epoch,
            group.configuration,
            group.digest,
        )? {
            OfferOutcome::Accepted | OfferOutcome::DuplicatePending => {}
            OfferOutcome::RequiresStoredIdentityCheck => {
                return Err(JournalError::Corrupt("durable marker is missing"));
            }
        }
        self.staged = Some((group.id, group.digest));
        let write_result = self.write_group_chunks_and_marker(group, &canonical, marker_key);
        if let Err(error) = write_result {
            self.needs_recovery = true;
            return Err(error);
        }
        match self
            .cursor
            .record_synced(group.id, group.epoch, group.configuration, group.digest)
        {
            Ok(prefix) => {
                self.staged = None;
                Ok(prefix)
            }
            Err(error) => {
                self.needs_recovery = true;
                Err(error.into())
            }
        }
    }

    fn write_group_chunks_and_marker(
        &mut self,
        group: &VerifiedJournalGroup<'_>,
        canonical: &[u8],
        marker_key: Key,
    ) -> Result<(), JournalError> {
        let count = canonical.len().div_ceil(CHUNK_BYTES);
        let _descriptor_permit = self.budget.try_reserve(
            self.attribution,
            count * std::mem::size_of::<ChunkDescriptor>(),
        )?;
        let mut descriptors = Vec::new();
        descriptors
            .try_reserve_exact(count)
            .map_err(|_| JournalError::Capacity("chunk descriptors"))?;
        for (index, bytes) in canonical.chunks(CHUNK_BYTES).enumerate() {
            let index = u16::try_from(index).map_err(|_| JournalError::Capacity("chunk count"))?;
            let _chunk_permit = self
                .budget
                .try_reserve(self.attribution, CHUNK_HEADER_BYTES + bytes.len())?;
            let value = encode_chunk(group.epoch, group.configuration, group.digest, index, bytes)?;
            let key = chunk_key(group.id, index)?;
            let descriptor = ChunkDescriptor {
                len: u32::try_from(value.len())
                    .map_err(|_| JournalError::Capacity("chunk length"))?,
                sha256: sha256(&value),
            };
            match self.storage.get(self.storage.snapshot(), &key)? {
                Some(existing) if existing.bytes() == value.as_slice() => {}
                Some(_) => return Err(JournalError::ConflictingIdentity),
                None => self.commit_put(key, value)?,
            }
            descriptors.push(descriptor);
        }
        let marker = Marker {
            id: group.id,
            epoch: group.epoch,
            configuration: group.configuration,
            transaction_id: group.transaction_id,
            digest: group.digest,
            metadata_len: u32::try_from(group.metadata.len())
                .map_err(|_| JournalError::Capacity("metadata length"))?,
            wal_len: u32::try_from(group.wal_frame.len())
                .map_err(|_| JournalError::Capacity("WAL length"))?,
            chunks: descriptors,
        };
        let _marker_permit = self
            .budget
            .try_reserve(self.attribution, 178 + marker.chunks.len() * 36)?;
        self.commit_put(marker_key, encode_marker(&marker)?)?;
        Ok(())
    }

    fn recover(&mut self) -> Result<(), JournalError> {
        let snapshot = self.storage.snapshot();
        let mut start = KeyBound::Included(Key::new(MARKER_PREFIX.to_vec())?);
        let end = KeyBound::Excluded(Key::new(MARKER_END.to_vec())?);
        loop {
            let range = KeyRange::new(start, end.clone())?;
            let page = self.storage.scan(snapshot, &range, self.max_scan_rows)?;
            if page.rows().is_empty() {
                break;
            }
            let last_key = page
                .rows()
                .last()
                .ok_or(JournalError::Corrupt("empty scan page"))?
                .key
                .clone();
            for row in page.rows() {
                let marker = decode_marker(&row.value)?;
                if row.key != marker_key(marker.id)? {
                    return Err(JournalError::Corrupt("marker key and identity differ"));
                }
                self.validate_scope(marker.id, marker.epoch, marker.configuration)?;
                let (_canonical, _canonical_permit) =
                    self.load_group(&marker, snapshot, MAX_CANONICAL_BYTES)?;
                self.cursor.offer_identity(
                    marker.id,
                    marker.epoch,
                    marker.configuration,
                    marker.digest,
                )?;
                self.cursor.record_synced(
                    marker.id,
                    marker.epoch,
                    marker.configuration,
                    marker.digest,
                )?;
            }
            if page.rows().len() < self.max_scan_rows {
                break;
            }
            start = KeyBound::Excluded(last_key);
        }
        self.recover_staging(snapshot)?;
        Ok(())
    }

    /// Enumerate chunks without retaining a set proportional to the inbox.
    /// A marker's referenced chunks were already verified by `recover`.
    /// At most one incomplete group may remain; it must be retried or repaired
    /// before this origin can stage another group. This does not reclaim SSD.
    fn recover_staging(&mut self, snapshot: tb_core::ReadSnapshot) -> Result<(), JournalError> {
        let mut start = KeyBound::Included(Key::new(CHUNK_PREFIX.to_vec())?);
        let end = KeyBound::Excluded(Key::new(CHUNK_END.to_vec())?);
        let mut unmarked_count = 0_usize;
        loop {
            let range = KeyRange::new(start, end.clone())?;
            let page = self.storage.scan(snapshot, &range, self.max_scan_rows)?;
            if page.rows().is_empty() {
                break;
            }
            let last_key = page
                .rows()
                .last()
                .ok_or(JournalError::Corrupt("empty chunk scan page"))?
                .key
                .clone();
            for row in page.rows() {
                let (id, index) = parse_chunk_key(&row.key)?;
                let (epoch, configuration, digest) = chunk_header_identity(&row.value)?;
                self.validate_scope(id, epoch, configuration)?;
                let payload = decode_chunk(&row.value, epoch, configuration, digest, index)?;
                if payload.is_empty() || payload.len() > CHUNK_BYTES || index as usize >= MAX_CHUNKS
                {
                    return Err(JournalError::Corrupt("staged chunk bounds"));
                }
                if let Some(stored) = self.storage.get(snapshot, &marker_key(id)?)? {
                    let marker = decode_marker(stored.bytes())?;
                    let descriptor = marker
                        .chunks
                        .get(index as usize)
                        .ok_or(JournalError::Corrupt("extra chunk after final marker"))?;
                    if marker.id != id
                        || marker.epoch != epoch
                        || marker.configuration != configuration
                        || marker.digest != digest
                        || descriptor.len as usize != row.value.len()
                        || descriptor.sha256 != sha256(&row.value)
                    {
                        return Err(JournalError::Corrupt("committed chunk mismatch"));
                    }
                } else {
                    unmarked_count += 1;
                    if unmarked_count > MAX_CHUNKS {
                        return Err(JournalError::Corrupt("too many unmarked chunks"));
                    }
                    match self.staged {
                        None => self.staged = Some((id, digest)),
                        Some((staged_id, staged_digest))
                            if staged_id == id && staged_digest == digest => {}
                        Some(_) => {
                            return Err(JournalError::Corrupt(
                                "multiple unreconciled staged events",
                            ));
                        }
                    }
                }
            }
            if page.rows().len() < self.max_scan_rows {
                break;
            }
            start = KeyBound::Excluded(last_key);
        }
        Ok(())
    }

    fn validate_scope(
        &self,
        id: EventId,
        epoch: MembershipEpoch,
        configuration: ConfigurationId,
    ) -> Result<(), JournalError> {
        if id.database() != self.scope.database
            || id.shard() != self.scope.shard
            || id.origin() != self.scope.origin
        {
            return Err(JournalError::Cursor(CursorError::WrongScope));
        }
        if epoch != self.scope.epoch {
            return Err(JournalError::Cursor(CursorError::EpochMismatch));
        }
        if configuration != self.scope.configuration {
            return Err(JournalError::Cursor(CursorError::ConfigurationMismatch));
        }
        Ok(())
    }

    fn canonical_bytes(
        &self,
        group: &VerifiedJournalGroup<'_>,
    ) -> Result<(Vec<u8>, Reservation), JournalError> {
        if group.metadata.is_empty() || group.metadata.len() > MAX_DISTRIBUTED_METADATA_BYTES {
            return Err(JournalError::InvalidGroup("distributed metadata length"));
        }
        if group.wal_frame.is_empty() || group.wal_frame.len() > MAX_WAL_GROUP_BYTES {
            return Err(JournalError::InvalidGroup("complete WAL length"));
        }
        let frame = decode_wal_frame(group.wal_frame, &FormatLimits::local_v1())?;
        if frame.transaction_id() != group.transaction_id {
            return Err(JournalError::InvalidGroup("WAL transaction identity"));
        }
        if digest_event(group.metadata, group.wal_frame)? != group.digest {
            return Err(JournalError::InvalidGroup("event digest"));
        }
        let capacity = 8 + group.metadata.len() + group.wal_frame.len();
        let permit = self.budget.try_reserve(self.attribution, capacity)?;
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(capacity)
            .map_err(|_| JournalError::Capacity("canonical buffer"))?;
        bytes.extend_from_slice(
            &u32::try_from(group.metadata.len())
                .map_err(|_| JournalError::Capacity("metadata length"))?
                .to_le_bytes(),
        );
        bytes.extend_from_slice(group.metadata);
        bytes.extend_from_slice(
            &u32::try_from(group.wal_frame.len())
                .map_err(|_| JournalError::Capacity("WAL length"))?
                .to_le_bytes(),
        );
        bytes.extend_from_slice(group.wal_frame);
        Ok((bytes, permit))
    }

    fn load_group(
        &self,
        marker: &Marker,
        snapshot: ReadSnapshot,
        max_bytes: usize,
    ) -> Result<(Vec<u8>, WorkReservation), JournalError> {
        let total = 8_usize
            .checked_add(marker.metadata_len as usize)
            .and_then(|size| size.checked_add(marker.wal_len as usize))
            .ok_or(JournalError::Corrupt("marker length overflow"))?;
        if marker.metadata_len == 0
            || marker.metadata_len as usize > MAX_DISTRIBUTED_METADATA_BYTES
            || marker.wal_len == 0
            || marker.wal_len as usize > MAX_WAL_GROUP_BYTES
            || marker.chunks.len() != total.div_ceil(CHUNK_BYTES)
        {
            return Err(JournalError::Corrupt("marker lengths exceed event limits"));
        }
        if total > max_bytes {
            return Err(JournalError::Capacity("durable read max bytes"));
        }
        let permit = self.budget.try_reserve_work(self.attribution, total, 1)?;
        let mut canonical = Vec::new();
        canonical
            .try_reserve_exact(total)
            .map_err(|_| JournalError::Capacity("recovery buffer"))?;
        if canonical.capacity() > permit.bytes() {
            return Err(JournalError::Capacity("recovery buffer capacity"));
        }
        for (index, descriptor) in marker.chunks.iter().enumerate() {
            let index = u16::try_from(index).map_err(|_| JournalError::Corrupt("chunk ordinal"))?;
            let key = chunk_key(marker.id, index)?;
            let chunk = self
                .storage
                .get(snapshot, &key)?
                .ok_or(JournalError::Corrupt(
                    "committed marker references missing chunk",
                ))?;
            if chunk.bytes().len() != descriptor.len as usize
                || sha256(chunk.bytes()) != descriptor.sha256
            {
                return Err(JournalError::Corrupt("committed chunk digest or length"));
            }
            let payload = decode_chunk(
                chunk.bytes(),
                marker.epoch,
                marker.configuration,
                marker.digest,
                index,
            )?;
            if payload.len() > CHUNK_BYTES
                || (index as usize + 1 < marker.chunks.len() && payload.len() != CHUNK_BYTES)
            {
                return Err(JournalError::Corrupt("noncanonical chunk size"));
            }
            if payload.len() > total - canonical.len() {
                return Err(JournalError::Corrupt("committed group length"));
            }
            canonical.extend_from_slice(payload);
        }
        if canonical.len() != total {
            return Err(JournalError::Corrupt("committed group length"));
        }
        let metadata_len = u32::from_le_bytes(
            canonical[..4]
                .try_into()
                .map_err(|_| JournalError::Corrupt("metadata length"))?,
        ) as usize;
        if metadata_len != marker.metadata_len as usize {
            return Err(JournalError::Corrupt("metadata length differs from marker"));
        }
        let wal_len_offset = 4 + metadata_len;
        let wal_len = u32::from_le_bytes(
            canonical[wal_len_offset..wal_len_offset + 4]
                .try_into()
                .map_err(|_| JournalError::Corrupt("WAL length"))?,
        ) as usize;
        if wal_len != marker.wal_len as usize {
            return Err(JournalError::Corrupt("WAL length differs from marker"));
        }
        let frame = &canonical[wal_len_offset + 4..];
        if digest_event(&canonical[4..wal_len_offset], frame)? != marker.digest {
            return Err(JournalError::Corrupt("committed event digest"));
        }
        let decoded = decode_wal_frame(frame, &FormatLimits::local_v1())?;
        if decoded.transaction_id() != marker.transaction_id {
            return Err(JournalError::Corrupt("committed WAL transaction identity"));
        }
        Ok((canonical, permit))
    }

    fn check_disk_headroom(&self, canonical_len: usize) -> Result<(), JournalError> {
        // This is a conservative admission check, not an atomic disk reservation.
        // A mid-group disk failure remains safe because chunks lack a marker.
        let worst_case = canonical_len
            .checked_mul(2)
            .and_then(|bytes| bytes.checked_add(2 * 1024 * 1024))
            .ok_or(JournalError::Capacity("journal disk headroom"))?;
        let remaining = self
            .max_disk_bytes
            .saturating_sub(self.storage.stats().disk_bytes);
        if remaining < worst_case as u64 {
            return Err(JournalError::Capacity("journal disk headroom"));
        }
        Ok(())
    }

    fn commit_put(&mut self, key: Key, value: Vec<u8>) -> Result<(), JournalError> {
        let token = internal_token(self.storage.now_unix_ms(), self.retry_window_ms)?;
        let result = self.storage.commit_batch(
            self.storage.snapshot(),
            token,
            &[Mutation::Put { key, value }],
        );
        match result {
            Ok(_) => Ok(()),
            Err(error) => {
                // A failed sync may have made the write durable. Reopen and
                // reconcile before any retry or acknowledgment.
                self.needs_recovery = true;
                Err(error.into())
            }
        }
    }
}

fn internal_token(now_ms: u64, retry_window_ms: u64) -> Result<TransactionId, JournalError> {
    let deadline = now_ms
        .checked_add(retry_window_ms)
        .ok_or(JournalError::Capacity("retry deadline"))?;
    Ok(TransactionId::new(
        deadline,
        ulid::Ulid::generate().to_bytes(),
    ))
}

pub(crate) fn digest_event(metadata: &[u8], wal_frame: &[u8]) -> Result<EventDigest, JournalError> {
    crate::metadata::compute_event_digest(metadata, wal_frame)
        .map_err(|_| JournalError::Capacity("canonical event digest input"))
}

fn sha256(bytes: &[u8]) -> [u8; 32] {
    Sha256::digest(bytes).into()
}

fn identity_bytes(id: EventId) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(64);
    bytes.extend_from_slice(&id.database().to_bytes());
    bytes.extend_from_slice(&id.shard().get().to_be_bytes());
    bytes.extend_from_slice(&id.origin().replica_id.to_bytes());
    bytes.extend_from_slice(&id.origin().incarnation);
    bytes.extend_from_slice(&id.sequence().to_be_bytes());
    bytes
}

fn marker_key(id: EventId) -> Result<Key, JournalError> {
    let mut bytes = MARKER_PREFIX.to_vec();
    bytes.extend_from_slice(&identity_bytes(id));
    Ok(Key::new(bytes)?)
}

fn chunk_key(id: EventId, index: u16) -> Result<Key, JournalError> {
    let mut bytes = CHUNK_PREFIX.to_vec();
    bytes.extend_from_slice(&identity_bytes(id));
    bytes.extend_from_slice(&index.to_be_bytes());
    Ok(Key::new(bytes)?)
}

fn parse_chunk_key(key: &Key) -> Result<(EventId, u16), JournalError> {
    let bytes = key.as_bytes();
    if bytes.len() != 4 + 64 + 2 || &bytes[..4] != CHUNK_PREFIX {
        return Err(JournalError::Corrupt("chunk key grammar"));
    }
    let mut offset = 4;
    let database = DatabaseId::from_bytes(take::<16>(bytes, &mut offset)?);
    let shard = ShardId::new(u64::from_be_bytes(take::<8>(bytes, &mut offset)?));
    let replica_id = ReplicaId::from_bytes(take::<16>(bytes, &mut offset)?);
    let incarnation = take::<16>(bytes, &mut offset)?;
    let sequence = u64::from_be_bytes(take::<8>(bytes, &mut offset)?);
    let id = EventId::new(
        database,
        shard,
        OriginId {
            replica_id,
            incarnation,
        },
        sequence,
    )
    .map_err(|_| JournalError::Corrupt("chunk event identity"))?;
    let index = u16::from_be_bytes(take::<2>(bytes, &mut offset)?);
    Ok((id, index))
}

fn chunk_header_identity(
    value: &[u8],
) -> Result<(MembershipEpoch, ConfigurationId, EventDigest), JournalError> {
    if value.len() < CHUNK_HEADER_BYTES || &value[..8] != CHUNK_MAGIC {
        return Err(JournalError::Corrupt("staged chunk header"));
    }
    let epoch = MembershipEpoch::new(u64::from_le_bytes(
        value[8..16]
            .try_into()
            .map_err(|_| JournalError::Corrupt("staged epoch"))?,
    ));
    if epoch.get() == 0 {
        return Err(JournalError::Corrupt("staged epoch"));
    }
    let configuration = ConfigurationId::from_bytes(
        value[16..48]
            .try_into()
            .map_err(|_| JournalError::Corrupt("staged configuration"))?,
    )
    .map_err(|_| JournalError::Corrupt("staged configuration"))?;
    let digest = EventDigest::from_bytes(
        value[48..80]
            .try_into()
            .map_err(|_| JournalError::Corrupt("staged digest"))?,
    );
    Ok((epoch, configuration, digest))
}

fn encode_chunk(
    epoch: MembershipEpoch,
    configuration: ConfigurationId,
    digest: EventDigest,
    index: u16,
    payload: &[u8],
) -> Result<Vec<u8>, JournalError> {
    let mut value = Vec::with_capacity(CHUNK_HEADER_BYTES + payload.len());
    value.extend_from_slice(CHUNK_MAGIC);
    value.extend_from_slice(&epoch.get().to_le_bytes());
    value.extend_from_slice(&configuration.to_bytes());
    value.extend_from_slice(&digest.to_bytes());
    value.extend_from_slice(&index.to_le_bytes());
    value.extend_from_slice(
        &u32::try_from(payload.len())
            .map_err(|_| JournalError::Capacity("chunk length"))?
            .to_le_bytes(),
    );
    value.extend_from_slice(payload);
    Ok(value)
}

fn decode_chunk(
    value: &[u8],
    epoch: MembershipEpoch,
    configuration: ConfigurationId,
    digest: EventDigest,
    index: u16,
) -> Result<&[u8], JournalError> {
    if value.len() < CHUNK_HEADER_BYTES
        || &value[..8] != CHUNK_MAGIC
        || value[8..16] != epoch.get().to_le_bytes()
        || value[16..48] != configuration.to_bytes()
        || value[48..80] != digest.to_bytes()
        || value[80..82] != index.to_le_bytes()
    {
        return Err(JournalError::Corrupt("chunk identity"));
    }
    let declared = u32::from_le_bytes(
        value[82..86]
            .try_into()
            .map_err(|_| JournalError::Corrupt("chunk length"))?,
    ) as usize;
    if declared != value.len() - CHUNK_HEADER_BYTES {
        return Err(JournalError::Corrupt("chunk length"));
    }
    Ok(&value[CHUNK_HEADER_BYTES..])
}

fn encode_marker(marker: &Marker) -> Result<Vec<u8>, JournalError> {
    let mut value =
        Vec::with_capacity(8 + 64 + 8 + 32 + 24 + 32 + 4 + 4 + 2 + marker.chunks.len() * 36);
    value.extend_from_slice(MARKER_MAGIC);
    value.extend_from_slice(&identity_bytes(marker.id));
    value.extend_from_slice(&marker.epoch.get().to_le_bytes());
    value.extend_from_slice(&marker.configuration.to_bytes());
    value.extend_from_slice(&marker.transaction_id.to_bytes());
    value.extend_from_slice(&marker.digest.to_bytes());
    value.extend_from_slice(&marker.metadata_len.to_le_bytes());
    value.extend_from_slice(&marker.wal_len.to_le_bytes());
    value.extend_from_slice(
        &u16::try_from(marker.chunks.len())
            .map_err(|_| JournalError::Capacity("marker chunk count"))?
            .to_le_bytes(),
    );
    for chunk in &marker.chunks {
        value.extend_from_slice(&chunk.len.to_le_bytes());
        value.extend_from_slice(&chunk.sha256);
    }
    Ok(value)
}

fn decode_marker(bytes: &[u8]) -> Result<Marker, JournalError> {
    if bytes.len() < 178 || &bytes[..8] != MARKER_MAGIC {
        return Err(JournalError::Corrupt("marker magic or length"));
    }
    let mut offset = 8;
    let database = DatabaseId::from_bytes(take::<16>(bytes, &mut offset)?);
    let shard = ShardId::new(u64::from_be_bytes(take::<8>(bytes, &mut offset)?));
    let replica_id = ReplicaId::from_bytes(take::<16>(bytes, &mut offset)?);
    let incarnation = take::<16>(bytes, &mut offset)?;
    let sequence = u64::from_be_bytes(take::<8>(bytes, &mut offset)?);
    let id = EventId::new(
        database,
        shard,
        OriginId {
            replica_id,
            incarnation,
        },
        sequence,
    )
    .map_err(|_| JournalError::Corrupt("marker event identity"))?;
    let epoch = MembershipEpoch::new(u64::from_le_bytes(take::<8>(bytes, &mut offset)?));
    if epoch.get() == 0 {
        return Err(JournalError::Corrupt("marker epoch"));
    }
    let configuration = ConfigurationId::from_bytes(take::<32>(bytes, &mut offset)?)
        .map_err(|_| JournalError::Corrupt("marker configuration"))?;
    let transaction_id = TransactionId::from_bytes(take::<24>(bytes, &mut offset)?);
    let digest = EventDigest::from_bytes(take::<32>(bytes, &mut offset)?);
    let metadata_len = u32::from_le_bytes(take::<4>(bytes, &mut offset)?);
    let wal_len = u32::from_le_bytes(take::<4>(bytes, &mut offset)?);
    let count = u16::from_le_bytes(take::<2>(bytes, &mut offset)?) as usize;
    if count == 0 || count > MAX_CHUNKS || bytes.len() != offset + count * 36 {
        return Err(JournalError::Corrupt("marker chunk table"));
    }
    let mut chunks = Vec::with_capacity(count);
    for _ in 0..count {
        chunks.push(ChunkDescriptor {
            len: u32::from_le_bytes(take::<4>(bytes, &mut offset)?),
            sha256: take::<32>(bytes, &mut offset)?,
        });
    }
    Ok(Marker {
        id,
        epoch,
        configuration,
        transaction_id,
        digest,
        metadata_len,
        wal_len,
        chunks,
    })
}

fn take<const N: usize>(bytes: &[u8], offset: &mut usize) -> Result<[u8; N], JournalError> {
    let end = offset
        .checked_add(N)
        .ok_or(JournalError::Corrupt("marker offset"))?;
    let field = bytes
        .get(*offset..end)
        .ok_or(JournalError::Corrupt("truncated marker"))?;
    *offset = end;
    field
        .try_into()
        .map_err(|_| JournalError::Corrupt("marker field width"))
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::{Path, PathBuf};

    use tb_core::{BudgetConfig, TenantId, WorkerId};
    use tb_format::{WalOperation, encode_wal_frame_into};

    use super::*;

    struct TempInbox(PathBuf);

    impl TempInbox {
        fn new() -> Self {
            Self(std::env::temp_dir().join(format!(
                "terrabase-replica-journal-{}-{}",
                std::process::id(),
                ulid::Ulid::generate()
            )))
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempInbox {
        fn drop(&mut self) {
            if self.0.exists() {
                fs::remove_dir_all(&self.0).expect("remove test inbox");
            }
        }
    }

    fn budget() -> MemoryBudget {
        MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("test budget"))
    }

    fn config(path: &Path) -> StorageConfig {
        StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(11))
    }

    fn scope() -> CursorScope {
        CursorScope {
            database: DatabaseId::from_bytes([2; 16]),
            shard: ShardId::new(3),
            origin: OriginId {
                replica_id: ReplicaId::from_bytes([4; 16]),
                incarnation: [5; 16],
            },
            epoch: MembershipEpoch::new(6),
            configuration: ConfigurationId::from_bytes([7; 32]).expect("configuration"),
        }
    }

    fn open(path: &Path) -> ReplicaJournal {
        ReplicaJournal::open(
            config(path),
            budget(),
            scope(),
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        )
        .expect("open journal")
    }

    struct Fixture {
        id: EventId,
        transaction_id: TransactionId,
        metadata: Vec<u8>,
        wal_frame: Vec<u8>,
        digest: EventDigest,
    }

    impl Fixture {
        fn new(sequence: u64, value_len: usize) -> Self {
            Self::with_metadata(sequence, value_len, 33)
        }

        fn with_metadata(sequence: u64, value_len: usize, metadata_len: usize) -> Self {
            let scope = scope();
            let id = EventId::new(scope.database, scope.shard, scope.origin, sequence)
                .expect("event identity");
            let seed = u8::try_from(sequence).expect("test origin sequence fits byte seed");
            let transaction_id = TransactionId::new(u64::MAX, [seed; 16]);
            let value = vec![seed; value_len];
            let mut wal_frame = Vec::new();
            let first_len = value_len.min(tb_core::MAX_VALUE_BYTES);
            let mut operations = vec![WalOperation::Put {
                key: b"logical-fixture-0",
                value: &value[..first_len],
            }];
            if first_len < value_len {
                operations.push(WalOperation::Put {
                    key: b"logical-fixture-1",
                    value: &value[first_len..],
                });
            }
            encode_wal_frame_into(
                &mut wal_frame,
                sequence * 10, // Local WAL sequence is not the origin event sequence.
                transaction_id,
                &operations,
                &FormatLimits::local_v1(),
            )
            .expect("complete WAL fixture");
            // This is a journal-only fixture, not a valid document metadata
            // encoding. The production intake stays private until that codec.
            let metadata = vec![seed; metadata_len];
            let digest = digest_event(&metadata, &wal_frame).expect("bounded event digest");
            Self {
                id,
                transaction_id,
                metadata,
                wal_frame,
                digest,
            }
        }

        fn group(&self) -> VerifiedJournalGroup<'_> {
            VerifiedJournalGroup {
                id: self.id,
                epoch: scope().epoch,
                configuration: scope().configuration,
                transaction_id: self.transaction_id,
                digest: self.digest,
                metadata: &self.metadata,
                wal_frame: &self.wal_frame,
            }
        }
    }

    fn stage_without_marker(journal: &mut ReplicaJournal, fixture: &Fixture) -> Marker {
        let group = fixture.group();
        let (canonical, _permit) = journal.canonical_bytes(&group).expect("canonical bytes");
        let mut chunks = Vec::new();
        for (index, payload) in canonical.chunks(CHUNK_BYTES).enumerate() {
            let index = u16::try_from(index).expect("bounded chunk count");
            let value = encode_chunk(
                group.epoch,
                group.configuration,
                group.digest,
                index,
                payload,
            )
            .expect("chunk");
            chunks.push(ChunkDescriptor {
                len: u32::try_from(value.len()).expect("bounded chunk length"),
                sha256: sha256(&value),
            });
            journal
                .commit_put(chunk_key(group.id, index).expect("chunk key"), value)
                .expect("synced chunk");
        }
        Marker {
            id: group.id,
            epoch: group.epoch,
            configuration: group.configuration,
            transaction_id: group.transaction_id,
            digest: group.digest,
            metadata_len: u32::try_from(group.metadata.len()).expect("bounded metadata"),
            wal_len: u32::try_from(group.wal_frame.len()).expect("bounded WAL"),
            chunks,
        }
    }

    #[test]
    fn reordered_synced_markers_recover_contiguous_prefix_and_detect_collision() {
        let dir = TempInbox::new();
        let two = Fixture::new(2, 8);
        let one = Fixture::new(1, 8);
        let mut journal = open(dir.path());
        assert_eq!(journal.append_group(&two.group()).expect("stage second"), 0);
        drop(journal);

        let mut journal = open(dir.path());
        assert_eq!(journal.durable_prefix(), 0);
        assert_eq!(journal.append_group(&one.group()).expect("stage first"), 2);
        assert_eq!(journal.append_group(&one.group()).expect("duplicate"), 2);
        let changed = Fixture::new(1, 9);
        assert!(matches!(
            journal.append_group(&changed.group()),
            Err(JournalError::ConflictingIdentity)
        ));
        drop(journal);
        assert_eq!(open(dir.path()).durable_prefix(), 2);
    }

    #[test]
    fn synced_chunks_without_marker_do_not_advance_and_retry_reuses_them() {
        let dir = TempInbox::new();
        let fixture = Fixture::new(1, 1_500_000);
        let later = Fixture::new(2, 8);
        let mut journal = open(dir.path());
        let marker = stage_without_marker(&mut journal, &fixture);
        assert!(marker.chunks.len() > 1);
        drop(journal);

        let mut journal = open(dir.path());
        assert_eq!(journal.durable_prefix(), 0);
        assert!(matches!(
            journal.append_group(&later.group()),
            Err(JournalError::Capacity("unreconciled staged event"))
        ));
        assert_eq!(
            journal
                .append_group(&fixture.group())
                .expect("resume staging"),
            1
        );
        assert_eq!(
            journal
                .append_group(&later.group())
                .expect("staging gate cleared"),
            2
        );
        drop(journal);
        assert_eq!(open(dir.path()).durable_prefix(), 2);
    }

    #[test]
    fn synced_marker_before_cursor_update_recovers_but_missing_chunk_fails_closed() {
        let dir = TempInbox::new();
        let fixture = Fixture::new(1, 8);
        let mut journal = open(dir.path());
        let marker = stage_without_marker(&mut journal, &fixture);
        journal
            .commit_put(
                marker_key(marker.id).expect("marker key"),
                encode_marker(&marker).expect("bounded marker"),
            )
            .expect("synced final marker");
        assert_eq!(journal.durable_prefix(), 0);
        drop(journal);

        assert_eq!(open(dir.path()).durable_prefix(), 1);
        let storage = Storage::open(config(dir.path()), budget()).expect("open private storage");
        storage
            .commit_batch(
                storage.snapshot(),
                internal_token(storage.now_unix_ms(), 60_000).expect("fresh token"),
                &[Mutation::Delete {
                    key: chunk_key(fixture.id, 0).expect("chunk key"),
                }],
            )
            .expect("delete chunk for corruption test");
        drop(storage);
        let error = ReplicaJournal::open(
            config(dir.path()),
            budget(),
            scope(),
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        )
        .err()
        .expect("missing committed chunk must fail recovery");
        assert!(matches!(error, JournalError::Corrupt(_)));
    }

    #[test]
    fn retained_marker_cannot_be_relabelled_into_another_epoch() {
        let dir = TempInbox::new();
        let fixture = Fixture::new(1, 8);
        let mut journal = open(dir.path());
        assert_eq!(
            journal
                .append_group(&fixture.group())
                .expect("durable group"),
            1
        );
        drop(journal);

        let mut changed = scope();
        changed.epoch = MembershipEpoch::new(7);
        let error = ReplicaJournal::open(
            config(dir.path()),
            budget(),
            changed,
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        )
        .err()
        .expect("old marker must not become new epoch progress");
        assert!(matches!(
            error,
            JournalError::Cursor(CursorError::EpochMismatch)
        ));
    }

    #[test]
    fn recovery_quarantines_multiple_unmarked_event_identities() {
        let dir = TempInbox::new();
        let mut journal = open(dir.path());
        stage_without_marker(&mut journal, &Fixture::new(1, 8));
        stage_without_marker(&mut journal, &Fixture::new(2, 8));
        drop(journal);

        let error = ReplicaJournal::open(
            config(dir.path()),
            budget(),
            scope(),
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        )
        .err()
        .expect("multiple unreconciled identities must fail closed");
        assert!(matches!(
            error,
            JournalError::Corrupt("multiple unreconciled staged events")
        ));
    }

    #[test]
    fn many_large_marked_groups_reopen_across_chunk_scan_pages() {
        let dir = TempInbox::new();
        let mut journal = open(dir.path());
        for sequence in 1..=9 {
            let fixture =
                Fixture::with_metadata(sequence, 1_900_000, MAX_DISTRIBUTED_METADATA_BYTES);
            assert_eq!(
                journal
                    .append_group(&fixture.group())
                    .expect("synced large group"),
                sequence
            );
        }
        drop(journal);
        assert_eq!(open(dir.path()).durable_prefix(), 9);
    }

    #[test]
    fn scan_byte_limit_must_fit_one_maximum_storage_row() {
        let dir = TempInbox::new();
        let mut limits = config(dir.path());
        let required = limits.format.max_key_bytes + limits.format.max_value_bytes + 128;
        limits.max_scan_bytes = required - 1;
        let error = ReplicaJournal::open(
            limits,
            budget(),
            scope(),
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        )
        .err()
        .expect("undersized scan byte cap");
        assert!(matches!(
            error,
            JournalError::Capacity("journal scan byte limit")
        ));

        let mut exact = config(dir.path());
        exact.max_scan_bytes = required;
        let mut journal = ReplicaJournal::open(
            exact.clone(),
            budget(),
            scope(),
            CursorLimits {
                max_pending: 4,
                max_sequence_distance: 4,
            },
        )
        .expect("single-row scan cap opens");
        let fixture = Fixture::new(1, 1_900_000);
        assert_eq!(
            journal
                .append_group(&fixture.group())
                .expect("synced group"),
            1
        );
        drop(journal);
        assert_eq!(
            ReplicaJournal::open(
                exact,
                budget(),
                scope(),
                CursorLimits {
                    max_pending: 4,
                    max_sequence_distance: 4,
                },
            )
            .expect("single-row scan recovery")
            .durable_prefix(),
            1
        );
    }

    #[test]
    fn exact_durable_read_obeys_prefix_bounds_and_retains_work_permit() {
        let dir = TempInbox::new();
        let one = Fixture::new(1, 1_100_000);
        let two = Fixture::new(2, 8);
        let mut journal = open(dir.path());
        assert_eq!(journal.append_group(&two.group()).unwrap(), 0);
        let max = 8 + one.metadata.len() + one.wal_frame.len();
        assert!(
            journal
                .read_durable_exact(2, max, 11, 12)
                .unwrap()
                .is_none()
        );
        assert_eq!(journal.append_group(&one.group()).unwrap(), 2);
        assert!(matches!(
            journal.read_durable_exact(0, max, 11, 12),
            Err(JournalError::InvalidGroup("durable read bounds"))
        ));
        assert!(matches!(
            journal.read_durable_exact(1, 0, 11, 12),
            Err(JournalError::InvalidGroup("durable read bounds"))
        ));
        assert!(matches!(
            journal.read_durable_exact(1, MAX_CANONICAL_BYTES + 1, 11, 12),
            Err(JournalError::InvalidGroup("durable read bounds"))
        ));
        assert!(matches!(
            journal.read_durable_exact(1, max - 1, 11, 12),
            Err(JournalError::Capacity("durable read max bytes"))
        ));
        assert!(
            journal
                .read_durable_exact(u64::MAX, max, 11, 12)
                .unwrap()
                .is_none()
        );
        let before = journal.budget.snapshot();
        let group = journal.read_durable_exact(1, max, 11, 12).unwrap().unwrap();
        let retained = journal.budget.snapshot();
        assert_eq!(retained.reserved_bytes, before.reserved_bytes + max);
        assert_eq!(retained.reserved_items, before.reserved_items + 1);
        assert_eq!(group.metadata(), one.metadata.as_slice());
        assert_eq!(group.wal_frame(), one.wal_frame.as_slice());
        assert_eq!(group.id(), one.id);
        assert_eq!(group.digest(), one.digest);
        assert_eq!(group.transaction_id(), one.transaction_id);
        assert_eq!(group.canonical_bytes().len(), max);
        drop(group);
        let released = journal.budget.snapshot();
        assert_eq!(released.reserved_bytes, before.reserved_bytes);
        assert_eq!(released.reserved_items, before.reserved_items);
        drop(journal);
        let reopened = open(dir.path());
        let group = reopened
            .read_durable_exact(1, max, 13, 14)
            .unwrap()
            .unwrap();
        assert_eq!(group.metadata(), one.metadata.as_slice());
        assert_eq!(group.wal_frame(), one.wal_frame.as_slice());
    }

    #[derive(Clone, Copy, Debug)]
    enum Corruption {
        DescriptorLength,
        DescriptorHash,
        ChunkHeader,
        ChunkIndex,
        NoncanonicalChunkSize,
        CanonicalSize,
        EventDigest,
        WalTransaction,
        MetadataLengthPrefix,
        WalLengthPrefix,
    }

    fn corrupt_durable_group(
        journal: &mut ReplicaJournal,
        fixture: &Fixture,
        corruption: Corruption,
    ) {
        let marker_key = marker_key(fixture.id).unwrap();
        let stored = journal
            .storage
            .get(journal.storage.snapshot(), &marker_key)
            .unwrap()
            .unwrap();
        let mut marker = decode_marker(stored.bytes()).unwrap();
        let mut chunks: Vec<Vec<u8>> = (0..marker.chunks.len())
            .map(|index| {
                let key = chunk_key(fixture.id, u16::try_from(index).unwrap()).unwrap();
                journal
                    .storage
                    .get(journal.storage.snapshot(), &key)
                    .unwrap()
                    .unwrap()
                    .bytes()
                    .to_vec()
            })
            .collect();

        match corruption {
            Corruption::DescriptorLength => marker.chunks[0].len += 1,
            Corruption::DescriptorHash => marker.chunks[0].sha256[0] ^= 1,
            Corruption::ChunkHeader => chunks[0][0] ^= 1,
            Corruption::ChunkIndex => chunks[0][80] ^= 1,
            Corruption::NoncanonicalChunkSize => {
                chunks[0].pop();
                let payload_len = u32::try_from(chunks[0].len() - CHUNK_HEADER_BYTES).unwrap();
                chunks[0][82..86].copy_from_slice(&payload_len.to_le_bytes());
            }
            Corruption::CanonicalSize => marker.wal_len += 1,
            Corruption::EventDigest => {
                marker.digest = EventDigest::from_bytes([42; 32]);
                for chunk in &mut chunks {
                    chunk[48..80].copy_from_slice(&marker.digest.to_bytes());
                }
            }
            Corruption::WalTransaction => {
                marker.transaction_id = TransactionId::new(u64::MAX, [99; 16]);
            }
            Corruption::MetadataLengthPrefix => {
                chunks[0][CHUNK_HEADER_BYTES..CHUNK_HEADER_BYTES + 4]
                    .copy_from_slice(&(marker.metadata_len + 1).to_le_bytes());
            }
            Corruption::WalLengthPrefix => {
                let offset = CHUNK_HEADER_BYTES + 4 + marker.metadata_len as usize;
                chunks[0][offset..offset + 4].copy_from_slice(&(marker.wal_len + 1).to_le_bytes());
            }
        }

        if !matches!(
            corruption,
            Corruption::DescriptorLength | Corruption::DescriptorHash
        ) {
            for (index, chunk) in chunks.into_iter().enumerate() {
                marker.chunks[index] = ChunkDescriptor {
                    len: u32::try_from(chunk.len()).unwrap(),
                    sha256: sha256(&chunk),
                };
                journal
                    .commit_put(
                        chunk_key(fixture.id, u16::try_from(index).unwrap()).unwrap(),
                        chunk,
                    )
                    .unwrap();
            }
        }
        journal
            .commit_put(marker_key, encode_marker(&marker).unwrap())
            .unwrap();
    }

    #[test]
    fn exact_durable_read_rejects_corrupt_marker_chunk_and_canonical_bindings() {
        let cases = [
            (
                Corruption::DescriptorLength,
                "committed chunk digest or length",
            ),
            (
                Corruption::DescriptorHash,
                "committed chunk digest or length",
            ),
            (Corruption::ChunkHeader, "chunk identity"),
            (Corruption::ChunkIndex, "chunk identity"),
            (Corruption::NoncanonicalChunkSize, "noncanonical chunk size"),
            (Corruption::CanonicalSize, "committed group length"),
            (Corruption::EventDigest, "committed event digest"),
            (
                Corruption::WalTransaction,
                "committed WAL transaction identity",
            ),
            (
                Corruption::MetadataLengthPrefix,
                "metadata length differs from marker",
            ),
            (
                Corruption::WalLengthPrefix,
                "WAL length differs from marker",
            ),
        ];

        for (corruption, expected) in cases {
            let dir = TempInbox::new();
            let value_len = if matches!(corruption, Corruption::NoncanonicalChunkSize) {
                1_100_000
            } else {
                8
            };
            let fixture = Fixture::new(1, value_len);
            let mut journal = open(dir.path());
            assert_eq!(journal.append_group(&fixture.group()).unwrap(), 1);
            corrupt_durable_group(&mut journal, &fixture, corruption);

            match journal.read_durable_exact(1, MAX_CANONICAL_BYTES, 11, 12) {
                Err(JournalError::Corrupt(actual)) => {
                    assert_eq!(actual, expected, "{corruption:?}");
                }
                Err(error) => panic!("{corruption:?}: unexpected error: {error}"),
                Ok(_) => panic!("{corruption:?}: accepted corrupt group"),
            }
        }
    }

    #[test]
    fn durable_read_fails_closed_for_missing_or_corrupt_marker_and_needs_reopen() {
        let dir = TempInbox::new();
        let one = Fixture::new(1, 8);
        let mut journal = open(dir.path());
        journal.append_group(&one.group()).unwrap();
        journal.needs_recovery = true;
        assert!(matches!(
            journal.read_durable_exact(2, MAX_CANONICAL_BYTES, 11, 12),
            Err(JournalError::NeedsRecovery)
        ));
        journal.needs_recovery = false;
        let key = marker_key(one.id).unwrap();
        let token = internal_token(journal.storage.now_unix_ms(), journal.retry_window_ms).unwrap();
        journal
            .storage
            .commit_batch(
                journal.storage.snapshot(),
                token,
                &[Mutation::Delete { key }],
            )
            .unwrap();
        assert!(matches!(
            journal.read_durable_exact(1, MAX_CANONICAL_BYTES, 11, 12),
            Err(JournalError::Corrupt("durable marker is missing"))
        ));
        journal
            .commit_put(marker_key(one.id).unwrap(), vec![0; 178])
            .unwrap();
        assert!(matches!(
            journal.read_durable_exact(1, MAX_CANONICAL_BYTES, 11, 12),
            Err(JournalError::Corrupt("marker magic or length"))
        ));
    }
}
