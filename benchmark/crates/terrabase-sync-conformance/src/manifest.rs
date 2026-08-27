//! The corpus integrity manifest: the machine-readable digest list over
//! the SE-04 golden-vector corpus. An SDK's CI vendors the corpus
//! directory plus this manifest and verifies the digests before running
//! any vector — integrity without a Rust toolchain (PD-2026-08-26-12
//! D1: the corpus is the compatibility contract; SE-05 makes it
//! tamper-evident).

use std::fmt;
use std::fs;
use std::path::Path;

use crate::is_corpus_file;

use serde_json::Value;
use serde_json::json;

use crate::MANIFEST_SCHEMA;
use crate::PROTOCOL;
use crate::PROTOCOL_VERSION;
use crate::sha256::Sha256;
use crate::sha256::to_hex;

/// The error of manifest construction and parsing. Carries reason text
/// only — never corpus bytes or host paths.
#[derive(Debug, PartialEq)]
pub struct ManifestError(String);

impl fmt::Display for ManifestError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "corpus manifest error: {}", self.0)
    }
}

impl std::error::Error for ManifestError {}

fn err(reason: impl Into<String>) -> ManifestError {
    ManifestError(reason.into())
}

/// One manifest entry: a corpus file's identity, digest, and size.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ManifestEntry {
    category: String,
    file: String,
    sha256: String,
    bytes: u64,
}

impl ManifestEntry {
    /// The corpus category the file covers.
    #[must_use]
    pub fn category(&self) -> &str {
        &self.category
    }

    /// The file name within the corpus directory (never a path).
    #[must_use]
    pub fn file(&self) -> &str {
        &self.file
    }

    /// The file's lowercase-hex SHA-256 digest.
    #[must_use]
    pub fn sha256(&self) -> &str {
        &self.sha256
    }

    /// The file's size in bytes.
    #[must_use]
    pub fn bytes(&self) -> u64 {
        self.bytes
    }
}

/// The corpus manifest: every corpus file, digested.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CorpusManifest {
    schema: String,
    protocol: String,
    protocol_version: u64,
    vectors: Vec<ManifestEntry>,
}

impl CorpusManifest {
    /// Builds the manifest of a corpus directory: every `*.json` file,
    /// digested and sized, one entry per file, sorted by file name.
    ///
    /// # Errors
    ///
    /// Returns [`ManifestError`] when the directory cannot be read or a
    /// file is not valid JSON carrying a string `category`.
    pub fn build(corpus_dir: &Path) -> Result<Self, ManifestError> {
        let entries = fs::read_dir(corpus_dir).map_err(|e| {
            err(format!("the corpus directory {} is unreadable: {e}", corpus_dir.display()))
        })?;
        let mut files: Vec<String> = entries
            .flatten()
            .map(|entry| entry.file_name().to_string_lossy().into_owned())
            .filter(|name| is_corpus_file(name))
            .collect();
        files.sort();
        let mut vectors = Vec::with_capacity(files.len());
        for file in files {
            let bytes = fs::read(corpus_dir.join(&file))
                .map_err(|e| err(format!("the vector file {file} is unreadable: {e}")))?;
            let value: Value = serde_json::from_slice(&bytes)
                .map_err(|e| err(format!("the vector file {file} is not valid JSON: {e}")))?;
            let category = value
                .get("category")
                .and_then(Value::as_str)
                .ok_or_else(|| err(format!("the vector file {file} carries no category")))?;
            let mut hasher = Sha256::new();
            hasher.update(&bytes);
            vectors.push(ManifestEntry {
                category: category.to_string(),
                file,
                sha256: to_hex(&hasher.finish()),
                bytes: bytes.len() as u64,
            });
        }
        Ok(Self {
            schema: MANIFEST_SCHEMA.to_string(),
            protocol: PROTOCOL.to_string(),
            protocol_version: PROTOCOL_VERSION,
            vectors,
        })
    }

