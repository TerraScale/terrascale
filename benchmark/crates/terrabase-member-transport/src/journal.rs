//! The lane's durable per-Member publication journal.
//!
//! This journal is the member lane's durable stand-in for the Engine's
//! publication-history journal: the same contract shape (global storage
//! sequence, per-tenant sequence mapping, a servable-history bound, a
//! durable operation ledger that binds each operation identity to one write
//! set, journal-derived cursators that survive a restart) over a
//! file-backed, fsync-per-commit record log. The production acquisition
//! seam — real Engines behind this transport — is the integrator's wiring
//! step recorded in the SE-06 docs; everything here is lane-scoped.
//!
//! Fail-closed format verification: the file carries a magic-and-version
//! header, every record carries a SHA-256 digest, and any mismatch —
//! another format, truncated bytes, a bad digest — fails the open with the
//! typed [`JournalError::FormatMismatch`] instead of being repaired or
//! guessed around.

use std::collections::BTreeMap;
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

use ring::digest::{Context, SHA256};

use crate::clock::ClockStamp;
use crate::envelope::{LaneCommit, LaneDocument, LaneWrite, MAX_FIELD_BYTES};
use crate::identity::{CollectionName, DocumentId, MemberId, OperationId, TenantId};

/// The journal file's magic and format version.
const JOURNAL_MAGIC: &[u8; 8] = b"TBV2MLJ1";

/// The digest length written per record.
const DIGEST_BYTES: usize = 32;

/// The maximum number of journal commits held in memory before the oldest
/// history is trimmed (the lane's retained-history bound; the servable bound
/// follows it).
const MAX_RETAINED_COMMITS: usize = 4096;

/// The outcome of one journal record attempt.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RecordOutcome {
    /// The commit was recorded at a fresh sequence.
    Recorded(u64),
    /// The durable ledger had already resolved the operation; the original
    /// sequence replays idempotently.
    Duplicate(u64),
}

/// One ledger-resolved operation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DurableResolution {
    /// The operation is durably committed at its recorded sequence.
    Committed(u64),
    /// The journal holds no record of the operation.
    Unknown,
}

/// Typed journal failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum JournalError {
    /// The location holds another format or bytes that do not verify.
    FormatMismatch,
    /// The location cannot be opened, read, or written.
    Unavailable,
    /// The journal is closed.
    Closed,
    /// The operation identity is already bound to a different write set.
    OperationIdReuse,
}

/// The durable per-Member replication progress bound to journal sequence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LaneCursor {
    member: MemberId,
    high_water: Option<u64>,
    tenants: Vec<(TenantId, u64)>,
}

impl LaneCursor {
    /// Creates the zero-progress cursor of one member.
    #[must_use]
    pub fn start(member: MemberId) -> Self {
        Self { member, high_water: None, tenants: Vec::new() }
    }

    /// Returns the member whose durable progress this cursor reports.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the last sequence the member's own journal durably recorded.
    #[must_use]
    pub fn high_water(&self) -> Option<u64> {
        self.high_water
    }

    /// Returns the member's latest journaled sequence for one tenant.
    #[must_use]
    pub fn tenant_progress(&self, tenant: &TenantId) -> Option<u64> {
        self.tenants.iter().find(|(recorded, _)| recorded == tenant).map(|(_, sequence)| *sequence)
    }

    /// Returns every tenant this cursor records progress for.
    pub fn tenants(&self) -> impl Iterator<Item = &TenantId> {
        self.tenants.iter().map(|(tenant, _)| tenant)
    }

    /// Reports whether the cursor records no durable progress.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.high_water.is_none() && self.tenants.is_empty()
    }
}

/// One journaled record: a tenant's commit, or a trim marker.
#[derive(Clone, Debug, Eq, PartialEq)]
enum Record {
    /// One durably recorded commit with its writer stamp, for one tenant.
    Commit { tenant: TenantId, commit: LaneCommit },
    /// History through `through` (inclusive) was trimmed and is no longer
    /// servable.
    Trim { through: u64 },
}

