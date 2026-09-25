use std::net::SocketAddr;
use std::path::PathBuf;

use tb_core::{DatabaseId, ReplicaId, TenantId};
use tb_replication::LocalAuthorityScope;

/// First bounded mTLS receive profile. These limits apply only when replication
/// networking is explicitly enabled; local/admin mode keeps its existing floor.
pub const MIN_REPLICATION_PROCESS_BYTES: usize = 4 * 1024 * 1024 * 1024;
/// Minimum network-mode headroom reserved outside logical admission.
pub const MIN_REPLICATION_HEADROOM_BYTES: usize = 1024 * 1024 * 1024;

const M: usize = 4 * 1024 * 1024;
const W: usize = 64 * 1024;

/// Logical process admission charged before one replication stream is handed to
/// Tonic. This is a conservative admission value, not an allocator/RSS bound.
pub const REPLICATION_STREAM_RESERVATION_BYTES: usize = 8 * M + 2 * W + 64 * 1024;
/// Logical process admission charged before Prost materializes a Sync message.
pub const REPLICATION_PREDECODE_RESERVATION_BYTES: usize = 4 * M + 1024 * 1024;

/// Network-mode process/headroom contract, separate from the local-only server.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ReplicationMemoryProfile {
    process_limit_bytes: usize,
    headroom_bytes: usize,
}

impl ReplicationMemoryProfile {
    /// Validate the first receive profile against the explicit process budget.
    ///
    /// # Errors
    /// Returns an error when the process limit or reserved headroom is below
    /// the qualification profile.
    pub fn new(
        process_limit_bytes: usize,
        headroom_bytes: usize,
    ) -> Result<Self, ReplicationConfigError> {
        let required_headroom = MIN_REPLICATION_HEADROOM_BYTES.max(process_limit_bytes / 4);
        if process_limit_bytes < MIN_REPLICATION_PROCESS_BYTES {
            return Err(ReplicationConfigError::ReplicationMemoryLimitTooSmall {
                configured: process_limit_bytes,
                minimum: MIN_REPLICATION_PROCESS_BYTES,
            });
        }
        if headroom_bytes < required_headroom || headroom_bytes >= process_limit_bytes {
            return Err(ReplicationConfigError::ReplicationHeadroomTooSmall {
                configured: headroom_bytes,
                minimum: required_headroom,
            });
        }

        let profile = Self {
            process_limit_bytes,
            headroom_bytes,
        };
        if profile.minimum_usable_bytes() > process_limit_bytes - headroom_bytes {
            return Err(ReplicationConfigError::ReplicationBudgetCannotCoverProfile);
        }
        Ok(profile)
    }

    /// Configured process limit including excluded runtime headroom.
    #[must_use]
    pub const fn process_limit_bytes(self) -> usize {
        self.process_limit_bytes
    }

    /// Bytes excluded from logical admission for runtime/allocator overhead.
    #[must_use]
    pub const fn headroom_bytes(self) -> usize {
        self.headroom_bytes
    }

    /// Minimum work allowance for the configured network concurrency.
    ///
    /// This sums eight connection leases, eight stream leases, eight
    /// pre-Prost frame leases, transport/semantic working sets and the existing
    /// local storage startup floor. Runtime storage/journal recovery can need
    /// additional capacity and remains subject to `MemoryBudget` admission.
    #[must_use]
    pub const fn minimum_usable_bytes(self) -> usize {
        8 * (2 * 1024 * 1024)
            + 8 * REPLICATION_STREAM_RESERVATION_BYTES
            + 8 * (REPLICATION_PREDECODE_RESERVATION_BYTES + 10 * 1024 * 1024 + 13 * 1024 * 1024)
            + 64 * 1024 * 1024
    }
}

