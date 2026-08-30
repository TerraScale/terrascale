//! The immutable cluster identity (P5): the mode record a cluster is
//! born with, never changed afterwards.
//!
//! The frozen backup-cluster contract §1: a cluster records
//! `mode: active | backup` in its immutable identity at creation; the
//! identity document is content-hashed, quorum-pinned, and verified at
//! every member start and join. `backup` is terminal — no promote,
//! convert, re-role, or mode-change operation exists anywhere, and a
//! mode attempt fails with the typed [`IdentityError::ModeImmutable`] error (the
//! wire's `backup_cluster_immutable`), distinct from the write refusal
//! [`IdentityError::ClusterReadOnly`] (`backup_cluster_read_only`) every client
//! mutation on a backup member answers with, its replication position
//! attached.

use std::fmt;
use std::path::Path;

/// A cluster's mode, frozen at creation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ClusterMode {
    /// The cluster serves reads and writes.
    Active,
    /// The cluster receives replication and refuses every client
    /// mutation — permanently.
    Backup,
}

impl fmt::Display for ClusterMode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Active => write!(f, "active"),
            Self::Backup => write!(f, "backup"),
        }
    }
}

impl ClusterMode {
    /// Parses the canonical mode text.
    ///
    /// # Errors
    ///
    /// Returns [`IdentityError::UnknownMode`] for any other text.
    pub fn parse(text: &str) -> Result<Self, IdentityError> {
        match text {
            "active" => Ok(Self::Active),
            "backup" => Ok(Self::Backup),
            _ => Err(IdentityError::UnknownMode),
        }
    }
}

/// The immutable identity record: the mode, the creation record, and
/// the content hash pinning both.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ClusterIdentity {
    cluster: String,
    mode: ClusterMode,
    /// The creation record: free-bounded operator text (max 256 bytes),
    /// hashed into the identity.
    creation_record: String,
    /// The content hash over the canonical record — the pin every
    /// member start and join verifies.
    content_hash: u64,
}

impl ClusterIdentity {
    /// Creates a cluster's identity, freezing its mode at this moment.
    ///
    /// # Errors
    ///
    /// Returns [`IdentityError::InvalidClusterName`] for an empty or
    /// over-long cluster name, [`IdentityError::InvalidCreationRecord`]
    /// for a record over 256 bytes, and never otherwise — creation is
    /// the one moment the mode is chosen.
    pub fn create(
        cluster: &str,
        mode: ClusterMode,
        creation_record: &str,
    ) -> Result<Self, IdentityError> {
        if cluster.trim().is_empty() || cluster.len() > 128 {
            return Err(IdentityError::InvalidClusterName);
        }
        if creation_record.len() > 256 {
            return Err(IdentityError::InvalidCreationRecord);
        }
        let content_hash = content_hash(cluster, mode, creation_record);
        Ok(Self {
            cluster: cluster.to_owned(),
            mode,
            creation_record: creation_record.to_owned(),
            content_hash,
        })
    }

    /// The cluster's identity slug.
    #[must_use]
    pub fn cluster(&self) -> &str {
        &self.cluster
    }

    /// The frozen mode.
    #[must_use]
    pub const fn mode(&self) -> ClusterMode {
        self.mode
    }

    /// The creation record.
    #[must_use]
    pub fn creation_record(&self) -> &str {
        &self.creation_record
    }

    /// The content hash pinning the record.
    #[must_use]
    pub const fn content_hash(&self) -> u64 {
        self.content_hash
    }

    /// Refuses a mode change — the typed error every attempt answers
    /// with, whatever the current mode is: the identity is immutable,
    /// not merely the backup's.
    ///
    /// # Errors
    ///
    /// Always returns [`IdentityError::ModeImmutable`].
    pub fn change_mode(&self, _to: ClusterMode) -> Result<Self, IdentityError> {
        Err(IdentityError::ModeImmutable)
    }

    /// Writes the identity durably to `dir`: the record file, then the
    /// hash beside it, so a torn write can never verify.
    ///
    /// # Errors
    ///
    /// Returns [`IdentityError::Unavailable`] when either write fails.
    pub fn pin(&self, dir: &Path) -> Result<(), IdentityError> {
        std::fs::create_dir_all(dir).map_err(|_| IdentityError::Unavailable)?;
        let record = dir.join("cluster-identity");
        let staged = dir.join("cluster-identity.staged");
        std::fs::write(&staged, self.canonical()).map_err(|_| IdentityError::Unavailable)?;
        std::fs::rename(&staged, &record).map_err(|_| IdentityError::Unavailable)?;
        Ok(())
    }

