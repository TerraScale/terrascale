//! The private archive record codec and member writer.
//!
//! An archive member is a deterministic, length-prefixed sequence of
//! logical records: documents exactly as the publication journal recorded
//! them, and journal deletes with their exact keys. The encoding carries no
//! page layout, WAL frame, file offset, or path — only typed logical facts
//! — and identical logical input always yields identical bytes, so a
//! recovery-point identity derived from those bytes is stable.
//!
//! A reserved-collection document is archived like any other document,
//! carrying its own collection name: the manifest counts it as a family
//! record, and a restore re-puts it through the Engine transaction seam,
//! which is the sanctioned family write path. The encoding needs no family
//! knowledge of its own.
//!
//! The writer streams records into bounded members: it rotates to a new
//! member file only at record boundaries, computes each member's SHA-256
//! incrementally, and never holds more than one encoded record in memory.
//! Archive bytes never leave `src/backup`.
//!
//! Every frame carries the local durability its commit had achieved when the
//! publication journaled it. That is what makes a durability disclosure
//! durable: the fact lives in the archived bytes the member digests bind, not
//! only in a count on the manifest, so a restore or audit reading the archive
//! sees the same disclosure the run reported.

use std::fs::File;
use std::io::Write;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use crate::encryption::{ArtifactCipher, MAX_PLAINTEXT_BYTES};
use zeroize::Zeroize;

use crate::backup::sha256::{Sha256, to_hex};
use crate::types::{Document, LocalDurability, TenantId};

/// Record tag: one tenant's document in its published form.
const TAG_DOCUMENT: u8 = 1;
/// Record tag: one journal delete.
const TAG_DELETE: u8 = 3;

/// Durability tag: the commit had achieved `Synced` local durability.
const TAG_DURABILITY_SYNCED: u8 = 0;
/// Durability tag: the commit had achieved `Written` local durability.
const TAG_DURABILITY_WRITTEN: u8 = 1;
/// Durability tag: the commit had achieved `Buffered` local durability.
const TAG_DURABILITY_BUFFERED: u8 = 2;

/// Encodes one logical record. Kept as a value so the writer stays free of
/// engine types.
#[derive(Clone, Debug, PartialEq)]
pub(crate) enum Record {
    /// A document as its publication stored it, version included.
    Document {
        /// The owning tenant.
        tenant: TenantId,
        /// The published document.
        document: Document,
        /// The local durability the commit that published this form achieved.
        durability: LocalDurability,
    },
    /// One delete from the publication journal.
    Delete {
        /// The owning tenant.
        tenant: TenantId,
        /// The deleted key's collection, or `None` when nothing existed.
        collection: Option<String>,
        /// The deleted document's identifier.
        id: String,
        /// The version that existed at the deleted key.
        version_at_delete: u64,
        /// The local durability the commit that published this delete
        /// achieved.
        durability: LocalDurability,
    },
}

/// Appends the record's deterministic encoding to `buffer`.
pub(crate) fn encode_into(buffer: &mut Vec<u8>, record: &Record) {
    buffer.reserve(16);
    match record {
        Record::Document { tenant, document, durability } => {
            buffer.push(TAG_DOCUMENT);
            encode_tenant(buffer, tenant);
            encode_durability(buffer, *durability);
            encode_identifier(buffer, document.id().as_str());
            encode_identifier(buffer, document.collection().as_str());
            encode_value(buffer, document.value());
            encode_u64(buffer, document.metadata().version());
        }
        Record::Delete { tenant, collection, id, version_at_delete, durability } => {
            buffer.push(TAG_DELETE);
            encode_tenant(buffer, tenant);
            encode_durability(buffer, *durability);
            match collection {
                Some(name) => {
                    buffer.push(1);
                    encode_identifier(buffer, name);
                }
                None => buffer.push(0),
            }
            encode_identifier(buffer, id);
            encode_u64(buffer, *version_at_delete);
        }
    }
}

