//! Private immutable roots, root files, and the published manifest pointer.

use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    sync::Arc,
};

use crate::types::{Document, DocumentId, TenantId};

use super::{
    codec::{self, Reader, StorageKey},
    intent::StagedWrite,
};

/// Magic bytes prefixing every root file.
const ROOT_MAGIC: &[u8; 8] = b"TBV2ROOT";

/// Magic bytes prefixing the manifest pointer.
const MANIFEST_MAGIC: &[u8; 8] = b"TBV2MANI";

/// The immutable identity of one published root.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct StorageRootId(String);

impl StorageRootId {
    /// Returns the canonical identity text, which is also its file name.
    pub(crate) fn as_str(&self) -> &str {
        &self.0
    }

    /// Rebuilds an identity from text the durable ledger recorded.
    pub(crate) fn from_recorded(text: String) -> Self {
        Self(text)
    }

    /// Builds an identity from its canonical text.
    pub(crate) fn from_encoded(text: &str) -> Option<Self> {
        if text.len() == 32 && text.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            Some(Self(text.to_owned()))
        } else {
            None
        }
    }
}

impl std::fmt::Display for StorageRootId {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// An immutable table of the documents one root contains.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct RootTable {
    documents: BTreeMap<StorageKey, Document>,
    /// The secondary identifier index: every canonical key that stores one
    /// `(tenant, document identifier)` pair, in canonical key order. Point
    /// reads and conflict checks resolve by identifier here instead of
    /// scanning `documents`, whose canonical order is
    /// `(tenant, collection, identifier)` and therefore cannot serve an
    /// identifier-only lookup by range.
    by_document: BTreeMap<(String, String), BTreeSet<StorageKey>>,
}

impl RootTable {
    /// Creates an empty table.
    pub(crate) fn empty() -> Self {
        Self::default()
    }

    /// Returns the current version of one key, if present.
    pub(crate) fn version(&self, key: &StorageKey) -> Option<u64> {
        self.documents.get(key).map(|document| document.metadata().version())
    }

    /// Finds the first document, in canonical key order, whose tenant and
    /// document identifier match.
    pub(crate) fn find_by_document_id(
        &self,
        tenant: &TenantId,
        id: &DocumentId,
    ) -> Option<(StorageKey, Document)> {
        let keys = self
            .by_document
            .get(&(tenant.as_str().to_owned(), id.as_str().to_owned()))?;
        // The set is maintained in canonical key order, so its first entry
        // is exactly the scan's first match.
        let key = keys.iter().next()?;
        let document = self.documents.get(key)?;
        Some((key.clone(), document.clone()))
    }

    /// Returns every document of one tenant at this table's state, in
    /// canonical key order.
    ///
    /// Documents carry their collection, value, and metadata, so callers can
    /// filter and order without a second lookup. The full-table scan is a
    /// tracer-scale implementation limit, not a contract limit.
    pub(crate) fn tenant_documents(&self, tenant: &TenantId) -> Vec<Document> {
        self.documents
            .iter()
            .filter(|(key, _)| key.tenant() == tenant.as_str())
            .map(|(_, document)| document.clone())
            .collect()
    }