    /// Reads and verifies the pinned identity of `dir`.
    ///
    /// # Errors
    ///
    /// Returns [`IdentityError::Absent`] when no identity is pinned,
    /// [`IdentityError::Tampered`] when the record does not verify
    /// against its own recorded hash, and [`IdentityError::Malformed`]
    /// for a structurally invalid record.
    pub fn open(dir: &Path) -> Result<Self, IdentityError> {
        let record = std::fs::read_to_string(dir.join("cluster-identity"))
            .map_err(|_| IdentityError::Absent)?;
        let mut lines = record.lines();
        let Some(cluster) = lines
            .next()
            .map(|line| line.strip_prefix("cluster: ").unwrap_or(line))
            .map(str::to_owned)
        else {
            return Err(IdentityError::Malformed);
        };
        let Some(mode_text) =
            lines.next().map(|line| line.strip_prefix("mode: ").unwrap_or(line)).map(str::to_owned)
        else {
            return Err(IdentityError::Malformed);
        };
        let Some(hash_text) =
            lines.next().map(|line| line.strip_prefix("hash: ").unwrap_or(line)).map(str::to_owned)
        else {
            return Err(IdentityError::Malformed);
        };
        let creation_record = lines
            .map(|line| line.strip_prefix("record: ").unwrap_or(line).to_owned())
            .collect::<Vec<String>>()
            .join("\n");
        let mode = ClusterMode::parse(&mode_text)?;
        let recorded =
            u64::from_str_radix(hash_text.trim(), 16).map_err(|_| IdentityError::Malformed)?;
        if content_hash(&cluster, mode, &creation_record) != recorded {
            return Err(IdentityError::Tampered);
        }
        Ok(Self { cluster, mode, creation_record, content_hash: recorded })
    }

    /// The canonical pinned record text.
    #[must_use]
    pub fn canonical(&self) -> String {
        format!(
            "cluster: {}\nmode: {}\nhash: {:016x}\nrecord: {}",
            self.cluster, self.mode, self.content_hash, self.creation_record
        )
    }
}

/// The identity's typed failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IdentityError {
    /// The cluster name was empty or over-long.
    InvalidClusterName,
    /// The creation record exceeded its bound.
    InvalidCreationRecord,
    /// The mode text is not a mode.
    UnknownMode,
    /// No identity is pinned at the location.
    Absent,
    /// The pinned record does not verify against its own hash.
    Tampered,
    /// The pinned record is structurally invalid.
    Malformed,
    /// The durable write could not complete.
    Unavailable,
    /// A mode change was attempted: the identity is immutable — the
    /// wire's `backup_cluster_immutable`.
    ModeImmutable,
    /// A client mutation on a backup member: permanent, typed refusal
    /// — the wire's `backup_cluster_read_only`, position attached.
    ClusterReadOnly,
}

/// The FNV-1a-64 content hash over the canonical identity fields —
/// the same digest style the engine's object keys use.
fn content_hash(cluster: &str, mode: ClusterMode, record: &str) -> u64 {
    fn fnv1a(bytes: &[u8]) -> u64 {
        let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
        for byte in bytes {
            hash ^= u64::from(*byte);
            hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
        }
        hash
    }
    let mode_byte = match mode {
        ClusterMode::Active => b"a",
        ClusterMode::Backup => b"b",
    };
    let mut acc = fnv1a(cluster.as_bytes());
    acc ^= fnv1a(mode_byte);
    acc = acc.wrapping_mul(0x0000_0100_0000_01b3);
    acc ^ fnv1a(record.as_bytes())
}

#[cfg(test)]
mod tests {
    use super::{ClusterIdentity, ClusterMode, IdentityError};

    fn dir(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "terrabase-cluster-identity-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn creation_freezes_the_mode_and_pins_verifiably() {
        let identity = ClusterIdentity::create("cell-a", ClusterMode::Active, "created 2026-08-29")
            .expect("creates");
        assert_eq!(identity.mode(), ClusterMode::Active);
        assert!(identity.content_hash() != 0);

        let location = dir("create");
        identity.pin(&location).expect("pins");
        let opened = ClusterIdentity::open(&location).expect("verifies");
        assert_eq!(opened, identity);
        let _ = std::fs::remove_dir_all(&location);
    }

    #[test]
    fn a_tampered_record_fails_the_open() {
        let location = dir("tamper");
        ClusterIdentity::create("cell-a", ClusterMode::Backup, "created")
            .expect("creates")
            .pin(&location)
            .expect("pins");
        let record = location.join("cluster-identity");
        let text = std::fs::read_to_string(&record).expect("reads");
        // A quiet mode flip inside the pinned record: the hash no longer
        // verifies — backup can never be edited into active.
        std::fs::write(&record, text.replace("mode: backup", "mode: active")).expect("rewrites");
        assert_eq!(ClusterIdentity::open(&location).err(), Some(IdentityError::Tampered));
        let _ = std::fs::remove_dir_all(&location);
    }

    #[test]
    fn mode_changes_refuse_typed_whatever_the_current_mode() {
        for mode in [ClusterMode::Active, ClusterMode::Backup] {
            let identity = ClusterIdentity::create("cell-a", mode, "created").expect("creates");
            assert_eq!(
                identity.change_mode(ClusterMode::Active).err(),
                Some(IdentityError::ModeImmutable),
                "the identity is immutable, not merely the backup's"
            );
        }
    }

    #[test]
    fn an_absent_or_malformed_identity_fails_typed() {
        let location = dir("absent");
        assert_eq!(ClusterIdentity::open(&location).err(), Some(IdentityError::Absent));
        std::fs::write(location.join("cluster-identity"), "not a record").unwrap();
        assert_eq!(ClusterIdentity::open(&location).err(), Some(IdentityError::Malformed));
        let _ = std::fs::remove_dir_all(&location);
    }
}