    /// Parses a manifest, failing closed on unknown schema identities,
    /// foreign protocols, and malformed entries.
    ///
    /// # Errors
    ///
    /// Returns [`ManifestError`] naming the first violated invariant.
    pub fn from_json_str(text: &str) -> Result<Self, ManifestError> {
        let value: Value = serde_json::from_str(text)
            .map_err(|e| err(format!("the manifest is not valid JSON: {e}")))?;
        let schema = value
            .get("schema")
            .and_then(Value::as_str)
            .ok_or_else(|| err("the manifest carries no schema identity"))?;
        if schema != MANIFEST_SCHEMA {
            return Err(err(format!(
                "unknown manifest schema {schema} (expected {MANIFEST_SCHEMA})"
            )));
        }
        let protocol = value
            .get("protocol")
            .and_then(Value::as_str)
            .ok_or_else(|| err("the manifest carries no protocol identity"))?;
        if protocol != PROTOCOL {
            return Err(err(format!("the manifest names the protocol {protocol}, not {PROTOCOL}")));
        }
        let version = value
            .get("protocol_version")
            .and_then(Value::as_u64)
            .ok_or_else(|| err("the manifest carries no protocol version"))?;
        if version != PROTOCOL_VERSION {
            return Err(err(format!(
                "the manifest names protocol version {version}, not {PROTOCOL_VERSION}"
            )));
        }
        let raw = value
            .get("vectors")
            .and_then(Value::as_array)
            .ok_or_else(|| err("the manifest carries no vector list"))?;
        let mut vectors = Vec::with_capacity(raw.len());
        for entry in raw {
            let category = entry
                .get("category")
                .and_then(Value::as_str)
                .ok_or_else(|| err("a manifest entry carries no category"))?;
            let file = entry
                .get("file")
                .and_then(Value::as_str)
                .ok_or_else(|| err("a manifest entry carries no file name"))?;
            if file.contains('/') || file.contains('\\') {
                return Err(err(format!(
                    "the manifest entry for {category} carries a path, not a file name"
                )));
            }
            let sha256 = entry
                .get("sha256")
                .and_then(Value::as_str)
                .ok_or_else(|| err(format!("the manifest entry for {file} carries no digest")))?;
            if sha256.len() != 64
                || !sha256.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
            {
                return Err(err(format!(
                    "the manifest entry for {file} carries a malformed digest"
                )));
            }
            let bytes = entry.get("bytes").and_then(Value::as_u64).ok_or_else(|| {
                err(format!("the manifest entry for {file} carries no byte count"))
            })?;
            vectors.push(ManifestEntry {
                category: category.to_string(),
                file: file.to_string(),
                sha256: sha256.to_string(),
                bytes,
            });
        }
        let mut seen_categories = vectors.iter().map(|e| e.category.clone()).collect::<Vec<_>>();
        seen_categories.sort();
        seen_categories.dedup();
        if seen_categories.len() != vectors.len() {
            return Err(err("the manifest repeats a category"));
        }
        Ok(Self {
            schema: schema.to_string(),
            protocol: protocol.to_string(),
            protocol_version: version,
            vectors,
        })
    }

    /// The manifest's schema identity.
    #[must_use]
    pub fn schema(&self) -> &str {
        &self.schema
    }

    /// The manifest's entries, one per corpus file.
    #[must_use]
    pub fn vectors(&self) -> &[ManifestEntry] {
        &self.vectors
    }

    /// The manifest's canonical JSON (pretty, deterministic).
    ///
    /// # Panics
    ///
    /// Panics only if serialization fails, which the manifest's
    /// JSON-only field types preclude.
    #[must_use]
    pub fn to_json_string(&self) -> String {
        serde_json::to_string_pretty(&self.to_json()).expect("the manifest serializes")
    }