/// The lane's durable publication journal for one Member.
pub struct MemberJournal {
    dir: PathBuf,
    file: Option<File>,
    commits: Vec<(TenantId, LaneCommit)>,
    tenant_sequences: BTreeMap<TenantId, u64>,
    high_water: Option<u64>,
    servable_from: Option<u64>,
    ledger: BTreeMap<String, u64>,
    write_set_digests: BTreeMap<String, Vec<u8>>,
    documents: BTreeMap<(TenantId, DocumentId), (CollectionName, String, u64)>,
    versions: BTreeMap<(TenantId, DocumentId), u64>,
}

impl MemberJournal {
    /// Opens (or creates) the journal at one durable location.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::FormatMismatch`] when the location holds
    /// another format or bytes that do not verify, and
    /// [`JournalError::Unavailable`] when it cannot be read.
    pub fn open(dir: &Path) -> Result<Self, JournalError> {
        std::fs::create_dir_all(dir).map_err(|_| JournalError::Unavailable)?;
        let path = dir.join("member-journal.log");
        let mut file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&path)
            .map_err(|_| JournalError::Unavailable)?;
        let mut bytes = Vec::new();
        file.read_to_end(&mut bytes).map_err(|_| JournalError::Unavailable)?;
        let mut journal = Self {
            dir: dir.to_path_buf(),
            file: None,
            commits: Vec::new(),
            tenant_sequences: BTreeMap::new(),
            high_water: None,
            servable_from: None,
            ledger: BTreeMap::new(),
            write_set_digests: BTreeMap::new(),
            documents: BTreeMap::new(),
            versions: BTreeMap::new(),
        };
        if bytes.is_empty() {
            file.write_all(JOURNAL_MAGIC)
                .and_then(|()| file.sync_data())
                .map_err(|_| JournalError::Unavailable)?;
            journal.file = Some(file);
            return Ok(journal);
        }
        if bytes.len() < JOURNAL_MAGIC.len() || &bytes[..JOURNAL_MAGIC.len()] != JOURNAL_MAGIC {
            return Err(JournalError::FormatMismatch);
        }
        let mut offset = JOURNAL_MAGIC.len();
        while offset < bytes.len() {
            let Some(record_len_u32) = decode_u32(&bytes[offset..]) else {
                return Err(JournalError::FormatMismatch);
            };
            let record_len = record_len_u32 as usize;
            offset += 4;
            if bytes.len() < offset + record_len + DIGEST_BYTES {
                return Err(JournalError::FormatMismatch);
            }
            let payload = &bytes[offset..offset + record_len];
            offset += record_len;
            let digest = &bytes[offset..offset + DIGEST_BYTES];
            offset += DIGEST_BYTES;
            let mut context = Context::new(&SHA256);
            context.update(&record_len_u32.to_le_bytes());
            context.update(payload);
            if context.finish().as_ref() != digest {
                return Err(JournalError::FormatMismatch);
            }
            let record = decode_record(payload).ok_or(JournalError::FormatMismatch)?;
            journal.replay(record);
        }
        journal.file = Some(file);
        Ok(journal)
    }

    /// Returns the journal's durable location (the lane's own temp root;
    /// never recorded in receipts by design).
    #[must_use]
    pub fn location(&self) -> &Path {
        &self.dir
    }

    /// Replays one record into the in-memory state.
    fn replay(&mut self, record: Record) {
        match record {
            Record::Commit { tenant, commit } => {
                self.tenant_sequences.insert(tenant.clone(), commit.sequence);
                self.ledger.insert(commit.operation.as_str().to_owned(), commit.sequence);
                self.write_set_digests
                    .insert(commit.operation.as_str().to_owned(), write_set_digest(&commit.writes));
                self.high_water =
                    Some(self.high_water.map_or(commit.sequence, |high| high.max(commit.sequence)));
                if self.servable_from.is_none() {
                    self.servable_from = Some(commit.sequence);
                }
                self.apply_to_documents(&tenant, &commit.writes);
                self.commits.push((tenant, commit));
            }
            Record::Trim { through } => {
                self.commits.retain(|(_, commit)| commit.sequence > through);
                if let Some((_, first)) = self.commits.first() {
                    self.servable_from = Some(first.sequence);
                } else {
                    self.servable_from = self.high_water;
                }
            }
        }
    }

    /// Durably records one commit under `tenant`, assigning the next journal
    /// sequence.
    ///
    /// The durable operation ledger decides duplicates: the same operation
    /// identity under the same write set replays idempotently to its
    /// original sequence; the same identity under a different write set is
    /// the typed [`JournalError::OperationIdReuse`] rejection, never a
    /// silent skip. The commit's carried sequence is ignored — the journal
    /// assigns its own.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::Closed`], [`JournalError::OperationIdReuse`],
    /// or [`JournalError::Unavailable`] on the io failure.
    pub fn record_commit(
        &mut self,
        tenant: &TenantId,
        commit: &LaneCommit,
    ) -> Result<RecordOutcome, JournalError> {
        let key = commit.operation.as_str().to_owned();
        let digest = write_set_digest(&commit.writes);
        if let Some(sequence) = self.ledger.get(&key) {
            if self.write_set_digests.get(&key).is_some_and(|existing| *existing == digest) {
                return Ok(RecordOutcome::Duplicate(*sequence));
            }
            return Err(JournalError::OperationIdReuse);
        }
        let sequence = self.high_water.map_or(1, |high| high + 1);
        let commit = LaneCommit { sequence, ..commit.clone() };
        let record = Record::Commit { tenant: tenant.clone(), commit: commit.clone() };
        self.append(&record)?;
        if self.commits.len() > MAX_RETAINED_COMMITS {
            let through = self.commits[0].1.sequence;
            self.append(&Record::Trim { through })?;
        }
        self.replay(Record::Commit { tenant: tenant.clone(), commit });
        Ok(RecordOutcome::Recorded(sequence))
    }

    /// Appends one framed record to the durable file.
    #[allow(
        clippy::cast_possible_truncation,
        reason = "the record payload is bounded by the retained-history and payload bounds; a length beyond u32 cannot occur"
    )]
    fn append(&mut self, record: &Record) -> Result<(), JournalError> {
        let file = self.file.as_mut().ok_or(JournalError::Closed)?;
        let payload = encode_record(record);
        let mut framed = Vec::with_capacity(payload.len() + DIGEST_BYTES + 4);
        framed.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        framed.extend_from_slice(&payload);
        let digest = {
            let mut context = Context::new(&SHA256);
            context.update(&(payload.len() as u32).to_le_bytes());
            context.update(&payload);
            context.finish()
        };
        framed.extend_from_slice(digest.as_ref());
        file.write_all(&framed)
            .and_then(|()| file.sync_data())
            .map_err(|_| JournalError::Unavailable)
    }

    /// Trims journal history through `sequence` (inclusive), advancing the
    /// servable bound: cursors below it can no longer be served.
    ///
    /// # Errors
    ///
    /// Returns the journal's typed failures.
    pub fn trim_through(&mut self, sequence: u64) -> Result<(), JournalError> {
        self.append(&Record::Trim { through: sequence })?;
        self.replay(Record::Trim { through: sequence });
        Ok(())
    }

    /// Resolves one operation through the durable ledger.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::Closed`] while the journal is closed.
    pub fn resolve(&self, operation: &OperationId) -> Result<DurableResolution, JournalError> {
        if self.file.is_none() {
            return Err(JournalError::Closed);
        }
        Ok(match self.ledger.get(operation.as_str()) {
            Some(sequence) => DurableResolution::Committed(*sequence),
            None => DurableResolution::Unknown,
        })
    }

    /// Returns one tenant's published commits after `after`, in journal
    /// sequence order, up to `limit` entries.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::Closed`] while the journal is closed.
    pub fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: u64,
        limit: u32,
    ) -> Result<Vec<LaneCommit>, JournalError> {
        if self.file.is_none() {
            return Err(JournalError::Closed);
        }
        Ok(self
            .commits
            .iter()
            .filter(|(recorded, commit)| commit.sequence > after && recorded == tenant)
            .map(|(_, commit)| commit.clone())
            .take(limit as usize)
            .collect())
    }

    /// Returns every tenant with at least one journaled commit.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::Closed`] while the journal is closed.
    pub fn committed_tenants(&self) -> Result<Vec<TenantId>, JournalError> {
        if self.file.is_none() {
            return Err(JournalError::Closed);
        }
        Ok(self.tenant_sequences.keys().cloned().collect())
    }

    /// Returns the tenant's latest journaled sequence at or below `at`.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::Closed`] while the journal is closed.
    pub fn tenant_sequence(&self, tenant: &TenantId, at: u64) -> Result<Option<u64>, JournalError> {
        if self.file.is_none() {
            return Err(JournalError::Closed);
        }
        Ok(self
            .commits
            .iter()
            .filter(|(recorded, commit)| commit.sequence <= at && recorded == tenant)
            .map(|(_, commit)| commit.sequence)
            .max())
    }

    /// Returns the oldest sequence the journal can serve.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::Closed`] while the journal is closed.
    pub fn servable_history_start(&self) -> Result<Option<u64>, JournalError> {
        if self.file.is_none() {
            return Err(JournalError::Closed);
        }
        Ok(self.servable_from)
    }

    /// Returns the last journal sequence durably recorded.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::Closed`] while the journal is closed.
    pub fn journaled_high_water(&self) -> Result<Option<u64>, JournalError> {
        if self.file.is_none() {
            return Err(JournalError::Closed);
        }
        Ok(self.high_water)
    }

    /// Returns the tenant's documents as the journal's derived state holds
    /// them, in canonical key order.
    ///
    /// # Errors
    ///
    /// Returns [`JournalError::Closed`] while the journal is closed.
    pub fn documents_at(&self, tenant: &TenantId) -> Result<Vec<LaneDocument>, JournalError> {
        if self.file.is_none() {
            return Err(JournalError::Closed);
        }
        Ok(self
            .documents
            .iter()
            .filter(|((recorded, _), _)| recorded == tenant)
            .map(|((_, id), (collection, value, _))| LaneDocument {
                id: id.clone(),
                collection: collection.clone(),
                value: value.clone(),
            })
            .collect())
    }

    /// Derives the member's durable cursor from the journal: the ground
    /// truth of what the member durably applied.
    ///
    /// # Errors
    ///
    /// Returns the journal's typed failures.
    pub fn cursor(&self, member: &MemberId) -> Result<LaneCursor, JournalError> {
        let high_water = self.journaled_high_water()?;
        let mut tenants = Vec::new();
        for tenant in self.committed_tenants()? {
            let progress = self.tenant_sequence(&tenant, high_water.unwrap_or(0))?;
            if let Some(sequence) = progress {
                tenants.push((tenant, sequence));
            }
        }
        Ok(LaneCursor { member: member.clone(), high_water, tenants })
    }

    /// Applies one commit's writes to the derived document state.
    fn apply_to_documents(&mut self, tenant: &TenantId, writes: &[LaneWrite]) {
        for write in writes {
            match write {
                LaneWrite::Put { document } => {
                    let key = (tenant.clone(), document.id.clone());
                    let version = self.versions.get(&key).copied().unwrap_or(0) + 1;
                    self.versions.insert(key.clone(), version);
                    self.documents.insert(
                        key,
                        (document.collection.clone(), document.value.clone(), version),
                    );
                }
                LaneWrite::Delete { id, .. } => {
                    let key = (tenant.clone(), id.clone());
                    let version = self.versions.get(&key).copied().unwrap_or(0) + 1;
                    self.versions.insert(key.clone(), version);
                    self.documents.remove(&key);
                }
            }
        }
    }

    /// Closes the journal; the durable location keeps its bytes.
    pub fn close(&mut self) {
        self.file = None;
    }

    /// Reports whether the journal is closed.
    #[must_use]
    pub fn is_closed(&self) -> bool {
        self.file.is_none()
    }

    /// Reopens the journal from its durable location after a close.
    ///
    /// # Errors
    ///
    /// Returns the open's typed failures.
    pub fn reopen(&mut self) -> Result<(), JournalError> {
        let dir = self.dir.clone();
        *self = Self::open(&dir)?;
        Ok(())
    }
}

