//! Bounded API-key authentication for the local-v1 client listener.
//!
//! A registry is one immutable, validated generation. The listener swaps it
//! together with its TLS configuration; queued work must consult the current
//! generation again immediately before entering the document worker.

use std::collections::BTreeMap;
use std::fs::{self, File, OpenOptions};
use std::io::Read;
use std::num::NonZeroU32;
use std::path::Path;
use std::sync::Arc;

use axum::http::HeaderMap;
use axum::http::header::AUTHORIZATION;
use base64::Engine as _;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use governor::{DefaultDirectRateLimiter, Quota, RateLimiter};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;
use tb_core::{
    Attribution, CollectionId, DatabaseId, MemoryBudget, ShardId, TenantId, WorkReservation,
};
use tokio::sync::{OwnedSemaphorePermit, Semaphore};
use zeroize::Zeroize;

use crate::LocalV1Scope;

const MAX_REGISTRY_FILE_BYTES: usize = 256 * 1024;
const MAX_KEYS: usize = 128;
const MAX_COLLECTION_GRANTS: usize = 4_096;
/// Includes bounded parser temporaries and one resident registry generation.
const REGISTRY_RESERVATION_BYTES: usize = 2 * 1024 * 1024;
const MAX_AUTHORIZATION_BYTES: usize = 128;
const TOKEN_PREFIX: &str = "tbk1";
const VERIFIER_DOMAIN: &[u8] = b"TB-APIKEY-V1\0";

/// The exact shard target from the request path, never a source of authority.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct ClientTarget {
    pub(crate) tenant: TenantId,
    pub(crate) database: DatabaseId,
    pub(crate) shard: ShardId,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ClientPermission {
    Read,
    Write,
}

/// Bounded identity retained in a queued request. It contains no bearer secret.
#[derive(Clone, Copy, Eq, PartialEq)]
pub(crate) struct AuthenticatedKey {
    id: [u8; 16],
    verifier_digest: [u8; 32],
}

impl std::fmt::Debug for AuthenticatedKey {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("AuthenticatedKey([redacted])")
    }
}

/// A per-entry concurrency slot held through the response write.
pub(crate) struct KeyAdmission {
    _slot: OwnedSemaphorePermit,
    entry: Arc<KeyEntry>,
    // A queued worker or response can outlive the registry generation after
    // reload; its entry must remain charged to the process memory budget.
    _registry_reservation: Arc<WorkReservation>,
}

/// An immutable generation: no request-supplied key can allocate limiter state.
pub(crate) struct AuthRegistry {
    keys: BTreeMap<[u8; 16], Arc<KeyEntry>>,
    unknown_attempts: DefaultDirectRateLimiter,
    reservation: Arc<WorkReservation>,
}

struct KeyEntry {
    verifier_digest: [u8; 32],
    target: ClientTarget,
    collections: Vec<CollectionId>,
    permissions: Permissions,
    revoked: bool,
    limiter: DefaultDirectRateLimiter,
    in_flight: Arc<Semaphore>,
}

#[derive(Clone, Copy)]
struct Permissions {
    read: bool,
    write: bool,
}