/// Appends the durability tag of the record's commit.
fn encode_durability(buffer: &mut Vec<u8>, durability: LocalDurability) {
    buffer.push(match durability {
        LocalDurability::Synced => TAG_DURABILITY_SYNCED,
        LocalDurability::Written => TAG_DURABILITY_WRITTEN,
        LocalDurability::Buffered => TAG_DURABILITY_BUFFERED,
    });
}

fn encode_tenant(buffer: &mut Vec<u8>, tenant: &TenantId) {
    encode_u16_text(buffer, tenant.as_str());
}

fn encode_identifier(buffer: &mut Vec<u8>, text: &str) {
    encode_u16_text(buffer, text);
}

/// Length-prefixed UTF-8 for bounded identifiers (at most 255 bytes).
fn encode_u16_text(buffer: &mut Vec<u8>, text: &str) {
    let bytes = text.as_bytes();
    let length = u16::try_from(bytes.len()).expect("validated identifiers are at most 255 bytes");
    buffer.extend_from_slice(&length.to_le_bytes());
    buffer.extend_from_slice(bytes);
}

fn encode_u64(buffer: &mut Vec<u8>, value: u64) {
    buffer.extend_from_slice(&value.to_le_bytes());
}

fn encode_value(buffer: &mut Vec<u8>, value: &crate::types::DocumentValue) {
    use crate::types::DocumentValue;
    match value {
        DocumentValue::Null => buffer.push(0),
        DocumentValue::Boolean(inner) => {
            buffer.push(1);
            buffer.push(u8::from(*inner));
        }
        DocumentValue::Integer(inner) => {
            buffer.push(2);
            buffer.extend_from_slice(&inner.to_le_bytes());
        }
        DocumentValue::Number(inner) => {
            buffer.push(3);
            buffer.extend_from_slice(&inner.to_le_bytes());
        }
        DocumentValue::String(inner) => {
            buffer.push(4);
            encode_u32_text(buffer, inner);
        }
        DocumentValue::Array(items) => {
            buffer.push(5);
            encode_u64(buffer, items.len() as u64);
            for item in items {
                encode_value(buffer, item);
            }
        }
        DocumentValue::Object(fields) => {
            // A `BTreeMap` iterates in canonical key order, so object
            // encoding is deterministic.
            buffer.push(6);
            encode_u64(buffer, fields.len() as u64);
            for (key, item) in fields {
                encode_u32_text(buffer, key);
                encode_value(buffer, item);
            }
        }
    }
}

fn encode_u32_text(buffer: &mut Vec<u8>, text: &str) {
    let bytes = text.as_bytes();
    let length = u32::try_from(bytes.len()).expect("field text length fits u32");
    buffer.extend_from_slice(&length.to_le_bytes());
    buffer.extend_from_slice(bytes);
}

/// One staged archive member under construction.
struct OpenMember {
    name: String,
    file: Option<File>,
    /// Encrypted members are assembled in bounded memory and only written
    /// after authentication has sealed the complete member. Plain members
    /// retain the streaming file path used by the compatibility format.
    plaintext: Vec<u8>,
    hasher: Sha256,
    length: u64,
}

impl Drop for OpenMember {
    fn drop(&mut self) {
        self.plaintext.zeroize();
    }
}

/// The bounded member writer for one backup run.
///
/// The writer creates member files lazily inside the staging directory the
/// destination provided, rotates at record boundaries once a member reaches
/// the run's byte bound, and reports each sealed member as an ordered
/// `(name, length, digest)` triple for the manifest.
pub(crate) struct MemberWriter {
    directory: PathBuf,
    bound_bytes: u64,
    counter: u64,
    open: Option<OpenMember>,
    sealed: Vec<SealedMember>,
    encryption: Option<Arc<Mutex<ArtifactCipher>>>,
}

/// One sealed member: its ordered name, exact length, and SHA-256 digest.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct SealedMember {
    name: String,
    length: u64,
    digest: String,
}

impl SealedMember {
    /// Returns the member's ordered archive name.
    pub(crate) fn name(&self) -> &str {
        &self.name
    }

    /// Returns the member's exact byte length.
    pub(crate) fn length(&self) -> u64 {
        self.length
    }

    /// Returns the member's SHA-256 digest as lowercase hex.
    pub(crate) fn digest(&self) -> &str {
        &self.digest
    }
}