/// Explicit settings for one network-facing replication listener.
#[derive(Clone, Debug)]
pub struct ReplicationConfig {
    /// Address for the mTLS gRPC listener, separate from loopback admin HTTP.
    pub listen_addr: SocketAddr,
    /// Root for accepted authority and per-origin inbox journals.
    pub data_dir: PathBuf,
    /// Signed `TBCAU001` authority certificate file.
    pub authority_certificate: PathBuf,
    /// Raw 32-byte Ed25519 trust public key file.
    pub trusted_public_key: PathBuf,
    /// Server certificate chain in PEM format.
    pub server_certificate: PathBuf,
    /// Server private key in PEM format.
    pub server_private_key: PathBuf,
    /// Client CA roots in PEM format.
    pub client_ca_roots: PathBuf,
    /// Local shard identity independently supplied to authority recovery.
    pub local_scope: LocalAuthorityScope,
    /// Local member incarnation, which is checked against the TLS leaf and
    /// signed member row before the receiver starts.
    pub local_incarnation: [u8; 16],
    /// Validated network-mode process/headroom profile.
    pub memory_profile: ReplicationMemoryProfile,
    /// Hidden process-test barrier for one receiver append.
    #[cfg(feature = "replication-test-hooks")]
    pub(crate) test_append_barrier_dir: Option<PathBuf>,
}

impl ReplicationConfig {
    /// Construct explicit replication settings without touching the filesystem.
    ///
    /// # Errors
    /// Returns an error when a required path is empty, the local incarnation is
    /// zero, or the configured network memory profile is below its minimum.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        listen_addr: SocketAddr,
        data_dir: PathBuf,
        authority_certificate: PathBuf,
        trusted_public_key: PathBuf,
        server_certificate: PathBuf,
        server_private_key: PathBuf,
        client_ca_roots: PathBuf,
        local_scope: LocalAuthorityScope,
        local_incarnation: [u8; 16],
        process_limit_bytes: usize,
        headroom_bytes: usize,
    ) -> Result<Self, ReplicationConfigError> {
        for path in [
            &data_dir,
            &authority_certificate,
            &trusted_public_key,
            &server_certificate,
            &server_private_key,
            &client_ca_roots,
        ] {
            if path.as_os_str().is_empty() {
                return Err(ReplicationConfigError::EmptyPath);
            }
        }
        if local_incarnation == [0; 16] {
            return Err(ReplicationConfigError::ZeroLocalIncarnation);
        }

        Ok(Self {
            listen_addr,
            data_dir,
            authority_certificate,
            trusted_public_key,
            server_certificate,
            server_private_key,
            client_ca_roots,
            local_scope,
            local_incarnation,
            memory_profile: ReplicationMemoryProfile::new(process_limit_bytes, headroom_bytes)?,
            #[cfg(feature = "replication-test-hooks")]
            test_append_barrier_dir: None,
        })
    }

    /// Configure the one-shot append barrier used by process tests.
    #[cfg(feature = "replication-test-hooks")]
    #[doc(hidden)]
    pub fn set_test_append_barrier_dir(&mut self, dir: Option<PathBuf>) {
        self.test_append_barrier_dir = dir;
    }
}

/// Configuration or qualification-profile error for opt-in replication mode.
#[derive(Debug, thiserror::Error)]
pub enum ReplicationConfigError {
    /// A required file or data directory option was empty.
    #[error("replication paths must not be empty")]
    EmptyPath,
    /// Zero is not a valid member incarnation.
    #[error("local member incarnation must be nonzero")]
    ZeroLocalIncarnation,
    /// The opt-in replication profile requires a larger process memory limit.
    #[error("replication process memory limit is {configured} bytes; minimum is {minimum}")]
    ReplicationMemoryLimitTooSmall { configured: usize, minimum: usize },
    /// Network mode reserves at least one GiB and one quarter of the process cap.
    #[error("replication headroom is {configured} bytes; minimum is {minimum}")]
    ReplicationHeadroomTooSmall { configured: usize, minimum: usize },
    /// The static network profile does not fit in the usable process budget.
    #[error("replication memory budget cannot cover the selected receive profile")]
    ReplicationBudgetCannotCoverProfile,
    /// The local identity is not a member of the signed accepted authority.
    #[error("local identity does not match the accepted authority")]
    LocalIdentityMismatch,
    /// A required trust or TLS input could not be loaded or decoded.
    #[error("invalid replication trust or TLS material: {0}")]
    TlsMaterial(String),
    /// A replication data directory cannot be opened safely.
    #[error("replication authority could not be opened: {0}")]
    Authority(String),
}