impl Permissions {
    const fn allows(self, permission: ClientPermission) -> bool {
        match permission {
            ClientPermission::Read => self.read,
            ClientPermission::Write => self.write,
        }
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RegistryFile {
    version: u64,
    keys: Vec<KeyFile>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct KeyFile {
    id: String,
    verifier_sha256: String,
    tenant: String,
    database: String,
    shard: String,
    collections: Vec<String>,
    permissions: Vec<String>,
    revoked: bool,
    rate_per_second: Option<u32>,
    burst: Option<u32>,
    max_in_flight: Option<usize>,
}

impl AuthRegistry {
    /// Load one fully validated registry, bounded both on disk and in memory.
    /// The caller must swap this with the validated TLS configuration as one
    /// generation; a failed reload must clear client admission and readiness.
    ///
    /// # Errors
    /// Rejects unreadable or unsafe material, invalid entries or grants, and
    /// process-budget admission failure.
    pub(crate) fn load_generation(
        path: &Path,
        scope: &LocalV1Scope,
        budget: &MemoryBudget,
        attribution: Attribution,
    ) -> Result<Self, AuthLoadError> {
        let reservation = budget
            .try_reserve_work(attribution, REGISTRY_RESERVATION_BYTES, 1)
            .map_err(|_| AuthLoadError::Capacity)?;
        let bytes = read_protected_bounded(path)?;
        let parsed: RegistryFile =
            serde_json::from_slice(&bytes).map_err(|_| AuthLoadError::InvalidRegistry)?;
        if parsed.version != 1 || parsed.keys.len() > MAX_KEYS {
            return Err(AuthLoadError::InvalidRegistry);
        }
        let mut keys = BTreeMap::new();
        let mut total_grants = 0_usize;
        for file in parsed.keys {
            total_grants = total_grants
                .checked_add(file.collections.len())
                .filter(|count| *count <= MAX_COLLECTION_GRANTS)
                .ok_or(AuthLoadError::InvalidRegistry)?;
            let (id, entry) = KeyEntry::parse(file, scope)?;
            if keys.insert(id, Arc::new(entry)).is_some() {
                return Err(AuthLoadError::InvalidRegistry);
            }
        }
        Ok(Self {
            keys,
            unknown_attempts: RateLimiter::direct(
                Quota::per_second(NonZeroU32::new(20).expect("nonzero rate"))
                    .allow_burst(NonZeroU32::new(40).expect("nonzero burst")),
            ),
            reservation: Arc::new(reservation),
        })
    }

    /// Authenticate exactly one bounded `Authorization: Bearer` header.
    /// Missing, malformed, unknown, bad-verifier and revoked keys share one
    /// externally visible error class. The raw secret is zeroized after hash.
    ///
    /// # Errors
    /// Returns an invalid-key or global invalid-attempt rate-limit result.
    pub(crate) fn authenticate(&self, headers: &HeaderMap) -> Result<AuthenticatedKey, AuthError> {
        let mut values = headers.get_all(AUTHORIZATION).iter();
        let Some(header) = values.next() else {
            return self.reject_invalid_key();
        };
        if values.next().is_some() {
            return self.reject_invalid_key();
        }
        let bytes = header.as_bytes();
        if bytes.len() > MAX_AUTHORIZATION_BYTES || !bytes.starts_with(b"Bearer ") {
            return self.reject_invalid_key();
        }
        let Some((id, mut secret)) = parse_token(&bytes[b"Bearer ".len()..]) else {
            return self.reject_invalid_key();
        };
        let digest = verifier_digest(id, &secret);
        secret.zeroize();
        let entry = self.keys.get(&id);
        let expected = entry.map_or(&[0_u8; 32], |entry| &entry.verifier_digest);
        let matches = bool::from(digest.ct_eq(expected));
        if !matches || entry.is_none_or(|entry| entry.revoked) {
            return self.reject_invalid_key();
        }
        Ok(AuthenticatedKey {
            id,
            verifier_digest: digest,
        })
    }

    /// Check the current grant and take one direct rate/concurrency admission.
    /// Call this before collecting a body, with an empty collection slice for
    /// commit routes; call `reauthorize` after decoding every write collection.
    ///
    /// # Errors
    /// Rejects a stale key, forbidden scope or exhausted key limit.
    pub(crate) fn authorize(
        &self,
        key: AuthenticatedKey,
        target: ClientTarget,
        permission: ClientPermission,
        collections: &[CollectionId],
    ) -> Result<KeyAdmission, AuthError> {
        let entry = self.checked_identity(key)?;
        Self::charge_rate(&entry)?;
        Self::check_grant(&entry, target, permission, collections)?;
        self.acquire_slot(entry)
    }

    /// At a queue-to-work cut, check the latest grant and retain this permit
    /// only if it belongs to the current generation. A reload drops the old
    /// slot, then charges and acquires the replacement entry's limits.
    ///
    /// # Errors
    /// Rejects a removed or revoked key, forbidden scope or exhausted current
    /// rate/concurrency limit. The previous permit is dropped on every error.
    pub(crate) fn reauthorize(
        &self,
        key: AuthenticatedKey,
        target: ClientTarget,
        permission: ClientPermission,
        collections: &[CollectionId],
        previous: KeyAdmission,
    ) -> Result<KeyAdmission, AuthError> {
        let entry = self.checked_identity(key)?;
        if Arc::ptr_eq(&previous.entry, &entry) {
            Self::check_grant(&entry, target, permission, collections)?;
            return Ok(previous);
        }
        drop(previous);
        Self::charge_rate(&entry)?;
        Self::check_grant(&entry, target, permission, collections)?;
        self.acquire_slot(entry)
    }

    fn charge_rate(entry: &KeyEntry) -> Result<(), AuthError> {
        if entry.limiter.check().is_err() {
            return Err(AuthError::RateLimited);
        }
        Ok(())
    }

    fn acquire_slot(&self, entry: Arc<KeyEntry>) -> Result<KeyAdmission, AuthError> {
        let slot = Arc::clone(&entry.in_flight)
            .try_acquire_owned()
            .map_err(|_| AuthError::RateLimited)?;
        Ok(KeyAdmission {
            _slot: slot,
            entry,
            _registry_reservation: Arc::clone(&self.reservation),
        })
    }

    fn checked_identity(&self, key: AuthenticatedKey) -> Result<Arc<KeyEntry>, AuthError> {
        let entry = self.keys.get(&key.id).ok_or(AuthError::InvalidKey)?;
        if entry.revoked || !bool::from(key.verifier_digest.ct_eq(&entry.verifier_digest)) {
            return Err(AuthError::InvalidKey);
        }
        Ok(Arc::clone(entry))
    }

    fn check_grant(
        entry: &KeyEntry,
        target: ClientTarget,
        permission: ClientPermission,
        collections: &[CollectionId],
    ) -> Result<(), AuthError> {
        if entry.target != target
            || !entry.permissions.allows(permission)
            || collections
                .iter()
                .any(|collection| entry.collections.binary_search(collection).is_err())
        {
            return Err(AuthError::ForbiddenScope);
        }
        Ok(())
    }

    fn reject_invalid_key(&self) -> Result<AuthenticatedKey, AuthError> {
        if self.unknown_attempts.check().is_err() {
            Err(AuthError::RateLimited)
        } else {
            Err(AuthError::InvalidKey)
        }
    }
}

impl KeyEntry {
    fn parse(file: KeyFile, scope: &LocalV1Scope) -> Result<([u8; 16], Self), AuthLoadError> {
        let id = decode_fixed_base64::<16>(&file.id).ok_or(AuthLoadError::InvalidRegistry)?;
        let verifier_digest =
            decode_lower_hex::<32>(&file.verifier_sha256).ok_or(AuthLoadError::InvalidRegistry)?;
        let target = ClientTarget {
            tenant: TenantId::parse_canonical(&file.tenant)
                .map_err(|_| AuthLoadError::InvalidRegistry)?,
            database: DatabaseId::parse_canonical(&file.database)
                .map_err(|_| AuthLoadError::InvalidRegistry)?,
            shard: ShardId::new(
                parse_canonical_u64(&file.shard).ok_or(AuthLoadError::InvalidRegistry)?,
            ),
        };
        let mut collections = Vec::with_capacity(file.collections.len());
        for collection in file.collections {
            let parsed = CollectionId::parse_canonical(&collection)
                .map_err(|_| AuthLoadError::InvalidRegistry)?;
            if collections
                .last()
                .is_some_and(|previous| previous >= &parsed)
            {
                return Err(AuthLoadError::InvalidRegistry);
            }
            collections.push(parsed);
        }
        if !scope.allows_grant(target.tenant, target.database, target.shard, &collections) {
            return Err(AuthLoadError::InvalidRegistry);
        }
        let permissions = parse_permissions(&file.permissions)?;
        let rate = file.rate_per_second.unwrap_or(5);
        let burst = file.burst.unwrap_or(10);
        let in_flight = file.max_in_flight.unwrap_or(2);
        if !(1..=100).contains(&rate)
            || !(1..=200).contains(&burst)
            || !(1..=4).contains(&in_flight)
        {
            return Err(AuthLoadError::InvalidRegistry);
        }
        Ok((
            id,
            Self {
                verifier_digest,
                target,
                collections,
                permissions,
                revoked: file.revoked,
                limiter: RateLimiter::direct(
                    Quota::per_second(NonZeroU32::new(rate).expect("validated nonzero rate"))
                        .allow_burst(NonZeroU32::new(burst).expect("validated nonzero burst")),
                ),
                in_flight: Arc::new(Semaphore::new(in_flight)),
            },
        ))
    }
}

fn parse_permissions(values: &[String]) -> Result<Permissions, AuthLoadError> {
    let mut permissions = Permissions {
        read: false,
        write: false,
    };
    match values {
        [read] if read == "read" => permissions.read = true,
        [write] if write == "write" => permissions.write = true,
        [read, write] if read == "read" && write == "write" => {
            permissions.read = true;
            permissions.write = true;
        }
        _ => return Err(AuthLoadError::InvalidRegistry),
    }
    Ok(permissions)
}

fn parse_token(token: &[u8]) -> Option<([u8; 16], [u8; 32])> {
    let token = std::str::from_utf8(token).ok()?;
    let mut parts = token.split('.');
    if parts.next()? != TOKEN_PREFIX {
        return None;
    }
    let id = decode_fixed_base64::<16>(parts.next()?)?;
    let mut secret = decode_fixed_base64::<32>(parts.next()?)?;
    if parts.next().is_some() {
        secret.zeroize();
        return None;
    }
    Some((id, secret))
}

fn decode_fixed_base64<const N: usize>(encoded: &str) -> Option<[u8; N]> {
    if N > 32 || encoded.len() != (N * 8).div_ceil(6) {
        return None;
    }
    // base64's checked decoder requires space for its conservative estimate,
    // which is two bytes greater than the exact length for these unpadded sizes.
    let mut scratch = [0_u8; 34];
    let count = URL_SAFE_NO_PAD.decode_slice(encoded, &mut scratch).ok();
    if count != Some(N) || URL_SAFE_NO_PAD.encode(&scratch[..N]) != encoded {
        scratch.zeroize();
        return None;
    }
    let mut decoded = [0_u8; N];
    decoded.copy_from_slice(&scratch[..N]);
    scratch.zeroize();
    Some(decoded)
}

fn parse_canonical_u64(value: &str) -> Option<u64> {
    let bytes = value.as_bytes();
    if bytes.is_empty()
        || (bytes.len() > 1 && bytes[0] == b'0')
        || !bytes.iter().all(u8::is_ascii_digit)
    {
        return None;
    }
    value.parse().ok()
}

fn decode_lower_hex<const N: usize>(value: &str) -> Option<[u8; N]> {
    if value.len() != 2 * N {
        return None;
    }
    let mut result = [0_u8; N];
    for (index, pair) in value.as_bytes().as_chunks::<2>().0.iter().enumerate() {
        let high = lower_hex_nibble(pair[0])?;
        let low = lower_hex_nibble(pair[1])?;
        result[index] = (high << 4) | low;
    }
    Some(result)
}

fn lower_hex_nibble(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        _ => None,
    }
}

#[must_use]
pub(crate) fn verifier_digest(id: [u8; 16], secret: &[u8; 32]) -> [u8; 32] {
    let mut hasher = Sha256::new();
    hasher.update(VERIFIER_DOMAIN);
    hasher.update(id);
    hasher.update(secret);
    hasher.finalize().into()
}

fn read_protected_bounded(path: &Path) -> Result<Vec<u8>, AuthLoadError> {
    let path_metadata = fs::symlink_metadata(path).map_err(AuthLoadError::Io)?;
    if !path_metadata.is_file() || path_metadata.len() > MAX_REGISTRY_FILE_BYTES as u64 {
        return Err(AuthLoadError::UnsafeFile);
    }
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NOFOLLOW);
    }
    let file: File = options.open(path).map_err(AuthLoadError::Io)?;
    let metadata = file.metadata().map_err(AuthLoadError::Io)?;
    if !metadata.is_file() || metadata.len() > MAX_REGISTRY_FILE_BYTES as u64 {
        return Err(AuthLoadError::UnsafeFile);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::{MetadataExt, PermissionsExt};
        if metadata.dev() != path_metadata.dev()
            || metadata.ino() != path_metadata.ino()
            || metadata.permissions().mode() & 0o077 != 0
        {
            return Err(AuthLoadError::UnsafeFile);
        }
    }
    let capacity = usize::try_from(metadata.len()).map_err(|_| AuthLoadError::UnsafeFile)?;
    let mut bytes = Vec::with_capacity(capacity);
    file.take(MAX_REGISTRY_FILE_BYTES as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(AuthLoadError::Io)?;
    if bytes.len() > MAX_REGISTRY_FILE_BYTES {
        return Err(AuthLoadError::UnsafeFile);
    }
    Ok(bytes)
}

#[derive(Debug, thiserror::Error)]
pub(crate) enum AuthLoadError {
    #[error("cannot read API-key registry")]
    Io(#[source] std::io::Error),
    #[error("API-key registry is not a protected bounded regular file")]
    UnsafeFile,
    #[error("API-key registry has invalid format or grants")]
    InvalidRegistry,
    #[error("capacity unavailable for API-key registry")]
    Capacity,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum AuthError {
    InvalidKey,
    ForbiddenScope,
    RateLimited,
}

#[cfg(test)]
mod tests {
    use super::*;
    use tb_core::{BudgetConfig, MemoryCategory, WorkerId};

    #[test]
    fn token_parser_requires_canonical_fixed_width_unpadded_base64url() {
        let id = [3_u8; 16];
        let secret = [7_u8; 32];
        let token = format!(
            "tbk1.{}.{}",
            URL_SAFE_NO_PAD.encode(id),
            URL_SAFE_NO_PAD.encode(secret)
        );
        assert_eq!(parse_token(token.as_bytes()), Some((id, secret)));
        assert!(parse_token(format!("{token}=").as_bytes()).is_none());
        assert!(parse_token(format!("{token}.extra").as_bytes()).is_none());
        assert!(parse_token(token.replacen("tbk1", "TBK1", 1).as_bytes()).is_none());
    }

    #[test]
    fn registry_parser_rejects_duplicates_and_noncanonical_values() {
        assert!(
            serde_json::from_str::<RegistryFile>(r#"{"version":1,"keys":[],"keys":[]}"#).is_err()
        );
        assert!(
            serde_json::from_str::<RegistryFile>(r#"{"version":1,"keys":[],"extra":1}"#).is_err()
        );
        assert_eq!(parse_canonical_u64("0"), Some(0));
        for invalid in [
            "",
            "00",
            "01",
            "+1",
            "-1",
            "1.0",
            " 1",
            "18446744073709551616",
        ] {
            assert_eq!(parse_canonical_u64(invalid), None);
        }
        assert!(decode_lower_hex::<1>("af").is_some());
        assert!(decode_lower_hex::<1>("AF").is_none());
    }

    #[test]
    fn queued_admission_obeys_reloaded_limits_and_revocation() {
        let tenant = TenantId::from_bytes([1; 16]);
        let database = DatabaseId::from_bytes([2; 16]);
        let shard = ShardId::new(0);
        let collection = CollectionId::from_bytes([3; 16]);
        let scope = LocalV1Scope::configured(tenant, database, shard, [collection])
            .expect("fixed test scope");
        let budget = MemoryBudget::new(BudgetConfig::new(16 * 1024 * 1024, 0).expect("budget"));
        let attribution = Attribution {
            tenant,
            worker: WorkerId(1),
            category: MemoryCategory::RuntimeOverhead,
        };
        let id = [4_u8; 16];
        let digest = verifier_digest(id, &[5_u8; 32]);
        let key = AuthenticatedKey {
            id,
            verifier_digest: digest,
        };
        let target = ClientTarget {
            tenant,
            database,
            shard,
        };
        let make_registry = |max_in_flight, revoked, rate, burst| {
            let verifier_sha256 = digest
                .iter()
                .map(|byte| format!("{byte:02x}"))
                .collect::<String>();
            let file = KeyFile {
                id: URL_SAFE_NO_PAD.encode(id),
                verifier_sha256,
                tenant: tenant.to_string(),
                database: database.to_string(),
                shard: "0".into(),
                collections: vec![collection.to_string()],
                permissions: vec!["read".into(), "write".into()],
                revoked,
                rate_per_second: Some(rate),
                burst: Some(burst),
                max_in_flight: Some(max_in_flight),
            };
            let (id, entry) = KeyEntry::parse(file, &scope).expect("valid grant");
            AuthRegistry {
                keys: BTreeMap::from([(id, Arc::new(entry))]),
                unknown_attempts: RateLimiter::direct(Quota::per_second(
                    NonZeroU32::new(20).expect("nonzero"),
                )),
                reservation: Arc::new(
                    budget
                        .try_reserve_work(attribution, REGISTRY_RESERVATION_BYTES, 1)
                        .expect("registry budget"),
                ),
            }
        };
        let old = make_registry(2, false, 100, 200);
        let current = make_registry(1, false, 100, 200);
        let old_slot = old
            .authorize(key, target, ClientPermission::Read, &[collection])
            .expect("old admission");
        let current_slot = current
            .authorize(key, target, ClientPermission::Read, &[collection])
            .expect("occupy new one-slot limit");
        assert!(matches!(
            current.reauthorize(key, target, ClientPermission::Read, &[collection], old_slot),
            Err(AuthError::RateLimited)
        ));
        drop(current_slot);

        let revived_old_slot = old
            .authorize(key, target, ClientPermission::Read, &[collection])
            .expect("second old admission");
        let revoked = make_registry(1, true, 100, 200);
        assert!(matches!(
            revoked.reauthorize(
                key,
                target,
                ClientPermission::Read,
                &[collection],
                revived_old_slot
            ),
            Err(AuthError::InvalidKey)
        ));

        let limited = make_registry(1, false, 1, 1);
        let foreign_target = ClientTarget {
            shard: ShardId::new(1),
            ..target
        };
        assert!(matches!(
            limited.authorize(key, foreign_target, ClientPermission::Read, &[collection]),
            Err(AuthError::ForbiddenScope)
        ));
        assert!(matches!(
            limited.authorize(key, target, ClientPermission::Read, &[collection]),
            Err(AuthError::RateLimited)
        ));

        let reloaded_limited = make_registry(1, false, 1, 1);
        let old_slot = old
            .authorize(key, target, ClientPermission::Read, &[collection])
            .expect("old admission before reload");
        assert!(matches!(
            reloaded_limited.reauthorize(
                key,
                foreign_target,
                ClientPermission::Read,
                &[collection],
                old_slot
            ),
            Err(AuthError::ForbiddenScope)
        ));
        assert!(matches!(
            reloaded_limited.authorize(key, target, ClientPermission::Read, &[collection]),
            Err(AuthError::RateLimited)
        ));

        let detached = make_registry(1, false, 100, 200);
        let detached_slot = detached
            .authorize(key, target, ClientPermission::Read, &[collection])
            .expect("admission before registry replacement");
        let reserved = budget.snapshot().reserved_bytes;
        drop(detached);
        assert_eq!(budget.snapshot().reserved_bytes, reserved);
        drop(detached_slot);
        assert_eq!(
            budget.snapshot().reserved_bytes,
            reserved - REGISTRY_RESERVATION_BYTES
        );
    }
}