    /// Returns up to `limit` documents of one tenant in canonical key order,
    /// resuming at the window boundary named by `after` (W4-CA-7).
    ///
    /// The resume rule is deterministic and lossless: the window resumes
    /// immediately after the first canonical key of this tenant whose
    /// document identifier equals `after`; when no document bears that
    /// identifier, keys whose identifier sorts below it are skipped. With a
    /// tenant-unique identifier this pages the canonical order exactly. When
    /// one identifier is reused across collections, a window that ended past
    /// a later occurrence of its cursor identifier may re-serve the keys
    /// between the first occurrence and the window end — duplicates only,
    /// never losses — which is the at-least-once shape the streaming
    /// contract accepts; a caller paging a whole tenant with reused
    /// identifiers can also cycle, so collection scoped selectors are the
    /// exact path.
    ///
    /// The cursor scan walks the tenant's key range; a keyed secondary
    /// structure is a later optimization, not part of this seam.
    pub(crate) fn tenant_documents_from(
        &self,
        tenant: &TenantId,
        after: Option<&DocumentId>,
        limit: u32,
    ) -> Vec<Document> {
        let limit = usize::try_from(limit).unwrap_or(usize::MAX);
        let Some(after) = after else {
            return self.tenant_documents(tenant).into_iter().take(limit).collect();
        };
        // Resolve the cursor to a canonical position: the first key of this
        // tenant bearing the identifier.
        let resume_after_key = self
            .documents
            .iter()
            .find(|(key, _)| key.tenant() == tenant.as_str() && key.document() == after.as_str())
            .map(|(key, _)| key.clone());
        let mut documents: Vec<Document> = Vec::new();
        let mut skipping_below_cursor = resume_after_key.is_none();
        for (key, document) in self.documents.range((
            match resume_after_key {
                Some(ref key) => std::ops::Bound::Excluded(key),
                None => std::ops::Bound::Unbounded,
            },
            std::ops::Bound::Unbounded,
        )) {
            if key.tenant() != tenant.as_str() {
                continue;
            }
            if skipping_below_cursor && key.document() < after.as_str() {
                continue;
            }
            skipping_below_cursor = false;
            if documents.len() == limit {
                break;
            }
            documents.push(document.clone());
        }
        documents
    }

    /// Returns the document stored at one exact canonical key, if present.
    ///
    /// The publication-history derivation uses this to serve the published
    /// form of a write without re-deriving its version assignment, and the
    /// collection-aware snapshot read uses it to resolve one point read by
    /// its full key rather than by identifier alone.
    pub(crate) fn document(&self, key: &StorageKey) -> Option<Document> {
        self.documents.get(key).cloned()
    }

    /// Applies a validated write set and returns the resulting new table.
    ///
    /// The source table is not modified. Replacement documents receive the
    /// successor of the version currently stored at their key.
    pub(crate) fn apply(&self, tenant: &TenantId, writes: &[StagedWrite]) -> Self {
        let mut documents = self.documents.clone();
        let mut by_document = self.by_document.clone();
        for write in writes {
            match write {
                StagedWrite::Put { document, .. } => {
                    let key = StorageKey::new(tenant, document.collection(), document.id());
                    let version = documents
                        .get(&key)
                        .map_or(1, |existing| existing.metadata().version().saturating_add(1));
                    let mut published = document.clone();
                    published.set_version(version);
                    documents.insert(key.clone(), published);
                    by_document
                        .entry((key.tenant().to_owned(), key.document().to_owned()))
                        .or_default()
                        .insert(key);
                }
                StagedWrite::Delete { collection: Some(collection), id, .. } => {
                    let key = StorageKey::new(tenant, collection, id);
                    documents.remove(&key);
                    remove_from_identifier_index(&mut by_document, &key);
                }
                StagedWrite::Delete { collection: None, id, .. } => {
                    if let Some((key, _)) = self.find_by_document_id(tenant, id) {
                        documents.remove(&key);
                        remove_from_identifier_index(&mut by_document, &key);
                    }
                }
            }
        }
        Self { documents, by_document }
    }

    /// Encodes the table body for one sequence number.
    #[allow(clippy::cast_possible_truncation)]
    fn encode_body(&self, sequence: u64) -> Vec<u8> {
        let mut body = Vec::new();
        codec::push_u64(&mut body, sequence);
        codec::push_u32(&mut body, self.documents.len() as u32);
        for (key, document) in &self.documents {
            key.encode(&mut body);
            codec::encode_document(&mut body, document);
        }
        body
    }

    /// Decodes a table body and its sequence number.
    fn decode_body(reader: &mut Reader<'_>) -> Option<(u64, Self)> {
        let sequence = reader.read_u64()?;
        let count = reader.read_u32()?;
        let mut documents = BTreeMap::new();
        let mut by_document: BTreeMap<(String, String), BTreeSet<StorageKey>> = BTreeMap::new();
        for _ in 0..count {
            let key = StorageKey::decode(reader)?;
            let document = codec::decode_document(reader)?;
            by_document
                .entry((key.tenant().to_owned(), key.document().to_owned()))
                .or_default()
                .insert(key.clone());
            documents.insert(key, document);
        }
        Some((sequence, Self { documents, by_document }))
    }
}