/// Returns the SHA-256 digest of one canonical write-set encoding: the
/// ledger's binding of an operation identity to its write set.
fn write_set_digest(writes: &[LaneWrite]) -> Vec<u8> {
    let mut buffer = Vec::new();
    for write in writes {
        match write {
            LaneWrite::Put { document } => {
                buffer.push(1);
                put_str(&mut buffer, document.id.as_str());
                put_str(&mut buffer, document.collection.as_str());
                put_str(&mut buffer, &document.value);
            }
            LaneWrite::Delete { collection, id, version_at_delete } => {
                buffer.push(2);
                match collection {
                    Some(collection) => {
                        buffer.push(1);
                        put_str(&mut buffer, collection.as_str());
                    }
                    None => buffer.push(0),
                }
                put_str(&mut buffer, id.as_str());
                buffer.extend_from_slice(&version_at_delete.to_le_bytes());
            }
        }
    }
    let mut context = Context::new(&SHA256);
    context.update(&buffer);
    context.finish().as_ref().to_vec()
}

#[allow(
    clippy::cast_possible_truncation,
    reason = "the record payload is bounded by the retained-history and payload bounds; a length beyond u32 cannot occur"
)]
fn encode_record(record: &Record) -> Vec<u8> {
    let mut out = Vec::new();
    match record {
        Record::Commit { tenant, commit } => {
            out.push(1);
            put_str(&mut out, tenant.as_str());
            put_str(&mut out, commit.operation.as_str());
            out.extend_from_slice(&commit.stamp.ticks().to_le_bytes());
            out.extend_from_slice(&commit.stamp.counter().to_le_bytes());
            out.extend_from_slice(&commit.sequence.to_le_bytes());
            out.extend_from_slice(&(commit.writes.len() as u32).to_le_bytes());
            for write in &commit.writes {
                match write {
                    LaneWrite::Put { document } => {
                        out.push(1);
                        put_str(&mut out, document.id.as_str());
                        put_str(&mut out, document.collection.as_str());
                        put_str(&mut out, &document.value);
                    }
                    LaneWrite::Delete { collection, id, version_at_delete } => {
                        out.push(2);
                        match collection {
                            Some(collection) => {
                                out.push(1);
                                put_str(&mut out, collection.as_str());
                            }
                            None => out.push(0),
                        }
                        put_str(&mut out, id.as_str());
                        out.extend_from_slice(&version_at_delete.to_le_bytes());
                    }
                }
            }
        }
        Record::Trim { through } => {
            out.push(2);
            out.extend_from_slice(&through.to_le_bytes());
        }
    }
    out
}

