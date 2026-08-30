//! The pinned identity of the TBEP FlatBuffers generation inputs.
//!
//! ADR 0025 makes the checked-in root schema (`schema/tbep.fbs`) and the
//! shared frame registry (`schema/tbep-registry.json`) the only authority
//! for the private TBEP FlatBuffers surface, and makes every generated
//! artifact a pure function of those inputs plus the pinned compiler.
//! "Pinned" needs a *name* that survives without the compiler: this module
//! is that name.
//!
//! [`PINNED_SCHEMA_DIGEST`] is a canonical BLAKE3 digest over both inputs
//! and [`PINNED_BINDING_DIGEST`] is the same digest over the committed
//! generated Rust binding. The two constants are verified against the
//! working tree by `tests/fbs_drift.rs`, which needs neither `flatc` nor
//! the network: a hand-edited binding, a schema edit, or a registry edit
//! each fail a named test before any compiler is resolved. The `flatc`
//! equality gate (`generate.sh --check`) then proves the stronger fact
//! that the committed binding *equals regeneration* from those inputs.
//!
//! Vector files under `vectors/tbep/fbs/` record
//! [`PINNED_SCHEMA_DIGEST`] in their header, so the golden bytes and the
//! bindings are bound to one exact schema revision: a corpus recorded
//! against a different revision refuses to replay.

use std::path::Path;

/// Domain-separation label for the canonical schema digest. Every
/// TerraBase digest that is not a payload digest carries one, so a digest
/// from one use can never be replayed as another.
pub const DIGEST_DOMAIN: &str = "terrabase.tbep/schema-identity@1";

/// The repository-relative path of the authoritative FlatBuffers schema.
pub const SCHEMA_PATH: &str = "schema/tbep.fbs";

/// The repository-relative path of the shared frame registry.
pub const REGISTRY_PATH: &str = "schema/tbep-registry.json";

/// The repository-relative path of the committed generated Rust binding.
pub const BINDING_PATH: &str = "src/generated/fbs/tbep_generated.rs";

/// The pinned `flatc` release the committed binding was generated with
/// (`generate.sh` resolves and version-checks it).
pub const PINNED_FLATC_VERSION: &str = super::PINNED_FLATC_VERSION;

/// The SHA-256 of the pinned official `flatc` Linux x86_64 release
/// archive, duplicated from `generate.sh` so a Rust test can hold the two
/// pins to the same value (see
/// [`tests/fbs_drift.rs`](../tests/fbs_drift.rs)).
pub const PINNED_FLATC_ARCHIVE_SHA256: &str =
    "9f87066dc5dfa7fe02090b55bab5f3e55df03e32c9b0cdf229004ade7d091039";

/// The URL the pinned `flatc` archive is fetched from when neither
/// `TBEP_FLATC` nor a `flatc` on `PATH` is available.
pub const PINNED_FLATC_ARCHIVE_URL: &str =
    "https://github.com/google/flatbuffers/releases/download/v25.12.19/Linux.flatc.binary.g%2B%2B-13.zip";

/// BLAKE3 of `schema/tbep.fbs` alone.
pub const PINNED_FBS_DIGEST: &str =
    "1f005f9ccaeffc191a71f6f6d29fe648933cecde548211f2af4d6a31685edc35";

/// BLAKE3 of `schema/tbep-registry.json` alone.
pub const PINNED_REGISTRY_DIGEST: &str =
    "230dc0810a7787a8874beec5b29713874b8e8efa48e9894657146fae4428ba6d";

/// The canonical BLAKE3 digest of the two pinned generation inputs. This
/// is the revision identity every FlatBuffers vector file records.
pub const PINNED_SCHEMA_DIGEST: &str =
    "56771ec122d376083290c551057c0b1cc698d4649049177c176854e7438ac13e";

/// The BLAKE3 digest of the committed generated Rust binding. Any
/// hand-edit of that file changes it and fails the drift gate.
pub const PINNED_BINDING_DIGEST: &str =
    "7f6a07d18ef1206629450c0e0a464e86dae12fe93b1d19e8fc83a48b039c0024";

/// The two pinned generation inputs, loaded from the working tree.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PinnedInputs {
    /// The raw bytes of `schema/tbep.fbs`.
    pub fbs: Vec<u8>,
    /// The raw bytes of `schema/tbep-registry.json`.
    pub registry: Vec<u8>,
}

impl PinnedInputs {
    /// Reads both pinned inputs relative to the `terrabase-tbep` crate
    /// root (the parent of `tools/tbep-fbs`).
    pub fn load(crate_root: &Path) -> std::io::Result<Self> {
        Ok(Self {
            fbs: std::fs::read(crate_root.join(SCHEMA_PATH))?,
            registry: std::fs::read(crate_root.join(REGISTRY_PATH))?,
        })
    }

    /// The canonical digest of both inputs together.
    #[must_use]
    pub fn digest(&self) -> String {
        canonical_schema_digest(&self.fbs, &self.registry)
    }

    /// BLAKE3 of the schema input alone.
    #[must_use]
    pub fn fbs_digest(&self) -> String {
        to_hex(blake3::hash(&self.fbs).as_bytes())
    }

    /// BLAKE3 of the registry input alone.
    #[must_use]
    pub fn registry_digest(&self) -> String {
        to_hex(blake3::hash(&self.registry).as_bytes())
    }
}

/// Computes the canonical digest of the two pinned generation inputs.
///
/// The framing is domain-separated and length-prefixed so no pair of
/// inputs can be confused with any other pair or with a single
/// concatenated input:
///
/// ```text
/// BLAKE3(DIGEST_DOMAIN || 0x00
///        || SCHEMA_PATH   || 0x00 || u64-le(len(fbs))      || fbs
///        || REGISTRY_PATH || 0x00 || u64-le(len(registry)) || registry)
/// ```
#[must_use]
pub fn canonical_schema_digest(fbs: &[u8], registry: &[u8]) -> String {
    let mut hasher = blake3::Hasher::new();
    hasher.update(DIGEST_DOMAIN.as_bytes());
    hasher.update(&[0]);
    absorb(&mut hasher, SCHEMA_PATH, fbs);
    absorb(&mut hasher, REGISTRY_PATH, registry);
    to_hex(hasher.finalize().as_bytes())
}

/// BLAKE3 of one committed file, hex-encoded; used for the generated
/// binding, whose bytes are pinned exactly as generated.
#[must_use]
pub fn file_digest(bytes: &[u8]) -> String {
    to_hex(blake3::hash(bytes).as_bytes())
}

fn absorb(hasher: &mut blake3::Hasher, name: &str, bytes: &[u8]) {
    hasher.update(name.as_bytes());
    hasher.update(&[0]);
    hasher.update(&(bytes.len() as u64).to_le_bytes());
    hasher.update(bytes);
}

fn to_hex(digest: &[u8; blake3::OUT_LEN]) -> String {
    let mut out = String::with_capacity(digest.len() * 2);
    for byte in digest {
        use std::fmt::Write as _;
        let _ = write!(out, "{byte:02x}");
    }
    out
}