/// Removes one canonical key from the identifier index, dropping the
/// entry when its key set becomes empty.
fn remove_from_identifier_index(
    by_document: &mut BTreeMap<(String, String), BTreeSet<StorageKey>>,
    key: &StorageKey,
) {
    let identifier = (key.tenant().to_owned(), key.document().to_owned());
    if let Some(keys) = by_document.get_mut(&identifier)
        && keys.remove(key)
        && keys.is_empty()
    {
        by_document.remove(&identifier);
    }
}

impl StorageKey {
    // Key construction and accessors live in `codec`; this impl block is
    // intentionally empty so ordering stays defined in one place.
}

/// One published immutable root: identity, sequence, and table.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct StorageRoot {
    /// The root identity, also used as its file name.
    pub(crate) id: StorageRootId,
    /// The storage sequence this root publishes.
    pub(crate) sequence: u64,
    /// The immutable document table.
    pub(crate) table: Arc<RootTable>,
}

impl StorageRoot {
    /// The empty root every fresh store starts from.
    pub(crate) fn empty() -> Self {
        Self {
            id: StorageRootId::from_encoded(&format!("{:016x}{:016x}", 0, 0))
                .expect("canonical zero identity"),
            sequence: 0,
            table: Arc::new(RootTable::empty()),
        }
    }
}

/// Returns the file name of one root identity.
pub(crate) fn root_file_path(dir: &Path, id: &StorageRootId) -> PathBuf {
    dir.join("roots").join(format!("{}.root", id.as_str()))
}

/// Computes the immutable identity of one root without writing it.
pub(crate) fn root_identity_of(sequence: u64, table: &RootTable) -> StorageRootId {
    let checksum = codec::fnv1a(&table.encode_body(sequence));
    StorageRootId(format!("{sequence:016x}{checksum:016x}"))
}

/// Writes one root file and returns the root identity derived from its
/// sequence and content checksum.
pub(crate) fn write_root_file(
    dir: &Path,
    sequence: u64,
    table: &RootTable,
    sync: bool,
) -> io::Result<StorageRootId> {
    let id = root_identity_of(sequence, table);
    let path = root_file_path(dir, &id);
    if !path.exists() {
        fs::create_dir_all(dir.join("roots"))?;
        let mut file = fs::File::create(&path)?;
        file.write_all(ROOT_MAGIC)?;
        let body = table.encode_body(sequence);
        let mut head = Vec::new();
        codec::push_u64(&mut head, codec::fnv1a(&body));
        file.write_all(&head)?;
        file.write_all(&body)?;
        if sync {
            file.sync_data()?;
        }
    }
    Ok(id)
}

/// Loads and verifies one root file. `Ok(None)` means the file is absent.
pub(crate) fn load_root_file(path: &Path) -> Result<Option<(u64, RootTable, StorageRootId)>, ()> {
    let mut bytes = Vec::new();
    let mut file = match fs::File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err(()),
    };
    if file.read_to_end(&mut bytes).is_err() {
        return Err(());
    }
    let mut reader = Reader::new(&bytes);
    let mut magic = [0_u8; 8];
    for slot in &mut magic {
        *slot = reader.read_u8().ok_or(())?;
    }
    if &magic != ROOT_MAGIC {
        return Err(());
    }
    let checksum = reader.read_u64().ok_or(())?;
    let body = reader.take(reader.remaining()).ok_or(())?;
    if codec::fnv1a(body) != checksum {
        return Err(());
    }
    let mut body_reader = Reader::new(body);
    let (sequence, table) = RootTable::decode_body(&mut body_reader).ok_or(())?;
    let id = StorageRootId(format!("{sequence:016x}{checksum:016x}"));
    Ok(Some((sequence, table, id)))
}