fn decode_record(payload: &[u8]) -> Option<Record> {
    let tag = *payload.first()?;
    match tag {
        1 => {
            let (tenant, offset) = take_str(payload, 1)?;
            let (operation, offset) = take_str(payload, offset)?;
            if payload.len() < offset + 24 {
                return None;
            }
            let ticks = le_u64(payload, offset);
            let counter = le_u64(payload, offset + 8);
            let sequence = le_u64(payload, offset + 16);
            let offset = offset + 24;
            let count = le_u32(payload, offset) as usize;
            let offset = offset + 4;
            let mut writes = Vec::with_capacity(count);
            let mut cursor = offset;
            for _ in 0..count {
                let write_tag = *payload.get(cursor)?;
                cursor += 1;
                match write_tag {
                    1 => {
                        let (id, next) = take_str(payload, cursor)?;
                        let (collection, next) = take_str(payload, next)?;
                        let (value, next) = take_str(payload, next)?;
                        cursor = next;
                        writes.push(LaneWrite::Put {
                            document: LaneDocument {
                                id: DocumentId::try_new(&id).ok()?,
                                collection: CollectionName::try_new(&collection).ok()?,
                                value,
                            },
                        });
                    }
                    2 => {
                        let has_collection = *payload.get(cursor)?;
                        cursor += 1;
                        let collection = if has_collection == 1 {
                            let (collection, next) = take_str(payload, cursor)?;
                            cursor = next;
                            Some(CollectionName::try_new(&collection).ok()?)
                        } else {
                            None
                        };
                        let (id, next) = take_str(payload, cursor)?;
                        cursor = next;
                        if payload.len() < cursor + 8 {
                            return None;
                        }
                        let version_at_delete = le_u64(payload, cursor);
                        cursor += 8;
                        writes.push(LaneWrite::Delete {
                            collection,
                            id: DocumentId::try_new(&id).ok()?,
                            version_at_delete,
                        });
                    }
                    _ => return None,
                }
            }
            let _ = cursor;
            Some(Record::Commit {
                tenant: TenantId::try_new(&tenant).ok()?,
                commit: LaneCommit {
                    operation: OperationId::try_new(&operation).ok()?,
                    stamp: ClockStamp::new(ticks, counter),
                    sequence,
                    writes,
                },
            })
        }
        2 => {
            if payload.len() < 9 {
                return None;
            }
            Some(Record::Trim { through: le_u64(payload, 1) })
        }
        _ => None,
    }
}

