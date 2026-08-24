//! Private immutable roots, root files, and the published manifest pointer.

use std::{
    collections::BTreeMap,
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
        self.documents
            .iter()
            .find(|(key, _)| key.tenant() == tenant.as_str() && key.document() == id.as_str())
            .map(|(key, document)| (key.clone(), document.clone()))
    }

    /// Applies a validated write set and returns the resulting new table.
    ///
    /// The source table is not modified. Replacement documents receive the
    /// successor of the version currently stored at their key.
    pub(crate) fn apply(&self, tenant: &TenantId, writes: &[StagedWrite]) -> Self {
        let mut documents = self.documents.clone();
        for write in writes {
            match write {
                StagedWrite::Put { document, .. } => {
                    let key = StorageKey::new(tenant, document.collection(), document.id());
                    let version = documents
                        .get(&key)
                        .map_or(1, |existing| existing.metadata().version().saturating_add(1));
                    let mut published = document.clone();
                    published.set_version(version);
                    documents.insert(key, published);
                }
                StagedWrite::Delete { collection: Some(collection), id, .. } => {
                    let key = StorageKey::new(tenant, collection, id);
                    documents.remove(&key);
                }
                StagedWrite::Delete { collection: None, id, .. } => {
                    if let Some((key, _)) = self.find_by_document_id(tenant, id) {
                        documents.remove(&key);
                    }
                }
            }
        }
        Self { documents }
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
        for _ in 0..count {
            let key = StorageKey::decode(reader)?;
            let document = codec::decode_document(reader)?;
            documents.insert(key, document);
        }
        Some((sequence, Self { documents }))
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