/// Writes the temporary manifest naming one root.
pub(crate) fn write_manifest_temp(
    dir: &Path,
    sequence: u64,
    id: &StorageRootId,
    sync: bool,
) -> io::Result<()> {
    let mut body = Vec::new();
    codec::push_u64(&mut body, sequence);
    codec::push_str(&mut body, id.as_str());
    let checksum = codec::fnv1a(&body);
    let mut file = fs::File::create(dir.join("manifest.tmp"))?;
    file.write_all(MANIFEST_MAGIC)?;
    let mut head = Vec::new();
    codec::push_u64(&mut head, checksum);
    file.write_all(&head)?;
    file.write_all(&body)?;
    if sync {
        file.sync_data()?;
    }
    Ok(())
}

/// Atomically publishes the temporary manifest as the root pointer.
pub(crate) fn publish_manifest(dir: &Path) -> io::Result<()> {
    fs::rename(dir.join("manifest.tmp"), dir.join("manifest"))
}

/// Reads the published manifest. `Ok(None)` means no manifest exists yet.
pub(crate) fn read_manifest(dir: &Path) -> Option<(u64, StorageRootId)> {
    let mut bytes = Vec::new();
    let mut file = fs::File::open(dir.join("manifest")).ok()?;
    if file.read_to_end(&mut bytes).is_err() {
        return None;
    }
    let mut reader = Reader::new(&bytes);
    let mut magic = [0_u8; 8];
    for slot in &mut magic {
        *slot = reader.read_u8()?;
    }
    if &magic != MANIFEST_MAGIC {
        return None;
    }
    let checksum = reader.read_u64()?;
    let body = reader.take(reader.remaining())?;
    if codec::fnv1a(body) != checksum {
        return None;
    }
    let mut body_reader = Reader::new(body);
    let sequence = body_reader.read_u64()?;
    let id = StorageRootId::from_encoded(&body_reader.read_string()?)?;
    Some((sequence, id))
}

/// Syncs one file's bytes to the device.
pub(crate) fn sync_file(path: &Path) -> io::Result<()> {
    fs::File::open(path)?.sync_data()
}

/// Syncs a directory entry so a rename or creation survives a crash.
pub(crate) fn sync_dir(dir: &Path) -> io::Result<()> {
    fs::File::open(dir)?.sync_data()
}

/// Scans the `roots` directory for the highest-sequence root file that
/// verifies (GAP-S1 decision record D7).
///
/// This is the recovery fallback when the manifest is absent or does not
/// verify and the write-ahead log no longer carries the frames to rebuild
/// from — the state a compacted location is in after manifest damage. Every
/// candidate must verify as a root file and carry exactly the identity its
/// file name claims; anything else (a torn write, a misplaced file) is
/// skipped, never served. Compaction's rule that the manifest's own root is
/// never deleted guarantees at least one verifying candidate exists.
pub(crate) fn scan_newest_verifying_root(
    dir: &Path,
) -> Result<Option<(u64, RootTable, StorageRootId)>, ()> {
    let roots_dir = dir.join("roots");
    let entries = match fs::read_dir(&roots_dir) {
        Ok(entries) => entries,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err(()),
    };
    let mut newest: Option<(u64, RootTable, StorageRootId)> = None;
    for entry in entries {
        let Ok(entry) = entry else {
            continue;
        };
        let path = entry.path();
        if path.extension().is_none_or(|extension| extension != "root") {
            continue;
        }
        let Some(stem) = path.file_stem().and_then(|stem| stem.to_str()) else {
            continue;
        };
        let Some(claimed) = StorageRootId::from_encoded(stem) else {
            continue;
        };
        let Ok(Some((sequence, table, confirmed))) = load_root_file(&path) else {
            // A torn or damaged candidate is skipped: the fallback serves
            // verified state only, never a partial file.
            continue;
        };
        if confirmed != claimed {
            continue;
        }
        if newest.as_ref().is_none_or(|(known, _, _)| sequence > *known) {
            newest = Some((sequence, table, confirmed));
        }
    }
    Ok(newest)
}