#[allow(
    clippy::cast_possible_truncation,
    reason = "identifier and field text is bounded by the identifier rules; a length beyond u32 cannot occur"
)]
fn put_str(out: &mut Vec<u8>, text: &str) {
    out.extend_from_slice(&(text.len() as u32).to_le_bytes());
    out.extend_from_slice(text.as_bytes());
}

fn take_str(payload: &[u8], offset: usize) -> Option<(String, usize)> {
    if payload.len() < offset + 4 {
        return None;
    }
    let len = le_u32(payload, offset) as usize;
    let start = offset + 4;
    if payload.len() < start + len || len > MAX_FIELD_BYTES as usize {
        return None;
    }
    let text = String::from_utf8(payload[start..start + len].to_vec()).ok()?;
    Some((text, start + len))
}

fn le_u32(payload: &[u8], at: usize) -> u32 {
    let mut words = [0_u8; 4];
    words.copy_from_slice(&payload[at..at + 4]);
    u32::from_le_bytes(words)
}

fn le_u64(payload: &[u8], at: usize) -> u64 {
    let mut words = [0_u8; 8];
    words.copy_from_slice(&payload[at..at + 8]);
    u64::from_le_bytes(words)
}

fn decode_u32(bytes: &[u8]) -> Option<u32> {
    if bytes.len() < 4 {
        return None;
    }
    Some(le_u32(bytes, 0))
}

