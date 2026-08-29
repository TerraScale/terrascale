//! The corpus integrity manifest: the machine-readable digest list over
//! the golden-vector corpora a harness runs. An SDK's CI vendors the
//! corpus directories plus this manifest and verifies the digests
//! before running any vector — integrity without a Rust toolchain
//! (PD-2026-08-26-12 D1: the corpus is the compatibility contract;
//! SE-05 makes it tamper-evident).
//!
//! Schema `terrascale.sync.v2/corpus-manifest@2` extends the @1 form
//! with one field: each entry names the corpus (directory identity)
//! its file belongs to, so one manifest gates a multi-corpus kit run —
//! the SE-04 base corpus plus the ADR 0024 extension corpus this crate
//! owns. The @1 form still parses (its entries belong to the base
//! corpus); it is never emitted.

use std::collections::BTreeMap;
use std::fmt;
use std::fs;

use crate::is_corpus_file;

use serde_json::Value;
use serde_json::json;

use crate::BASE_CORPUS_NAME;
use crate::MANIFEST_SCHEMA;
use crate::MANIFEST_SCHEMA_LEGACY;
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
    corpus: String,
    category: String,
    file: String,
    sha256: String,
    bytes: u64,
}

impl ManifestEntry {
    /// The corpus (directory identity) the file belongs to.
    #[must_use]
    pub fn corpus(&self) -> &str {
        &self.corpus
    }

    /// The corpus category the file covers.
    #[must_use]
    pub fn category(&self) -> &str {
        &self.category
    }

    /// The file name within its corpus directory (never a path).
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

/// The corpus manifest: every corpus file of every gated corpus,
/// digested.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CorpusManifest {
    schema: String,
    protocol: String,
    protocol_version: u64,
    vectors: Vec<ManifestEntry>,
}

