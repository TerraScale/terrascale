//! Typed backup destination configuration (PB-6012, the local kind).
//!
//! A destination is a typed fact set, not a provider escape hatch: the
//! engine knows the local-filesystem kind now, and each future provider
//! kind (object stores, and so on) arrives as a new variant of
//! [`DestinationKind`] with its own validation contract — never as a
//! generic URL or credential blob. The configuration carries no secrets:
//! provider credentials, when a remote kind exists, will be supplied by the
//! caller per operation exactly like encryption keys, never stored by the
//! engine (PD-2026-08-26-11 D2's secret-handling rule).
//!
//! Validation is split in two on purpose. [`DestinationConfig::validate`]
//! is side-effect-free: it checks the path shape and what already exists,
//! so a status read can validate without touching the filesystem. The
//! writability proof is [`DestinationConfig::probe`], which creates the
//! root and writes one transient, uniquely named probe file before
//! removing it — a scheduled run always probes before it seals, so an
//! unwritable destination fails typed before any archive byte is staged.

use std::path::{Component, Path, PathBuf};

/// The kind of one backup destination. The local filesystem is the only
/// kind BK-04 implements; every later provider kind is a new variant with
/// its own validation, upload, listing, and deletion contract
/// (PB-6012's per-provider qualification rule).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DestinationKind {
    /// A directory tree on the local filesystem, holding `rp-<identity>`
    /// recovery-point directories and their sealed artifact bundles.
    LocalFileSystem,
}

/// The typed reason one destination configuration is invalid.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DestinationInvalid {
    /// The configured root is the empty path.
    EmptyPath,
    /// The configured root is relative; a destination must be absolute so
    /// behavior never depends on the caller's working directory.
    RelativePath,
    /// The configured root carries a `..` component and could escape its
    /// intended tree.
    ParentTraversal,
    /// The configured root is the filesystem root, which a backup
    /// destination must never be.
    RootPath,
    /// Something that exists at the configured root is not a directory.
    NotADirectory,
}

/// The typed failure of one destination operation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DestinationError {
    /// The configuration failed side-effect-free validation.
    Invalid(DestinationInvalid),
    /// The destination root could not be created, written, or cleaned
    /// during a probe or run.
    Unavailable,
}

/// The process-wide counter behind unique probe file names.
static PROBE_COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

/// A validated-by-construction backup destination configuration.
///
/// The configuration is a value: cloning it is free and it stays immutable.
/// Validation and probing are explicit typed operations so a caller can
/// distinguish "misconfigured" from "unwritable right now".
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DestinationConfig {
    kind: DestinationKind,
    root: PathBuf,
}

impl DestinationConfig {
    /// Configures a local-filesystem destination rooted at `root`.
    #[must_use]
    pub fn local(root: impl Into<PathBuf>) -> Self {
        Self { kind: DestinationKind::LocalFileSystem, root: root.into() }
    }

    /// Returns the destination's kind.
    #[must_use]
    pub fn kind(&self) -> DestinationKind {
        self.kind
    }

    /// Returns the destination root path. This is configuration, not
    /// status: schedule status never carries it (PB-6011's no-secrets
    /// surface), but the driving caller needs it to build other reads.
    #[must_use]
    pub fn root(&self) -> &Path {
        &self.root
    }

    /// Validates the configuration without side effects.
    ///
    /// A local root must be absolute, must not be the filesystem root, and
    /// must not contain a `..` component; if something already exists at
    /// the root it must be a directory.
    ///
    /// # Errors
    ///
    /// Returns the typed validation failure for the first violated rule.
    pub fn validate(&self) -> Result<(), DestinationError> {
        if self.root.as_os_str().is_empty() {
            return Err(DestinationError::Invalid(DestinationInvalid::EmptyPath));
        }
        if !self.root.is_absolute() {
            return Err(DestinationError::Invalid(DestinationInvalid::RelativePath));
        }
        if self.root.components().any(|component| component == Component::ParentDir) {
            return Err(DestinationError::Invalid(DestinationInvalid::ParentTraversal));
        }
        if self.root == Path::new("/") {
            return Err(DestinationError::Invalid(DestinationInvalid::RootPath));
        }
        if self.root.exists() && !self.root.is_dir() {
            return Err(DestinationError::Invalid(DestinationInvalid::NotADirectory));
        }
        Ok(())
    }

    /// Proves the destination is usable: creates the root (and missing
    /// parents) and writes plus removes one transient probe file.
    ///
    /// The probe file's name is unique per process and call, so two
    /// schedules probing the same destination concurrently never collide,
    /// and the probe leaves no residue on success.
    ///
    /// # Errors
    ///
    /// Returns [`DestinationError::Invalid`] when validation fails and
    /// [`DestinationError::Unavailable`] when the root cannot be created,
    /// written, or cleaned.
    pub fn probe(&self) -> Result<(), DestinationError> {
        self.validate()?;
        std::fs::create_dir_all(&self.root).map_err(|_| DestinationError::Unavailable)?;
        let index = PROBE_COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let probe = self.root.join(format!(".tbv2-probe-{}-{index}", std::process::id()));
        std::fs::write(&probe, b"probe")
            .and_then(|()| std::fs::remove_file(&probe))
            .map_err(|_| DestinationError::Unavailable)
    }
}