#[cfg(test)]
mod tests {
    use super::{DurableResolution, JournalError, MemberJournal, RecordOutcome};
    use crate::clock::ClockStamp;
    use crate::envelope::{LaneCommit, LaneDocument, LaneWrite};
    use crate::identity::{CollectionName, DocumentId, MemberId, OperationId, TenantId};

    fn dir(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "tbv2-se06-journal-{tag}-{}-{}",
            std::process::id(),
            line!()
        ));
        std::fs::remove_dir_all(&dir).ok();
        dir
    }

    fn commit(operation: &str, sequence: u64, value: &str) -> LaneCommit {
        LaneCommit {
            operation: OperationId::try_new(operation).expect("valid"),
            stamp: ClockStamp::new(1, 1),
            sequence,
            writes: vec![LaneWrite::Put {
                document: LaneDocument {
                    id: DocumentId::try_new("doc-1").expect("valid"),
                    collection: CollectionName::try_new("users").expect("valid"),
                    value: value.to_owned(),
                },
            }],
        }
    }

    #[test]
    fn commits_survive_a_close_and_reopen_with_the_same_cursor() {
        let location = dir("reopen");
        let tenant = TenantId::try_new("tenant-a").expect("valid");
        let member = MemberId::try_new("m-a1").expect("valid");
        {
            let mut journal = MemberJournal::open(&location).expect("opens");
            assert_eq!(
                journal.record_commit(&tenant, &commit("op-1", 0, "v1")),
                Ok(RecordOutcome::Recorded(1))
            );
            assert_eq!(
                journal.record_commit(&tenant, &commit("op-2", 0, "v2")),
                Ok(RecordOutcome::Recorded(2))
            );
            let cursor = journal.cursor(&member).expect("cursor");
            assert_eq!(cursor.high_water(), Some(2));
        }
        let journal = MemberJournal::open(&location).expect("reopens");
        let cursor = journal.cursor(&member).expect("cursor");
        assert_eq!(cursor.high_water(), Some(2));
        assert_eq!(journal.committed_tenants(), Ok(vec![tenant.clone()]));
        assert_eq!(
            journal.resolve(&OperationId::try_new("op-1").expect("valid")),
            Ok(DurableResolution::Committed(1))
        );
        std::fs::remove_dir_all(&location).ok();
    }

    #[test]
    fn the_ledger_decides_duplicates_and_reuse_fail_closed() {
        let location = dir("ledger");
        let tenant = TenantId::try_new("tenant-a").expect("valid");
        let mut journal = MemberJournal::open(&location).expect("opens");
        assert_eq!(
            journal.record_commit(&tenant, &commit("op-1", 0, "same")),
            Ok(RecordOutcome::Recorded(1))
        );
        assert_eq!(
            journal.record_commit(&tenant, &commit("op-1", 0, "same")),
            Ok(RecordOutcome::Duplicate(1))
        );
        assert_eq!(
            journal.record_commit(&tenant, &commit("op-1", 0, "different")),
            Err(JournalError::OperationIdReuse)
        );
        std::fs::remove_dir_all(&location).ok();
    }

    #[test]
    fn a_location_that_does_not_verify_fails_the_open() {
        let location = dir("corrupt");
        std::fs::create_dir_all(&location).expect("dir");
        std::fs::write(location.join("member-journal.log"), b"not-a-journal-at-all")
            .expect("write");
        assert!(matches!(MemberJournal::open(&location), Err(JournalError::FormatMismatch)));
        std::fs::remove_dir_all(&location).ok();
    }

    #[test]
    fn trim_advances_the_servable_bound() {
        let location = dir("trim");
        let tenant = TenantId::try_new("tenant-a").expect("valid");
        let mut journal = MemberJournal::open(&location).expect("opens");
        journal.record_commit(&tenant, &commit("op-1", 0, "v1")).expect("commit");
        journal.record_commit(&tenant, &commit("op-2", 0, "v2")).expect("commit");
        journal.record_commit(&tenant, &commit("op-3", 0, "v3")).expect("commit");
        journal.trim_through(1).expect("trim");
        assert_eq!(journal.servable_history_start(), Ok(Some(2)));
        assert_eq!(
            journal.tenant_commits(&tenant, 0, 10).expect("commits").len(),
            2,
            "trimmed history is not served"
        );
        std::fs::remove_dir_all(&location).ok();
    }
}