impl MemberWriter {
    /// Creates a writer that seals members at `bound_bytes` (never splitting
    /// a record) inside `directory`, which the destination already created.
    pub(crate) fn new(directory: PathBuf, bound_bytes: u64) -> Self {
        Self {
            directory,
            bound_bytes,
            counter: 0,
            open: None,
            sealed: Vec::new(),
            encryption: None,
        }
    }

    /// Creates an encrypted writer. The cipher is shared with the manifest
    /// sealing step so every envelope uses one caller-owned ordinal stream.
    pub(crate) fn new_encrypted(
        directory: PathBuf,
        bound_bytes: u64,
        encryption: Arc<Mutex<ArtifactCipher>>,
    ) -> Self {
        Self {
            directory,
            bound_bytes,
            counter: 0,
            open: None,
            sealed: Vec::new(),
            encryption: Some(encryption),
        }
    }

    /// Writes one record, rotating members at record boundaries.
    ///
    /// # Errors
    ///
    /// Returns [`std::io::Error`] when a member file cannot be created or
    /// written; the destination maps that to its typed failure.
    pub(crate) fn write(&mut self, record: &Record) -> std::io::Result<()> {
        if self.open.is_none() {
            let name = format!("member-{:06}", self.counter);
            self.counter += 1;
            let file = self
                .encryption
                .is_none()
                .then(|| File::create(self.directory.join(&name)))
                .transpose()?;
            self.open = Some(OpenMember {
                name,
                file,
                plaintext: Vec::new(),
                hasher: Sha256::new(),
                length: 0,
            });
        }
        let mut encoded = Vec::new();
        encode_into(&mut encoded, record);
        let member = self.open.as_mut().expect("a member is open");
        if let Some(file) = member.file.as_mut() {
            file.write_all(&encoded)?;
        } else {
            if member.plaintext.len().saturating_add(encoded.len()) > MAX_PLAINTEXT_BYTES {
                return Err(std::io::Error::other("encrypted member exceeds plaintext bound"));
            }
            member.plaintext.extend_from_slice(&encoded);
        }
        member.hasher.update(&encoded);
        member.length += encoded.len() as u64;
        // The encoded frame is copied into the member buffer in encrypted
        // mode; do not leave a second plaintext copy in allocator memory.
        encoded.zeroize();
        if member.length >= self.bound_bytes {
            self.seal_open_member()?;
        }
        Ok(())
    }

    /// Seals the open member, if any. Called once at the end of the run.
    ///
    /// # Errors
    ///
    /// Returns [`std::io::Error`] when the member file cannot be flushed.
    pub(crate) fn finish(mut self) -> std::io::Result<Vec<SealedMember>> {
        self.seal_open_member()?;
        Ok(self.sealed)
    }

    fn seal_open_member(&mut self) -> std::io::Result<()> {
        let Some(mut member) = self.open.take() else {
            return Ok(());
        };
        let (length, digest) = if let Some(file) = member.file.take() {
            file.sync_all()?;
            let hasher = std::mem::take(&mut member.hasher);
            (member.length, to_hex(&hasher.finish()))
        } else {
            let cipher = self.encryption.as_ref().expect("encrypted member cipher");
            let plaintext = std::mem::take(&mut member.plaintext);
            let sealed = cipher
                .lock()
                .map_err(|_| std::io::Error::other("encrypted member lock poisoned"))?
                .seal(&plaintext);
            // Clear the independent temporary copy even when the provider
            // rejects the envelope.
            let mut plaintext = plaintext;
            plaintext.zeroize();
            let envelope =
                sealed.map_err(|_| std::io::Error::other("encrypted member sealing failed"))?;
            let mut file = File::create(self.directory.join(&member.name))?;
            file.write_all(&envelope)?;
            file.sync_all()?;
            let mut hasher = Sha256::new();
            hasher.update(&envelope);
            (envelope.len() as u64, to_hex(&hasher.finish()))
        };
        self.sealed.push(SealedMember { name: std::mem::take(&mut member.name), length, digest });
        Ok(())
    }
}