impl CorpusManifest {
    /// Builds the manifest of corpus directories: every `*.json` file
    /// of every directory, digested and sized, one entry per file,
    /// sorted by corpus then file name. Each entry carries its
    /// directory's name as the corpus identity.
    ///
    /// # Errors
    ///
    /// Returns [`ManifestError`] when a directory cannot be read, a
    /// file is not valid JSON carrying a string `category`, or two
    /// directories share one name (a corpus identity would be
    /// ambiguous).
    pub fn build(corpus_dirs: &[std::path::PathBuf]) -> Result<Self, ManifestError> {
        let mut names = std::collections::BTreeSet::new();
        for dir in corpus_dirs {
            let name = dir
                .file_name()
                .map_or_else(|| ".".to_string(), |n| n.to_string_lossy().into_owned());
            if !names.insert(name) {
                return Err(err(format!(
                    "two corpus directories share the identity {}",
                    dir.display()
                )));
            }
        }
        let mut vectors = Vec::new();
        for dir in corpus_dirs {
            let corpus = dir
                .file_name()
                .map_or_else(|| ".".to_string(), |n| n.to_string_lossy().into_owned());
            let entries = fs::read_dir(dir).map_err(|e| {
                err(format!("the corpus directory {} is unreadable: {e}", dir.display()))
            })?;
            let mut files: Vec<String> = entries
                .flatten()
                .map(|entry| entry.file_name().to_string_lossy().into_owned())
                .filter(|name| is_corpus_file(name))
                .collect();
            files.sort();
            for file in files {
                let bytes = fs::read(dir.join(&file))
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
                    corpus: corpus.clone(),
                    category: category.to_string(),
                    file,
                    sha256: to_hex(&hasher.finish()),
                    bytes: bytes.len() as u64,
                });
            }
        }
        vectors.sort_by(|a, b| (&a.corpus, &a.file).cmp(&(&b.corpus, &b.file)));
        Ok(Self {
            schema: MANIFEST_SCHEMA.to_string(),
            protocol: PROTOCOL.to_string(),
            protocol_version: PROTOCOL_VERSION,
            vectors,
        })
    }

    /// Parses a manifest, failing closed on unknown schema identities,
    /// foreign protocols, and malformed entries. Both schema @2 (the
    /// emitted form, entries may name their corpus) and the legacy @1
    /// form (entries belong to the base corpus) parse.
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
        if schema != MANIFEST_SCHEMA && schema != MANIFEST_SCHEMA_LEGACY {
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
            let corpus = match entry.get("corpus").and_then(Value::as_str) {
                Some(name) => {
                    if name.is_empty() || name.contains('/') || name.contains('\\') {
                        return Err(err(format!(
                            "the manifest entry for {file} carries a corpus path, not a corpus identity"
                        )));
                    }
                    name.to_string()
                }
                None => BASE_CORPUS_NAME.to_string(),
            };
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
                corpus,
                category: category.to_string(),
                file: file.to_string(),
                sha256: sha256.to_string(),
                bytes,
            });
        }
        let mut seen =
            vectors.iter().map(|e| (e.corpus.clone(), e.category.clone())).collect::<Vec<_>>();
        seen.sort();
        seen.dedup();
        if seen.len() != vectors.len() {
            return Err(err("the manifest repeats a category within one corpus"));
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

    /// The manifest's canonical JSON (pretty, deterministic, schema
    /// @2 — every entry names its corpus).
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
            "schema": MANIFEST_SCHEMA,
            "protocol": self.protocol,
            "protocol_version": self.protocol_version,
            "vectors": self
                .vectors
                .iter()
                .map(|entry| {
                    json!({
                        "corpus": entry.corpus,
                        "category": entry.category,
                        "file": entry.file,
                        "sha256": entry.sha256,
                        "bytes": entry.bytes,
                    })
                })
                .collect::<Vec<_>>(),
        })
    }

    /// Verifies corpus directories against this manifest: every
    /// manifest entry's file must exist in its named corpus with the
    /// recorded digest and size, and every corpus file must have an
    /// entry. Returns the problems; an empty list is a verified corpus.
    ///
    /// Problems carry file names and reasons, never corpus bytes or
    /// host paths.
    ///
    /// # Errors
    ///
    /// Returns [`ManifestError`] only when a corpus directory itself
    /// cannot be read; file-level disagreement is reported through the
    /// returned problem list.
    pub fn verify(&self, corpus_dirs: &[std::path::PathBuf]) -> Result<Vec<String>, ManifestError> {
        let mut dirs: BTreeMap<String, &std::path::PathBuf> = BTreeMap::new();
        for dir in corpus_dirs {
            let name = dir
                .file_name()
                .map_or_else(|| ".".to_string(), |n| n.to_string_lossy().into_owned());
            if dirs.insert(name, dir).is_some() {
                return Err(err(format!(
                    "two corpus directories share the identity {}",
                    dir.display()
                )));
            }
        }
        let mut problems = Vec::new();
        for entry in &self.vectors {
            let Some(dir) = dirs.get(&entry.corpus) else {
                problems.push(format!(
                    "{}: its manifest entry names the corpus {} the run does not hold",
                    entry.file, entry.corpus
                ));
                continue;
            };
            match fs::read(dir.join(&entry.file)) {
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
        for (name, dir) in &dirs {
            let listed = fs::read_dir(dir).map_err(|e| {
                err(format!("the corpus directory {} is unreadable: {e}", dir.display()))
            })?;
            let present: Vec<String> = listed
                .flatten()
                .map(|e| e.file_name().to_string_lossy().into_owned())
                .filter(|file| is_corpus_file(file))
                .collect();
            for file in present {
                if !self.vectors.iter().any(|entry| entry.file == file) {
                    problems
                        .push(format!("{file}: corpus file of {name} without a manifest entry"));
                }
            }
        }
        Ok(problems)
    }
}

#[cfg(test)]
mod tests {
    use super::CorpusManifest;
    use crate::BASE_CORPUS_NAME;

    #[test]
    fn a_well_formed_manifest_parses_and_round_trips() {
        let text = r#"{
            "schema": "terrascale.sync.v2/corpus-manifest@2",
            "protocol": "terrascale.sync.v2",
            "protocol_version": 2,
            "vectors": [
                {
                    "corpus": "terrascale.sync.v2",
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
        assert_eq!(manifest.vectors()[0].corpus(), BASE_CORPUS_NAME);
        let reparsed =
            CorpusManifest::from_json_str(&manifest.to_json_string()).expect("round trips");
        assert_eq!(manifest, reparsed);
    }

    #[test]
    fn the_legacy_schema_parses_into_base_corpus_entries() {
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
        let manifest = CorpusManifest::from_json_str(text).expect("the legacy schema parses");
        assert_eq!(manifest.vectors()[0].corpus(), BASE_CORPUS_NAME);
        // The legacy form parses but is never re-emitted.
        let emitted = manifest.to_json_string();
        assert!(emitted.contains("terrascale.sync.v2/corpus-manifest@2"));
        assert!(!emitted.contains("terrascale.sync.v2/corpus-manifest@1"));
    }

    #[test]
    fn malformed_entries_fail_closed() {
        let base = r#"{
            "schema": "terrascale.sync.v2/corpus-manifest@2",
            "protocol": "terrascale.sync.v2",
            "protocol_version": 2,
            "vectors": ["#;
        for tail in [
            r#"{ "category": "c", "file": "../escape.json", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "bytes": 1 }],"#,
            r#"{ "category": "c", "file": "c.json", "sha256": "short", "bytes": 1 }],"#,
            r#"{ "category": "c", "file": "c.json", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }],"#,
            r#"{ "category": "c", "file": "c.json", "corpus": "../escape", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "bytes": 1 }],"#,
            r#"{ "category": "c", "file": "a.json", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "bytes": 1 },
                { "category": "c", "file": "b.json", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "bytes": 1 }],"#,
        ] {
            let text = format!("{base}{tail}}}");
            assert!(
                CorpusManifest::from_json_str(&text).is_err(),
                "malformed entry must fail closed: {tail}"
            );
        }
    }
}