    fn to_json(&self) -> Value {
        json!({
            "schema": self.schema,
            "protocol": self.protocol,
            "protocol_version": self.protocol_version,
            "vectors": self
                .vectors
                .iter()
                .map(|entry| {
                    json!({
                        "category": entry.category,
                        "file": entry.file,
                        "sha256": entry.sha256,
                        "bytes": entry.bytes,
                    })
                })
                .collect::<Vec<_>>(),
        })
    }

    /// Verifies a corpus directory against this manifest: every
    /// manifest entry's file must exist with the recorded digest and
    /// size, and every corpus file must have an entry. Returns the
    /// problems; an empty list is a verified corpus.
    ///
    /// Problems carry file names and reasons, never corpus bytes or
    /// host paths.
    ///
    /// # Errors
    ///
    /// Returns [`ManifestError`] only when the corpus directory itself
    /// cannot be read; file-level disagreement is reported through the
    /// returned problem list.
    pub fn verify(&self, corpus_dir: &Path) -> Result<Vec<String>, ManifestError> {
        let mut problems = Vec::new();
        for entry in &self.vectors {
            match fs::read(corpus_dir.join(&entry.file)) {
                Err(_) => {
                    problems.push(format!("{}: missing from the corpus", entry.file));
                }
                Ok(bytes) => {
                    let mut hasher = Sha256::new();
                    hasher.update(&bytes);
                    let actual = to_hex(&hasher.finish());
                    if actual != entry.sha256 {
                        problems.push(format!(
                            "{}: sha256 mismatch (the manifest records {}, the corpus holds {actual})",
                            entry.file, entry.sha256
                        ));
                    } else if bytes.len() as u64 != entry.bytes {
                        problems.push(format!(
                            "{}: byte count mismatch (the manifest records {}, the corpus holds {})",
                            entry.file,
                            entry.bytes,
                            bytes.len()
                        ));
                    }
                }
            }
        }
        let listed = fs::read_dir(corpus_dir).map_err(|e| {
            err(format!("the corpus directory {} is unreadable: {e}", corpus_dir.display()))
        })?;
        let present: Vec<String> = listed
            .flatten()
            .map(|e| e.file_name().to_string_lossy().into_owned())
            .filter(|name| is_corpus_file(name))
            .collect();
        for file in present {
            if !self.vectors.iter().any(|entry| entry.file == file) {
                problems.push(format!("{file}: corpus file without a manifest entry"));
            }
        }
        Ok(problems)
    }
}

#[cfg(test)]
mod tests {
    use super::CorpusManifest;

    #[test]
    fn a_well_formed_manifest_parses_and_round_trips() {
        let text = r#"{
            "schema": "terrascale.sync.v2/corpus-manifest@1",
            "protocol": "terrascale.sync.v2",
            "protocol_version": 2,
            "vectors": [
                {
                    "category": "fresh-session",
                    "file": "fresh-session.json",
                    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                    "bytes": 10
                }
            ]
        }"#;
        let manifest = CorpusManifest::from_json_str(text).expect("parses");
        assert_eq!(manifest.vectors().len(), 1);
        assert_eq!(manifest.vectors()[0].category(), "fresh-session");
        let reparsed =
            CorpusManifest::from_json_str(&manifest.to_json_string()).expect("round trips");
        assert_eq!(manifest, reparsed);
    }

    #[test]
    fn malformed_entries_fail_closed() {
        let base = r#"{
            "schema": "terrascale.sync.v2/corpus-manifest@1",
            "protocol": "terrascale.sync.v2",
            "protocol_version": 2,
            "vectors": ["#;
        for tail in [
            r#"{ "category": "c", "file": "../escape.json", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "bytes": 1 }],"#,
            r#"{ "category": "c", "file": "c.json", "sha256": "short", "bytes": 1 }],"#,
            r#"{ "category": "c", "file": "c.json", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }],"#,
        ] {
            let text = format!("{base}{tail}}}");
            assert!(
                CorpusManifest::from_json_str(&text).is_err(),
                "malformed entry must fail closed: {tail}"
            );
        }
    }
}
