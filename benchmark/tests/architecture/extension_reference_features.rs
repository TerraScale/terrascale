//! Gate 3 — extension-reference-feature architecture gate (GOAL.md P8:
//! "Run ... extension-reference-feature ... architecture gates").
//!
//! Authority: GOAL.md 'Stable extension seams' — "TerraBase owns typed,
//! versioned registries for every feature family expected to grow", each
//! with "one owner, stable numeric or string identities, explicit major
//! versions, ... bounded decode and resource rules, unknown-feature
//! rejection, and golden vectors", and "Registration order cannot change
//! persisted bytes".
//!
//! The tables below are the reference-implementation manifest: for every
//! registry entry that claims to exist, the gate verifies a reference
//! implementation exists in the named source (the stable identity, the
//! implementing type, its codec dispatch, and its public codec entry
//! point). The manifest is compared with the live sources in both
//! directions — a registry entry without a reference implementation fails,
//! and a live entry absent from the manifest fails — so every registry
//! change arrives with this file and a review note citing the accepting
//! record. The manifest starts with the registries that exist today (the
//! native register datatype kinds, the canonical value-type tags, and the
//! engine document families); each growing family named by GOAL.md joins
//! here as it lands.

use std::collections::BTreeSet;

use crate::fsx;

/// The native register datatype registry of `terrabase-crdt`:
/// (kind constant, stable wire tag, register type, `RegisterCodec` encode
/// entry, `RegisterCodec` decode entry).
const REGISTER_DATATYPES: &[(&str, u8, &str, &str, &str)] = &[
    ("LWW_KIND", 1, "LwwRegister", "encode_lww", "decode_lww"),
    ("MV_KIND", 2, "MvRegister", "encode_mv", "decode_mv"),
];

/// The canonical value-type registry of `terrabase-crdt` (`CanonicalValue`
/// implementations): (Rust type, stable `TYPE_TAG`).
const CANONICAL_VALUE_TYPES: &[(&str, u8)] = &[("bool", 1), ("u64", 2), ("i64", 3), ("String", 4)];

/// The engine document-family registry (`FamilyKind`): (enum variant,
/// reserved record key). GOAL.md forbids deriving persisted identities from
/// Rust enum order, so every variant maps to an explicit string key.
const FAMILY_RECORD_KEYS: &[(&str, &str)] = &[
    ("Attachments", "attachments"),
    ("Counters", "counters"),
    ("TimeSeries", "time-series"),
    ("Revisions", "revisions"),
    ("CompareExchange", "compare-exchange"),
    ("Identities", "identities"),
    ("ExpirationRefresh", "lifecycle"),
    ("Archival", "archival"),
    ("SchemaValidation", "validation"),
    ("ConflictTombstones", "tombstones"),
];

/// The explicit codec major versions: (manifest path, file-declared const
/// form). Persisted bytes pin these identities; a bump requires a versioned
/// migration and a review note here.
const CODEC_MAJOR_VERSIONS: &[(&str, &str)] = &[
    ("crates/terrabase-causal/src/lib.rs", "pub const CODEC_VERSION: u8 = 1;"),
    ("crates/terrabase-crdt/src/register.rs", "const CODEC_VERSION: u8 = 1;"),
];

#[test]
fn register_datatypes_have_reference_implementations() {
    let register =
        fsx::strip_line_comments(&fsx::read_repo_file("crates/terrabase-crdt/src/register.rs"));
    let codec =
        fsx::strip_line_comments(&fsx::read_repo_file("crates/terrabase-crdt/src/codec.rs"));

    for (kind, tag, register_type, encode_entry, decode_entry) in REGISTER_DATATYPES {
        assert!(
            register.contains(&format!("const {kind}: u8 = {tag};")),
            "registered datatype kind {kind} lost its stable tag {tag}",
        );
        assert!(
            register.contains(&format!("pub struct {register_type}<T: CanonicalValue>")),
            "registered kind {kind} has no reference register type {register_type}",
        );
        assert!(
            register.contains(&format!("CanonicalEncode for {register_type}<T>")),
            "registered kind {kind} has no canonical encode implementation on \
             {register_type}",
        );
        assert!(
            register.contains(&format!("encode_state(output, {kind},")),
            "registered kind {kind} is not dispatched by its encode path",
        );
        assert!(
            register.contains(&format!("decode_state(&mut reader, limits, {kind})")),
            "registered kind {kind} is not dispatched by its decode path",
        );
        assert!(
            register.contains("reader.byte()? != expected_kind"),
            "the register decoder no longer rejects unknown datatype kinds \
             (GOAL 'Stable extension seams': unknown-feature rejection)",
        );
        for entry in [encode_entry, decode_entry] {
            assert!(
                codec.contains(&format!("pub fn {entry}")),
                "RegisterCodec has no public {entry} entry point for {register_type}",
            );
        }
    }
}