impl From<std::io::Error> for ReplicationConfigError {
    fn from(error: std::io::Error) -> Self {
        Self::TlsMaterial(error.to_string())
    }
}

/// Parse a lowercase or uppercase exact-length hexadecimal byte string.
///
/// # Errors
/// Returns `None` when the value contains a non-hex digit or wrong length.
#[must_use]
pub fn parse_fixed_hex<const N: usize>(value: &str) -> Option<[u8; N]> {
    if value.len() != N * 2 {
        return None;
    }
    let mut output = [0_u8; N];
    for (index, pair) in value.as_bytes().as_chunks::<2>().0.iter().enumerate() {
        let high = hex_digit(pair[0])?;
        let low = hex_digit(pair[1])?;
        output[index] = (high << 4) | low;
    }
    Some(output)
}

fn hex_digit(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        b'A'..=b'F' => Some(byte - b'A' + 10),
        _ => None,
    }
}

/// Parse the local ULID scope from canonical command-line identifiers.
///
/// # Errors
/// Returns a readable error if any identifier is not canonical.
pub fn parse_local_authority_scope(
    cluster: &str,
    tenant: &str,
    database: &str,
    shard: u64,
    member: &str,
) -> Result<LocalAuthorityScope, ReplicationConfigError> {
    let cluster = parse_fixed_hex(cluster).ok_or_else(|| {
        ReplicationConfigError::TlsMaterial("cluster ID must be exactly 32 hex digits".into())
    })?;
    let tenant = tenant.parse::<TenantId>().map_err(|error| {
        ReplicationConfigError::TlsMaterial(format!("invalid tenant ID: {error}"))
    })?;
    let database = database.parse::<DatabaseId>().map_err(|error| {
        ReplicationConfigError::TlsMaterial(format!("invalid database ID: {error}"))
    })?;
    let member = member.parse::<ReplicaId>().map_err(|error| {
        ReplicationConfigError::TlsMaterial(format!("invalid member ID: {error}"))
    })?;
    if shard == 0 {
        return Err(ReplicationConfigError::TlsMaterial(
            "shard ID must be nonzero".into(),
        ));
    }
    Ok(LocalAuthorityScope {
        cluster,
        tenant,
        database,
        shard: tb_core::ShardId::new(shard),
        member,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn network_memory_profile_has_explicit_minimum_and_headroom() {
        assert!(
            ReplicationMemoryProfile::new(
                MIN_REPLICATION_PROCESS_BYTES - 1,
                MIN_REPLICATION_HEADROOM_BYTES,
            )
            .is_err()
        );
        assert!(
            ReplicationMemoryProfile::new(
                MIN_REPLICATION_PROCESS_BYTES,
                MIN_REPLICATION_HEADROOM_BYTES - 1,
            )
            .is_err()
        );
        let profile = ReplicationMemoryProfile::new(
            MIN_REPLICATION_PROCESS_BYTES,
            MIN_REPLICATION_HEADROOM_BYTES,
        )
        .expect("first receive profile fits");
        assert!(
            profile.minimum_usable_bytes()
                < profile.process_limit_bytes() - profile.headroom_bytes()
        );
    }

    #[test]
    fn fixed_hex_requires_exact_size_and_valid_digits() {
        assert_eq!(parse_fixed_hex::<2>("00aF"), Some([0, 175]));
        assert_eq!(parse_fixed_hex::<2>("00a"), None);
        assert_eq!(parse_fixed_hex::<2>("00xF"), None);
    }
}