#[test]
fn canonical_value_registry_matches_the_manifest() {
    let codec =
        fsx::strip_line_comments(&fsx::read_repo_file("crates/terrabase-crdt/src/codec.rs"));
    let mut live: BTreeSet<(String, u8)> = BTreeSet::new();
    let mut lines = codec.lines().enumerate().peekable();
    while let Some((index, line)) = lines.next() {
        let Some(implementing) = line.trim().strip_prefix("impl CanonicalValue for ") else {
            continue;
        };
        let type_name = implementing.trim_end_matches('{').trim().to_string();
        let mut tag = None;
        let mut encodes = false;
        let mut decodes = false;
        while let Some(&(_, body)) = lines.peek() {
            let trimmed = body.trim();
            if trimmed.starts_with("impl ") || trimmed.starts_with("#[cfg") {
                break;
            }
            if let Some(rest) = trimmed.strip_prefix("const TYPE_TAG: u8 = ") {
                tag = rest.trim_end_matches(';').parse::<u8>().ok().or(tag);
            }
            if trimmed.starts_with("fn encode_value") {
                encodes = true;
            }
            if trimmed.starts_with("fn decode_value") {
                decodes = true;
            }
            if body.starts_with('}') {
                break;
            }
            lines.next();
        }
        let Some(tag) = tag else {
            panic!(
                "canonical value type {type_name} (codec.rs line {}) registers no TYPE_TAG",
                index + 1
            );
        };
        assert!(
            encodes && decodes,
            "canonical value type {type_name} has no reference codec (encode_value + \
             decode_value) for its tag {tag}",
        );
        live.insert((type_name, tag));
    }
    let manifest: BTreeSet<(String, u8)> = CANONICAL_VALUE_TYPES
        .iter()
        .map(|(type_name, tag)| (type_name.to_string(), *tag))
        .collect();
    assert_eq!(
        live, manifest,
        "the CanonicalValue registry drifted from the reference-implementation manifest; \
         every registry change requires a review note in this gate",
    );
    let tags: BTreeSet<u8> = manifest.iter().map(|(_, tag)| *tag).collect();
    assert_eq!(
        tags.len(),
        manifest.len(),
        "canonical value TYPE_TAGs must be unique stable identities",
    );
}

#[test]
fn engine_family_registry_matches_the_manifest_and_keeps_explicit_keys() {
    let families = fsx::strip_line_comments(&fsx::read_repo_file("src/families/mod.rs"));
    let state = fsx::strip_line_comments(&fsx::read_repo_file("src/families/state.rs"));

    let mut variants: Vec<String> = Vec::new();
    let mut inside = false;
    for line in families.lines() {
        let trimmed = line.trim();
        if trimmed == "pub enum FamilyKind {" {
            inside = true;
            continue;
        }
        if inside {
            if trimmed.starts_with('}') {
                break;
            }
            if let Some(name) = trimmed.strip_suffix(',') {
                variants.push(name.trim().to_string());
            }
        }
    }
    let manifest: Vec<String> =
        FAMILY_RECORD_KEYS.iter().map(|(variant, _)| (*variant).to_string()).collect();
    assert_eq!(
        variants, manifest,
        "the FamilyKind registry drifted from the reference-implementation manifest; every \
         family addition or removal requires a review note in this gate",
    );

    let mut keys = BTreeSet::new();
    for (variant, key) in FAMILY_RECORD_KEYS {
        assert!(
            state.contains(&format!("FamilyKind::{variant} => \"{key}\"")),
            "registered family {variant} has no reserved record key implementation",
        );
        assert!(keys.insert(*key), "family record keys must be unique, `{key}` is not");
    }
    assert_eq!(
        keys.len(),
        FAMILY_RECORD_KEYS.len(),
        "every registered family must own exactly one reserved record key",
    );
}

#[test]
fn codec_major_versions_stay_explicit_and_frozen() {
    for (path, declaration) in CODEC_MAJOR_VERSIONS {
        let source = fsx::strip_line_comments(&fsx::read_repo_file(path));
        assert!(
            source.contains(declaration),
            "{path} no longer declares `{declaration}`; codec major versions are stable \
             registry identities and a bump requires a versioned migration plus a review \
             note in this gate",
        );
    }
}
