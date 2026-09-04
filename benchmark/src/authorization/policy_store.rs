//! Durable, authenticated authorization-policy publication journal.
//!
//! The store is intentionally small and append-only. Every complete record is
//! authenticated with a store-specific BLAKE3 key, and replay reconstructs the
//! latest policy for each exact tenant, Project, and database scope. The key is
//! supplied by the owning runtime so production can source it from the selected
//! key provider without putting key lifecycle policy in this module.

use std::{
    collections::{BTreeMap, BTreeSet},
    fs::{File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::Arc,
};

#[cfg(unix)]
use std::os::unix::{fs::OpenOptionsExt, fs::PermissionsExt};

use zeroize::Zeroize;

use super::{
    AUTHORIZATION_FORMAT_VERSION, AuthorizationError, ClaimComparison, ClaimPredicate,
    FieldProjection, Operation, Policy, PolicyRule, RelationshipPredicate, ResourceScope,
};
use crate::identity::{
    AuthorizationPolicyUlid, DatabaseUlid, PrincipalGrantUlid, PrincipalUlid, ProjectUlid,
    TenantUlid,
};

const JOURNAL_MAGIC: &[u8; 8] = b"TBAPJNL1";
const RECORD_VERSION: u8 = 1;
const AUTHENTICATION_BYTES: usize = 32;
const MAX_JOURNAL_BYTES: u64 = 64 * 1024 * 1024;
const MAX_RECORD_BYTES: usize = 4 * 1024 * 1024;
const RECORD_DOMAIN: &[u8] = b"terrabase.authorization.policy-journal.record.v1\0";
const HEAD_MAGIC: &[u8; 8] = b"TBAPHEAD";
const HEAD_DOMAIN: &[u8] = b"terrabase.authorization.policy-journal.head.v1\0";

/// An immutable view of the latest durable policy for each database scope.
#[derive(Clone, Default, Eq, PartialEq)]
pub struct PolicyStoreSnapshot {
    policies: BTreeMap<ResourceScope, Policy>,
}

/// A move-only key handle for the authenticated policy journal.
///
/// Runtimes should construct this from their trusted key-provider adapter and
/// pass it to [`AuthorizationPolicyStore::open_with_key`]. It is deliberately
/// neither `Clone` nor `Copy`.
pub struct PolicyStoreKey(Vec<u8>);

impl PolicyStoreKey {
    /// Wraps key material supplied by a trusted key provider.
    #[must_use]
    pub fn from_bytes(bytes: [u8; AUTHENTICATION_BYTES]) -> Self {
        Self(bytes.into())
    }

    fn into_bytes(mut self) -> Vec<u8> {
        std::mem::take(&mut self.0)
    }
}

impl Drop for PolicyStoreKey {
    fn drop(&mut self) {
        self.0.zeroize();
    }
}

impl std::fmt::Debug for PolicyStoreSnapshot {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("PolicyStoreSnapshot")
            .field("policy_count", &self.policies.len())
            .finish_non_exhaustive()
    }
}

impl PolicyStoreSnapshot {
    /// Returns the number of scopes with a published policy.
    #[must_use]
    pub fn len(&self) -> usize {
        self.policies.len()
    }

    /// Returns whether no scope has a published policy.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.policies.is_empty()
    }

    /// Returns the latest policy for an exact scope.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::PolicyMissing`] when the scope has no
    /// durable publication.
    pub fn policy(&self, scope: &ResourceScope) -> Result<&Policy, AuthorizationError> {
        self.policies.get(scope).ok_or(AuthorizationError::PolicyMissing)
    }
}

/// An owner-only append journal of scoped authorization policies.
pub struct AuthorizationPolicyStore {
    path: PathBuf,
    file: File,
    authentication_key: [u8; AUTHENTICATION_BYTES],
    snapshot: PolicyStoreSnapshot,
    previous_tag: [u8; AUTHENTICATION_BYTES],
    record_count: u64,
    lock_path: PathBuf,
    head_path: PathBuf,
    writer_lease: Arc<File>,
    expiries: BTreeMap<ResourceScope, u64>,
    generations: BTreeMap<ResourceScope, u64>,
    revoked_principals: BTreeMap<(ResourceScope, PrincipalUlid), u64>,
    revoked_grants: BTreeMap<(ResourceScope, PrincipalGrantUlid), u64>,
}

struct RecoveredState {
    snapshot: PolicyStoreSnapshot,
    previous_tag: [u8; AUTHENTICATION_BYTES],
    record_count: u64,
    truncated_tail: bool,
    expiries: BTreeMap<ResourceScope, u64>,
    generations: BTreeMap<ResourceScope, u64>,
    revoked_principals: BTreeMap<(ResourceScope, PrincipalUlid), u64>,
    revoked_grants: BTreeMap<(ResourceScope, PrincipalGrantUlid), u64>,
}

impl std::fmt::Debug for AuthorizationPolicyStore {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthorizationPolicyStore")
            .field("path", &self.path)
            .field("snapshot", &self.snapshot)
            .finish_non_exhaustive()
    }
}

impl Drop for AuthorizationPolicyStore {
    fn drop(&mut self) {
        self.authentication_key.zeroize();
        if Arc::strong_count(&self.writer_lease) == 1 {
            let _ = std::fs::remove_file(&self.lock_path);
        }
    }
}

impl AuthorizationPolicyStore {
    /// Opens or creates an owner-only policy journal and authenticates every
    /// existing record before returning it.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::PolicyStoreUnsafe`] for a symlink,
    /// non-regular file, or group/world-accessible journal. I/O failures return
    /// [`AuthorizationError::PolicyStoreUnavailable`]. A malformed, truncated,
    /// or unauthenticated complete journal returns
    /// [`AuthorizationError::PolicyJournalCorrupt`].
    pub fn open(
        path: impl AsRef<Path>,
        authentication_key: [u8; AUTHENTICATION_BYTES],
    ) -> Result<Self, AuthorizationError> {
        Self::open_with_key(path, PolicyStoreKey::from_bytes(authentication_key))
    }

    /// Opens the journal using a move-only key-provider result.
    ///
    /// # Errors
    ///
    /// Returns the same durable-store refusals as [`Self::open`].
    #[allow(
        clippy::needless_pass_by_value,
        reason = "the key handle is intentionally consumed at the trust boundary"
    )]
    pub fn open_with_key(
        path: impl AsRef<Path>,
        authentication_key: PolicyStoreKey,
    ) -> Result<Self, AuthorizationError> {
        let authentication_key: [u8; AUTHENTICATION_BYTES] = authentication_key
            .into_bytes()
            .try_into()
            .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
        let path = path.as_ref().to_path_buf();
        reject_symlink(&path)?;
        if let Some(parent) = path.parent().filter(|parent| !parent.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent)
                .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
        }
        let (mut file, created) = open_journal(&path)?;
        validate_open_file(&file)?;
        let lock_path = lock_path_for(&path)?;
        let writer_lease = acquire_writer_lease(&lock_path)?;

        if created {
            file.write_all(JOURNAL_MAGIC)
                .and_then(|()| file.sync_all())
                .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
            sync_parent(&path)?;
        }

        let recovered = replay(&mut file, &authentication_key)?;
        let RecoveredState {
            snapshot,
            previous_tag,
            record_count,
            truncated_tail,
            expiries,
            generations,
            revoked_principals,
            revoked_grants,
        } = recovered;
        let head_path = head_path_for(&path)?;
        reconcile_head(
            &head_path,
            &authentication_key,
            record_count,
            &previous_tag,
            truncated_tail,
        )?;
        file.seek(SeekFrom::End(0)).map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
        Ok(Self {
            path,
            file,
            authentication_key,
            snapshot,
            previous_tag,
            record_count,
            lock_path,
            head_path,
            writer_lease: Arc::new(writer_lease),
            expiries,
            generations,
            revoked_principals,
            revoked_grants,
        })
    }

    /// Appends a newer policy for the exact database scope and syncs it before
    /// changing the visible snapshot. Re-publishing identical contents at the
    /// same generation is idempotent.
    ///
    /// # Errors
    ///
    /// Returns a typed rollback or equivocation refusal for a non-monotonic
    /// publication, or [`AuthorizationError::PolicyStoreUnavailable`] when the
    /// durable append cannot complete.
    pub fn publish(
        &mut self,
        scope: ResourceScope,
        policy: Policy,
    ) -> Result<(), AuthorizationError> {
        self.publish_with_expiry(scope, policy, u64::MAX)
    }

    /// Appends a policy whose validity ends at the supplied Unix-second
    /// instant. A zero expiry is refused.
    ///
    /// # Errors
    ///
    /// Returns a typed generation, bound, or durable-I/O refusal.
    pub fn publish_with_expiry(
        &mut self,
        scope: ResourceScope,
        policy: Policy,
        expires_at: u64,
    ) -> Result<(), AuthorizationError> {
        if expires_at == 0 {
            return Err(AuthorizationError::InvalidExpiry);
        }
        let current_generation = self.generations.get(&scope).copied().unwrap_or(0);
        if policy.generation() < current_generation {
            return Err(AuthorizationError::PolicyGenerationRollback);
        }
        if let Some(current) = self.snapshot.policies.get(&scope) {
            match policy.generation().cmp(&current.generation()) {
                std::cmp::Ordering::Less => {
                    return Err(AuthorizationError::PolicyGenerationRollback);
                }
                std::cmp::Ordering::Equal
                    if &policy == current
                        && self.expiries.get(&scope).copied().unwrap_or(u64::MAX) == expires_at =>
                {
                    return Ok(());
                }
                std::cmp::Ordering::Equal => {
                    return Err(AuthorizationError::PolicyGenerationEquivocation);
                }
                std::cmp::Ordering::Greater => {}
            }
        }

        let generation = policy.generation();
        let payload = encode_publish_record(&scope, &policy, expires_at)?;
        let payload_len =
            u32::try_from(payload.len()).map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
        let tag = authenticate(&self.authentication_key, &self.previous_tag, &payload);
        append_frame(&mut self.file, payload_len, &payload, &tag)?;
        self.snapshot.policies.insert(scope.clone(), policy);
        self.expiries.insert(scope.clone(), expires_at);
        self.generations.insert(scope, generation);
        self.previous_tag.copy_from_slice(tag.as_bytes());
        self.record_count =
            self.record_count.checked_add(1).ok_or(AuthorizationError::PolicyStoreUnavailable)?;
        reconcile_head(
            &self.head_path,
            &self.authentication_key,
            self.record_count,
            &self.previous_tag,
            false,
        )?;
        Ok(())
    }

    /// Advances the scope generation and revokes all contexts for a principal
    /// at or below that generation.
    ///
    /// # Errors
    ///
    /// Returns a rollback refusal or durable-I/O error.
    pub fn revoke_principal(
        &mut self,
        scope: ResourceScope,
        principal_id: PrincipalUlid,
        generation: u64,
    ) -> Result<(), AuthorizationError> {
        self.append_revocation(Revocation::Principal { scope, principal_id, generation })
    }

    /// Advances the scope generation and revokes one signed grant at or below
    /// that generation.
    ///
    /// # Errors
    ///
    /// Returns a rollback refusal or durable-I/O error.
    pub fn revoke_grant(
        &mut self,
        scope: ResourceScope,
        grant_id: PrincipalGrantUlid,
        generation: u64,
    ) -> Result<(), AuthorizationError> {
        self.append_revocation(Revocation::Grant { scope, grant_id, generation })
    }

    /// Evaluates against the current durable publication and refuses missing,
    /// expired, stale, cross-scope, or revoked contexts before the policy AST.
    #[must_use]
    pub fn evaluate(
        &self,
        context: &super::PrincipalContext,
        request: &super::AuthorizationRequest,
    ) -> super::AuthorizationDecision {
        let Some(policy) = self.snapshot.policies.get(&request.scope) else {
            return super::AuthorizationDecision::Deny(super::DenialReason::ScopeMismatch);
        };
        let generation = self.generations.get(&request.scope).copied().unwrap_or(0);
        let revoked_principal = self
            .revoked_principals
            .get(&(request.scope.clone(), context.principal_id()))
            .is_some_and(|value| *value >= context.policy_generation());
        let revoked_grant = self
            .revoked_grants
            .get(&(request.scope.clone(), context.grant_id()))
            .is_some_and(|value| *value >= context.policy_generation());
        if request.scope != context.scope
            || generation != context.policy_generation()
            || policy.policy_id() != context.policy_id()
            || !policy.contains_grant(context.grant_id())
            || revoked_principal
            || revoked_grant
            || self.expiries.get(&request.scope).copied().unwrap_or(0) <= request.now
        {
            return super::AuthorizationDecision::Deny(
                super::DenialReason::PolicyGenerationMismatch,
            );
        }
        super::evaluate(context, policy, request)
    }

    fn append_revocation(&mut self, revocation: Revocation) -> Result<(), AuthorizationError> {
        let (scope, generation) = match &revocation {
            Revocation::Principal { scope, generation, .. }
            | Revocation::Grant { scope, generation, .. } => (scope, *generation),
        };
        if generation == 0 || generation <= self.generations.get(scope).copied().unwrap_or(0) {
            return Err(AuthorizationError::PolicyGenerationRollback);
        }
        let payload = encode_revocation_record(&revocation)?;
        let tag = authenticate(&self.authentication_key, &self.previous_tag, &payload);
        let payload_len =
            u32::try_from(payload.len()).map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
        append_frame(&mut self.file, payload_len, &payload, &tag)?;
        apply_revocation(
            &mut self.generations,
            &mut self.revoked_principals,
            &mut self.revoked_grants,
            revocation,
        );
        self.previous_tag.copy_from_slice(tag.as_bytes());
        self.record_count =
            self.record_count.checked_add(1).ok_or(AuthorizationError::PolicyStoreUnavailable)?;
        reconcile_head(
            &self.head_path,
            &self.authentication_key,
            self.record_count,
            &self.previous_tag,
            false,
        )?;
        Ok(())
    }

    /// Returns the latest policy for an exact scope.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::PolicyMissing`] when no policy is
    /// published for the scope.
    pub fn policy(&self, scope: &ResourceScope) -> Result<&Policy, AuthorizationError> {
        self.snapshot.policy(scope)
    }

    /// Returns the durable authorization revision for an exact scope. This
    /// advances on policy publication and revocation and is suitable for
    /// invalidating query/view authorization caches.
    pub fn revision(&self, scope: &ResourceScope) -> Result<u64, AuthorizationError> {
        self.generations.get(scope).copied().ok_or(AuthorizationError::PolicyMissing)
    }

    /// Returns the generation at which this scope last invalidated grants.
    /// A missing scope is fail-closed rather than interpreted as generation 0.
    pub fn revocation_generation(&self, scope: &ResourceScope) -> Result<u64, AuthorizationError> {
        self.revision(scope)
    }

    /// Returns an immutable clone of all current scoped publications.
    #[must_use]
    pub fn snapshot(&self) -> PolicyStoreSnapshot {
        self.snapshot.clone()
    }

    /// Returns the journal path without exposing its authentication key.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }
}

fn reject_symlink(path: &Path) -> Result<(), AuthorizationError> {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_symlink() => {
            Err(AuthorizationError::PolicyStoreUnsafe)
        }
        Ok(_) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(_) => Err(AuthorizationError::PolicyStoreUnavailable),
    }
}

fn open_journal(path: &Path) -> Result<(File, bool), AuthorizationError> {
    let mut existing = OpenOptions::new();
    existing.read(true).append(true);
    #[cfg(unix)]
    existing.custom_flags(0o400_000);
    match existing.open(path) {
        Ok(file) => return Ok((file, false)),
        Err(error) if error.kind() != std::io::ErrorKind::NotFound => {
            return Err(if error.raw_os_error() == Some(40) {
                AuthorizationError::PolicyStoreUnsafe
            } else {
                AuthorizationError::PolicyStoreUnavailable
            });
        }
        Err(_) => {}
    }

    let mut create = OpenOptions::new();
    create.read(true).append(true).create_new(true);
    #[cfg(unix)]
    create.mode(0o600).custom_flags(0o400_000);
    match create.open(path) {
        Ok(file) => Ok((file, true)),
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
            reject_symlink(path)?;
            existing
                .open(path)
                .map(|file| (file, false))
                .map_err(|_| AuthorizationError::PolicyStoreUnavailable)
        }
        Err(_) => Err(AuthorizationError::PolicyStoreUnavailable),
    }
}

fn validate_open_file(file: &File) -> Result<(), AuthorizationError> {
    let metadata = file.metadata().map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    if !metadata.is_file() {
        return Err(AuthorizationError::PolicyStoreUnsafe);
    }
    #[cfg(unix)]
    if metadata.permissions().mode() & 0o077 != 0 {
        return Err(AuthorizationError::PolicyStoreUnsafe);
    }
    Ok(())
}

fn sync_parent(path: &Path) -> Result<(), AuthorizationError> {
    let parent =
        path.parent().filter(|parent| !parent.as_os_str().is_empty()).unwrap_or(Path::new("."));
    File::open(parent)
        .and_then(|directory| directory.sync_all())
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)
}

fn acquire_writer_lease(path: &Path) -> Result<File, AuthorizationError> {
    reject_symlink(path)?;
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true);
    #[cfg(unix)]
    options.mode(0o600).custom_flags(0o00_400_000);
    let file = options.open(path).map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    validate_open_file(&file)?;
    file.try_lock().map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    if let Some(parent) = path.parent().filter(|parent| !parent.as_os_str().is_empty()) {
        File::open(parent)
            .and_then(|directory| directory.sync_all())
            .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    }
    Ok(file)
}

fn lock_path_for(path: &Path) -> Result<PathBuf, AuthorizationError> {
    let name = path.file_name().ok_or(AuthorizationError::PolicyStoreUnsafe)?;
    let mut lock_name = name.to_os_string();
    lock_name.push(".lock");
    Ok(path.with_file_name(lock_name))
}

fn head_path_for(path: &Path) -> Result<PathBuf, AuthorizationError> {
    let name = path.file_name().ok_or(AuthorizationError::PolicyStoreUnsafe)?;
    let mut head_name = name.to_os_string();
    head_name.push(".head");
    Ok(path.with_file_name(head_name))
}

fn head_tag(
    key: &[u8; AUTHENTICATION_BYTES],
    count: u64,
    tag: &[u8; AUTHENTICATION_BYTES],
) -> blake3::Hash {
    let mut hasher = blake3::Hasher::new_keyed(key);
    hasher.update(HEAD_DOMAIN);
    hasher.update(&count.to_le_bytes());
    hasher.update(tag);
    hasher.finalize()
}

fn reconcile_head(
    path: &Path,
    key: &[u8; AUTHENTICATION_BYTES],
    count: u64,
    tag: &[u8; AUTHENTICATION_BYTES],
    truncated_tail: bool,
) -> Result<(), AuthorizationError> {
    let mut expected = Vec::with_capacity(80);
    expected.extend_from_slice(HEAD_MAGIC);
    expected.extend_from_slice(&count.to_le_bytes());
    expected.extend_from_slice(tag);
    expected.extend_from_slice(head_tag(key, count, tag).as_bytes());
    reject_symlink(path)?;
    let mut read_options = OpenOptions::new();
    read_options.read(true);
    #[cfg(unix)]
    read_options.custom_flags(0o00_400_000);
    match read_options.open(path) {
        Ok(mut file) => {
            validate_open_file(&file)?;
            let length =
                file.metadata().map_err(|_| AuthorizationError::PolicyStoreUnavailable)?.len();
            if length != expected.len() as u64 {
                return Err(AuthorizationError::PolicyJournalCorrupt);
            }
            let mut actual = Vec::new();
            file.read_to_end(&mut actual)
                .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
            if actual.len() != expected.len() || actual[..8] != *HEAD_MAGIC {
                return Err(AuthorizationError::PolicyJournalCorrupt);
            }
            let head_count = u64::from_le_bytes(actual[8..16].try_into().unwrap());
            let head_record_tag: [u8; 32] = actual[16..48].try_into().unwrap();
            let head_mac: [u8; 32] = actual[48..80].try_into().unwrap();
            if head_tag(key, head_count, &head_record_tag).as_bytes() != head_mac.as_slice()
                || (head_count > count && !truncated_tail)
                || (head_count == count && head_record_tag != *tag)
            {
                return Err(AuthorizationError::PolicyJournalCorrupt);
            }
            if head_count == count {
                return Ok(());
            }
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(_) => return Err(AuthorizationError::PolicyStoreUnavailable),
    }
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true).truncate(true);
    #[cfg(unix)]
    options.mode(0o600).custom_flags(0o00_400_000);
    let mut file = options.open(path).map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    file.write_all(&expected)
        .and_then(|()| file.sync_all())
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    if let Some(parent) = path.parent().filter(|parent| !parent.as_os_str().is_empty()) {
        File::open(parent)
            .and_then(|directory| directory.sync_all())
            .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    }
    Ok(())
}

enum Revocation {
    Principal { scope: ResourceScope, principal_id: PrincipalUlid, generation: u64 },
    Grant { scope: ResourceScope, grant_id: PrincipalGrantUlid, generation: u64 },
}

fn apply_revocation(
    generations: &mut BTreeMap<ResourceScope, u64>,
    principals: &mut BTreeMap<(ResourceScope, PrincipalUlid), u64>,
    grants: &mut BTreeMap<(ResourceScope, PrincipalGrantUlid), u64>,
    revocation: Revocation,
) {
    match revocation {
        Revocation::Principal { scope, principal_id, generation } => {
            generations.insert(scope.clone(), generation);
            principals.insert((scope, principal_id), generation);
        }
        Revocation::Grant { scope, grant_id, generation } => {
            generations.insert(scope.clone(), generation);
            grants.insert((scope, grant_id), generation);
        }
    }
}

fn replay(
    file: &mut File,
    authentication_key: &[u8; AUTHENTICATION_BYTES],
) -> Result<RecoveredState, AuthorizationError> {
    let length = file.metadata().map_err(|_| AuthorizationError::PolicyStoreUnavailable)?.len();
    if length > MAX_JOURNAL_BYTES {
        return Err(AuthorizationError::PolicyJournalCorrupt);
    }
    let length = usize::try_from(length).map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
    file.seek(SeekFrom::Start(0)).map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    let mut bytes = Vec::with_capacity(length);
    file.take(MAX_JOURNAL_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    if bytes.len() != length || !bytes.starts_with(JOURNAL_MAGIC) {
        return Err(AuthorizationError::PolicyJournalCorrupt);
    }

    let mut offset = JOURNAL_MAGIC.len();
    let mut previous_tag = [0_u8; AUTHENTICATION_BYTES];
    let mut record_count = 0_u64;
    let mut truncated_tail = false;
    let mut policies = BTreeMap::new();
    let mut expiries = BTreeMap::new();
    let mut generations = BTreeMap::new();
    let mut revoked_principals = BTreeMap::new();
    let mut revoked_grants = BTreeMap::new();
    while offset < bytes.len() {
        let record_start = offset;
        let length_end = offset.checked_add(4).ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        if length_end > bytes.len() {
            truncate_tail(file, record_start as u64)?;
            truncated_tail = true;
            break;
        }
        let record_len = bytes
            .get(offset..length_end)
            .and_then(|value| value.try_into().ok())
            .map(u32::from_le_bytes)
            .and_then(|value| usize::try_from(value).ok())
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        if record_len == 0 || record_len > MAX_RECORD_BYTES {
            return Err(AuthorizationError::PolicyJournalCorrupt);
        }
        let payload_end =
            length_end.checked_add(record_len).ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        let tag_end = payload_end
            .checked_add(AUTHENTICATION_BYTES)
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        let Some(payload) = bytes.get(length_end..payload_end) else {
            truncate_tail(file, record_start as u64)?;
            truncated_tail = true;
            break;
        };
        let Some(tag) = bytes.get(payload_end..tag_end) else {
            truncate_tail(file, record_start as u64)?;
            truncated_tail = true;
            break;
        };
        if authenticate(authentication_key, &previous_tag, payload).as_bytes() != tag {
            return Err(AuthorizationError::PolicyJournalCorrupt);
        }
        match decode_journal_record(payload)? {
            JournalRecord::Publish { scope, policy, expires_at } => {
                if policy.generation() < generations.get(&scope).copied().unwrap_or(0) {
                    return Err(AuthorizationError::PolicyGenerationRollback);
                }
                apply_replayed(&mut policies, scope.clone(), policy.clone())?;
                generations.insert(scope.clone(), policy.generation());
                expiries.insert(scope, expires_at);
            }
            JournalRecord::Revoke(revocation) => {
                let generation = match &revocation {
                    Revocation::Principal { generation, .. }
                    | Revocation::Grant { generation, .. } => *generation,
                };
                let scope = match &revocation {
                    Revocation::Principal { scope, .. } | Revocation::Grant { scope, .. } => scope,
                };
                if generation <= generations.get(scope).copied().unwrap_or(0) {
                    return Err(AuthorizationError::PolicyGenerationRollback);
                }
                apply_revocation(
                    &mut generations,
                    &mut revoked_principals,
                    &mut revoked_grants,
                    revocation,
                );
            }
        }
        previous_tag.copy_from_slice(tag);
        record_count =
            record_count.checked_add(1).ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        offset = tag_end;
    }
    Ok(RecoveredState {
        snapshot: PolicyStoreSnapshot { policies },
        previous_tag,
        record_count,
        truncated_tail,
        expiries,
        generations,
        revoked_principals,
        revoked_grants,
    })
}

fn truncate_tail(file: &mut File, length: u64) -> Result<(), AuthorizationError> {
    file.set_len(length)
        .and_then(|()| file.sync_all())
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)
}

fn append_frame(
    file: &mut File,
    payload_len: u32,
    payload: &[u8],
    tag: &blake3::Hash,
) -> Result<(), AuthorizationError> {
    let current = file.metadata().map_err(|_| AuthorizationError::PolicyStoreUnavailable)?.len();
    let frame_len = 4_u64
        .checked_add(u64::from(payload_len))
        .and_then(|length| length.checked_add(AUTHENTICATION_BYTES as u64))
        .ok_or(AuthorizationError::ExceedsBound("policy journal"))?;
    if current.checked_add(frame_len).is_none_or(|length| length > MAX_JOURNAL_BYTES) {
        return Err(AuthorizationError::ExceedsBound("policy journal"));
    }
    file.write_all(&payload_len.to_le_bytes())
        .and_then(|()| file.write_all(payload))
        .and_then(|()| file.write_all(tag.as_bytes()))
        .and_then(|()| file.sync_data())
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)
}

fn apply_replayed(
    policies: &mut BTreeMap<ResourceScope, Policy>,
    scope: ResourceScope,
    policy: Policy,
) -> Result<(), AuthorizationError> {
    if let Some(current) = policies.get(&scope) {
        match policy.generation().cmp(&current.generation()) {
            std::cmp::Ordering::Less => {
                return Err(AuthorizationError::PolicyGenerationRollback);
            }
            std::cmp::Ordering::Equal if &policy == current => return Ok(()),
            std::cmp::Ordering::Equal => {
                return Err(AuthorizationError::PolicyGenerationEquivocation);
            }
            std::cmp::Ordering::Greater => {}
        }
    }
    policies.insert(scope, policy);
    Ok(())
}

fn authenticate(
    key: &[u8; AUTHENTICATION_BYTES],
    previous_tag: &[u8; AUTHENTICATION_BYTES],
    payload: &[u8],
) -> blake3::Hash {
    let mut hasher = blake3::Hasher::new_keyed(key);
    hasher.update(RECORD_DOMAIN);
    hasher.update(previous_tag);
    hasher.update(payload);
    hasher.finalize()
}

enum JournalRecord {
    Publish { scope: ResourceScope, policy: Policy, expires_at: u64 },
    Revoke(Revocation),
}

fn encode_publish_record(
    scope: &ResourceScope,
    policy: &Policy,
    expires_at: u64,
) -> Result<Vec<u8>, AuthorizationError> {
    let legacy = encode_record(scope, policy)?;
    let mut output = Vec::with_capacity(legacy.len() + 10);
    output.push(RECORD_VERSION);
    output.push(1);
    output.extend_from_slice(&expires_at.to_le_bytes());
    output.extend_from_slice(&legacy[1..]);
    if output.len() > MAX_RECORD_BYTES {
        return Err(AuthorizationError::PolicyStoreUnavailable);
    }
    Ok(output)
}

fn encode_revocation_record(revocation: &Revocation) -> Result<Vec<u8>, AuthorizationError> {
    let mut output = vec![RECORD_VERSION];
    match revocation {
        Revocation::Principal { scope, principal_id, generation } => {
            output.push(2);
            encode_scope(&mut output, scope);
            output.extend_from_slice(&principal_id.as_bytes());
            output.extend_from_slice(&generation.to_le_bytes());
        }
        Revocation::Grant { scope, grant_id, generation } => {
            output.push(3);
            encode_scope(&mut output, scope);
            output.extend_from_slice(&grant_id.as_bytes());
            output.extend_from_slice(&generation.to_le_bytes());
        }
    }
    if output.len() > MAX_RECORD_BYTES {
        return Err(AuthorizationError::PolicyStoreUnavailable);
    }
    Ok(output)
}

fn encode_scope(output: &mut Vec<u8>, scope: &ResourceScope) {
    output.extend_from_slice(&scope.tenant_id().as_bytes());
    output.extend_from_slice(&scope.project_id().as_bytes());
    output.extend_from_slice(&scope.database_id().as_bytes());
}

fn decode_journal_record(bytes: &[u8]) -> Result<JournalRecord, AuthorizationError> {
    let mut decoder = Decoder::new(bytes);
    if decoder.byte()? != RECORD_VERSION {
        return Err(AuthorizationError::PolicyJournalCorrupt);
    }
    match decoder.byte()? {
        1 => {
            let expires_at = decoder.u64()?;
            let mut legacy = Vec::with_capacity(bytes.len() - 9);
            legacy.push(RECORD_VERSION);
            legacy.extend_from_slice(decoder.take(bytes.len() - decoder.offset)?);
            let (scope, policy) = decode_record(&legacy)?;
            if expires_at == 0 {
                return Err(AuthorizationError::PolicyJournalCorrupt);
            }
            Ok(JournalRecord::Publish { scope, policy, expires_at })
        }
        2 | 3 => {
            let scope = ResourceScope::try_new(
                TenantUlid::from_wire_bytes(decoder.array()?),
                ProjectUlid::from_wire_bytes(decoder.array()?),
                DatabaseUlid::from_wire_bytes(decoder.array()?),
            )
            .map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
            let id = decoder.array()?;
            let generation = decoder.u64()?;
            if generation == 0 || !decoder.is_finished() {
                return Err(AuthorizationError::PolicyJournalCorrupt);
            }
            let revocation = if bytes[1] == 2 {
                Revocation::Principal {
                    scope,
                    principal_id: PrincipalUlid::from_wire_bytes(id),
                    generation,
                }
            } else {
                Revocation::Grant {
                    scope,
                    grant_id: PrincipalGrantUlid::from_wire_bytes(id),
                    generation,
                }
            };
            Ok(JournalRecord::Revoke(revocation))
        }
        _ => Err(AuthorizationError::PolicyJournalCorrupt),
    }
}

fn encode_record(scope: &ResourceScope, policy: &Policy) -> Result<Vec<u8>, AuthorizationError> {
    let mut output = Vec::new();
    output.push(RECORD_VERSION);
    output.extend_from_slice(&scope.tenant_id().as_bytes());
    output.extend_from_slice(&scope.project_id().as_bytes());
    output.extend_from_slice(&scope.database_id().as_bytes());
    output.extend_from_slice(&policy.policy_id.as_bytes());
    output.extend_from_slice(&policy.version.to_le_bytes());
    output.extend_from_slice(&policy.generation.to_le_bytes());
    put_count(&mut output, policy.indexed_fields.len())?;
    for field in &policy.indexed_fields {
        put_string(&mut output, field)?;
    }
    put_count(&mut output, policy.rules.len())?;
    for rule in &policy.rules {
        output.extend_from_slice(&rule.grant_id.as_bytes());
        output.push(operation_tag(rule.operation));
        put_string(&mut output, &rule.collection)?;
        put_count(&mut output, rule.claim_predicates.len())?;
        for predicate in &rule.claim_predicates {
            put_string(&mut output, &predicate.field)?;
            put_string(&mut output, &predicate.claim)?;
            output.push(comparison_tag(predicate.comparison));
        }
        // 0/1 retain the v1 relationship marker for journals written before
        // CEL row rules existed; 2 introduces a persisted compiled rule.
        if let Some(access_rule) = &rule.access_rule {
            output.push(2);
            put_string(&mut output, access_rule.expression())?;
        }
        match &rule.relationship {
            None => output.push(0),
            Some(relationship) => {
                output.push(1);
                put_string(&mut output, &relationship.relation)?;
                put_string(&mut output, &relationship.target_collection)?;
                output.push(relationship.max_hops);
                output.extend_from_slice(&relationship.max_matches.to_le_bytes());
            }
        }
        match &rule.projection.allow {
            None => output.extend_from_slice(&u16::MAX.to_le_bytes()),
            Some(allow) => {
                put_count(&mut output, allow.len())?;
                for field in allow {
                    put_string(&mut output, field)?;
                }
            }
        }
        put_count(&mut output, rule.projection.deny.len())?;
        for field in &rule.projection.deny {
            put_string(&mut output, field)?;
        }
    }
    if output.len() > MAX_RECORD_BYTES {
        return Err(AuthorizationError::PolicyStoreUnavailable);
    }
    Ok(output)
}

fn decode_record(bytes: &[u8]) -> Result<(ResourceScope, Policy), AuthorizationError> {
    let mut decoder = Decoder::new(bytes);
    if decoder.byte()? != RECORD_VERSION {
        return Err(AuthorizationError::PolicyJournalCorrupt);
    }
    let scope = ResourceScope::try_new(
        TenantUlid::from_wire_bytes(decoder.array()?),
        ProjectUlid::from_wire_bytes(decoder.array()?),
        DatabaseUlid::from_wire_bytes(decoder.array()?),
    )
    .map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
    let policy_id = AuthorizationPolicyUlid::from_wire_bytes(decoder.array()?);
    if decoder.u16()? != AUTHORIZATION_FORMAT_VERSION {
        return Err(AuthorizationError::PolicyJournalCorrupt);
    }
    let generation = decoder.u64()?;
    let indexed_count = usize::from(decoder.u16()?);
    let mut indexed_fields = BTreeSet::new();
    for _ in 0..indexed_count {
        if !indexed_fields.insert(decoder.string()?) {
            return Err(AuthorizationError::PolicyJournalCorrupt);
        }
    }
    let rule_count = usize::from(decoder.u16()?);
    let mut rules = Vec::with_capacity(rule_count);
    for _ in 0..rule_count {
        let grant_id = PrincipalGrantUlid::from_wire_bytes(decoder.array()?);
        let operation = decode_operation(decoder.byte()?)?;
        let collection = decoder.string()?;
        let predicate_count = usize::from(decoder.u16()?);
        let mut rule = PolicyRule::try_new_with_id(grant_id, operation, &collection)
            .map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
        for _ in 0..predicate_count {
            let field = decoder.string()?;
            let claim = decoder.string()?;
            let comparison = decode_comparison(decoder.byte()?)?;
            let predicate = ClaimPredicate::try_new(&field, &claim, comparison)
                .map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
            rule = rule.with_claim_predicate(predicate);
        }
        let relationship_tag = decoder.byte()?;
        let mut rule = if relationship_tag == 2 {
            let expression = decoder.string()?;
            let access_rule = super::CompiledAccessRule::try_compile(&expression, &indexed_fields)
                .map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
            rule.with_access_rule(access_rule)
        } else {
            rule
        };
        match if relationship_tag == 2 { decoder.byte()? } else { relationship_tag } {
            0 => {}
            1 => {
                let relation = decoder.string()?;
                let collection = decoder.string()?;
                let relationship = RelationshipPredicate::try_new(
                    &relation,
                    &collection,
                    decoder.byte()?,
                    decoder.u32()?,
                )
                .map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
                rule = rule.with_relationship(relationship);
            }
            _ => return Err(AuthorizationError::PolicyJournalCorrupt),
        }
        let allow_count = decoder.u16()?;
        let mut allow = None;
        if allow_count != u16::MAX {
            let mut fields = BTreeSet::new();
            for _ in 0..allow_count {
                if !fields.insert(decoder.string()?) {
                    return Err(AuthorizationError::PolicyJournalCorrupt);
                }
            }
            allow = Some(fields);
        }
        let deny_count = usize::from(decoder.u16()?);
        let mut deny = BTreeSet::new();
        for _ in 0..deny_count {
            if !deny.insert(decoder.string()?) {
                return Err(AuthorizationError::PolicyJournalCorrupt);
            }
        }
        rule = rule.with_projection(FieldProjection { allow, deny });
        rules.push(rule);
    }
    if !decoder.is_finished() {
        return Err(AuthorizationError::PolicyJournalCorrupt);
    }
    let policy = Policy::try_new_with_id(policy_id, generation, indexed_fields, rules)
        .map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
    Ok((scope, policy))
}

fn put_count(output: &mut Vec<u8>, count: usize) -> Result<(), AuthorizationError> {
    let count = u16::try_from(count).map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    output.extend_from_slice(&count.to_le_bytes());
    Ok(())
}

fn put_string(output: &mut Vec<u8>, value: &str) -> Result<(), AuthorizationError> {
    put_count(output, value.len())?;
    output.extend_from_slice(value.as_bytes());
    Ok(())
}

const fn operation_tag(operation: Operation) -> u8 {
    match operation {
        Operation::Read => 0,
        Operation::Query => 1,
        Operation::Mutate => 2,
        Operation::Subscribe => 3,
        Operation::Schema => 4,
        Operation::Admin => 5,
    }
}

fn decode_operation(tag: u8) -> Result<Operation, AuthorizationError> {
    match tag {
        0 => Ok(Operation::Read),
        1 => Ok(Operation::Query),
        2 => Ok(Operation::Mutate),
        3 => Ok(Operation::Subscribe),
        4 => Ok(Operation::Schema),
        5 => Ok(Operation::Admin),
        _ => Err(AuthorizationError::PolicyJournalCorrupt),
    }
}

const fn comparison_tag(comparison: ClaimComparison) -> u8 {
    match comparison {
        ClaimComparison::Equals => 0,
        ClaimComparison::NotEquals => 1,
        ClaimComparison::LessThan => 2,
        ClaimComparison::LessOrEqual => 3,
        ClaimComparison::GreaterThan => 4,
        ClaimComparison::GreaterOrEqual => 5,
    }
}

fn decode_comparison(tag: u8) -> Result<ClaimComparison, AuthorizationError> {
    match tag {
        0 => Ok(ClaimComparison::Equals),
        1 => Ok(ClaimComparison::NotEquals),
        2 => Ok(ClaimComparison::LessThan),
        3 => Ok(ClaimComparison::LessOrEqual),
        4 => Ok(ClaimComparison::GreaterThan),
        5 => Ok(ClaimComparison::GreaterOrEqual),
        _ => Err(AuthorizationError::PolicyJournalCorrupt),
    }
}

struct Decoder<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> Decoder<'a> {
    const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }

    fn take(&mut self, length: usize) -> Result<&'a [u8], AuthorizationError> {
        let end =
            self.offset.checked_add(length).ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        let value =
            self.bytes.get(self.offset..end).ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        self.offset = end;
        Ok(value)
    }

    fn byte(&mut self) -> Result<u8, AuthorizationError> {
        Ok(self.take(1)?[0])
    }

    fn array<const N: usize>(&mut self) -> Result<[u8; N], AuthorizationError> {
        self.take(N)?.try_into().map_err(|_| AuthorizationError::PolicyJournalCorrupt)
    }

    fn u16(&mut self) -> Result<u16, AuthorizationError> {
        Ok(u16::from_le_bytes(self.array()?))
    }

    fn u32(&mut self) -> Result<u32, AuthorizationError> {
        Ok(u32::from_le_bytes(self.array()?))
    }

    fn u64(&mut self) -> Result<u64, AuthorizationError> {
        Ok(u64::from_le_bytes(self.array()?))
    }

    fn string(&mut self) -> Result<String, AuthorizationError> {
        let length = usize::from(self.u16()?);
        std::str::from_utf8(self.take(length)?)
            .map(str::to_owned)
            .map_err(|_| AuthorizationError::PolicyJournalCorrupt)
    }

    const fn is_finished(&self) -> bool {
        self.offset == self.bytes.len()
    }
}

#[cfg(test)]
mod tests {
    use std::{
        fs::OpenOptions,
        io::{Seek, SeekFrom, Write},
        sync::atomic::{AtomicU64, Ordering},
    };

    use super::*;

    static PATH_COUNTER: AtomicU64 = AtomicU64::new(0);

    fn test_path(name: &str) -> PathBuf {
        let counter = PATH_COUNTER.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir()
            .join(format!("terrabase-policy-store-{name}-{}-{counter}.journal", std::process::id()))
    }

    fn scope() -> ResourceScope {
        ResourceScope::try_new(
            TenantUlid::from_wire_bytes([1; 16]),
            ProjectUlid::from_wire_bytes([2; 16]),
            DatabaseUlid::from_wire_bytes([3; 16]),
        )
        .expect("scope")
    }

    fn policy(generation: u64) -> Policy {
        let predicate =
            ClaimPredicate::try_new("owner", "tenant", ClaimComparison::Equals).expect("predicate");
        let relationship =
            RelationshipPredicate::try_new("member", "users", 2, 8).expect("relationship");
        let projection = FieldProjection::try_allow(["owner", "name"])
            .expect("allow")
            .try_deny(["owner"])
            .expect("deny");
        let rule = PolicyRule::try_new_with_id(
            PrincipalGrantUlid::from_wire_bytes([4; 16]),
            Operation::Read,
            "documents",
        )
        .expect("rule")
        .with_claim_predicate(predicate)
        .with_relationship(relationship)
        .with_projection(projection);
        Policy::try_new_with_id(
            AuthorizationPolicyUlid::from_wire_bytes([5; 16]),
            generation,
            ["owner"],
            [rule],
        )
        .expect("policy")
    }

    fn context(generation: u64) -> crate::authorization::PrincipalContext {
        crate::authorization::PrincipalContext::from_verified(
            crate::authorization::VerifiedPrincipalInput {
                scope: scope(),
                environment: "production".to_owned(),
                gateway_spiffe_principal: "spiffe://terrascale/gateway".to_owned(),
                issuer: "terrascale-go".to_owned(),
                principal_id: PrincipalUlid::from_wire_bytes([6; 16]),
                session_id: crate::identity::PrincipalSessionUlid::from_wire_bytes([7; 16]),
                grant_id: PrincipalGrantUlid::from_wire_bytes([4; 16]),
                policy_id: AuthorizationPolicyUlid::from_wire_bytes([5; 16]),
                actions: [super::super::Action::Read].into_iter().collect(),
                claims: super::super::PrincipalClaims::try_new([(
                    "tenant",
                    super::super::AttributeValue::String("tenant".to_owned()),
                )])
                .expect("claims"),
                audience: "terrascale.database".to_owned(),
                issued_at: 1,
                expires_at: 100,
                policy_generation: generation,
            },
        )
        .expect("context")
    }

    #[test]
    fn publication_reopens_with_exact_policy_and_permissions() {
        let path = test_path("reopen");
        let key = [7; 32];
        {
            let mut store = AuthorizationPolicyStore::open(&path, key).expect("open");
            store.publish(scope(), policy(1)).expect("publish");
            assert_eq!(store.policy(&scope()), Ok(&policy(1)));
            assert_eq!(store.snapshot().len(), 1);
        }
        let store = AuthorizationPolicyStore::open(&path, key).expect("reopen");
        assert_eq!(store.policy(&scope()), Ok(&policy(1)));
        #[cfg(unix)]
        assert_eq!(std::fs::metadata(&path).expect("metadata").permissions().mode() & 0o777, 0o600);
        std::fs::remove_file(path).expect("remove journal");
    }

    #[test]
    fn publication_refuses_rollback_and_equivocation_but_allows_idempotence() {
        let path = test_path("generation");
        let mut store = AuthorizationPolicyStore::open(&path, [8; 32]).expect("open");
        store.publish(scope(), policy(2)).expect("publish");
        store.publish(scope(), policy(2)).expect("idempotent");
        assert_eq!(
            store.publish(scope(), policy(1)),
            Err(AuthorizationError::PolicyGenerationRollback)
        );
        let different = Policy::try_new_with_id(
            AuthorizationPolicyUlid::from_wire_bytes([9; 16]),
            2,
            ["owner"],
            [PolicyRule::try_new(Operation::Read, "other").expect("rule")],
        )
        .expect("policy");
        assert_eq!(
            store.publish(scope(), different),
            Err(AuthorizationError::PolicyGenerationEquivocation)
        );
        std::fs::remove_file(path).expect("remove journal");
    }

    #[test]
    fn wrong_key_tamper_and_truncation_fail_closed() {
        let path = test_path("corruption");
        {
            let mut store = AuthorizationPolicyStore::open(&path, [10; 32]).expect("open");
            store.publish(scope(), policy(1)).expect("publish");
        }
        assert!(matches!(
            AuthorizationPolicyStore::open(&path, [11; 32]),
            Err(AuthorizationError::PolicyJournalCorrupt)
        ));
        let length = std::fs::metadata(&path).expect("metadata").len();
        OpenOptions::new()
            .write(true)
            .open(&path)
            .expect("open for truncate")
            .set_len(length - 1)
            .expect("truncate");
        let recovered =
            AuthorizationPolicyStore::open(&path, [10; 32]).expect("torn tail recovery");
        assert!(recovered.snapshot().is_empty());
        std::fs::remove_file(path).expect("remove journal");

        let tampered = test_path("tamper");
        {
            let mut store = AuthorizationPolicyStore::open(&tampered, [12; 32]).expect("open");
            store.publish(scope(), policy(1)).expect("publish");
        }
        let mut file = OpenOptions::new().read(true).write(true).open(&tampered).expect("open");
        // Skip the frame length, version/op, and the all-ones expiry.
        file.seek(SeekFrom::Start((JOURNAL_MAGIC.len() + 17) as u64)).expect("seek");
        file.write_all(&[0xFF]).expect("tamper");
        file.sync_all().expect("sync");
        assert!(matches!(
            AuthorizationPolicyStore::open(&tampered, [12; 32]),
            Err(AuthorizationError::PolicyJournalCorrupt)
        ));
        std::fs::remove_file(tampered).expect("remove journal");
    }

    #[cfg(unix)]
    #[test]
    fn broad_permissions_and_symlinks_are_refused() {
        use std::os::unix::fs::{PermissionsExt, symlink};

        let path = test_path("permissions");
        AuthorizationPolicyStore::open(&path, [13; 32]).expect("create");
        std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o640))
            .expect("change permissions");
        assert!(matches!(
            AuthorizationPolicyStore::open(&path, [13; 32]),
            Err(AuthorizationError::PolicyStoreUnsafe)
        ));

        let link = test_path("symlink");
        symlink(&path, &link).expect("symlink");
        assert!(matches!(
            AuthorizationPolicyStore::open(&link, [13; 32]),
            Err(AuthorizationError::PolicyStoreUnsafe)
        ));
        std::fs::remove_file(link).expect("remove symlink");
        std::fs::remove_file(path).expect("remove journal");
    }

    #[test]
    fn revocation_is_durable_and_invalidates_old_contexts() {
        let path = test_path("revocation");
        let mut store = AuthorizationPolicyStore::open(&path, [14; 32]).expect("open");
        store.publish(scope(), policy(1)).expect("publish");
        let request = crate::authorization::AuthorizationRequest::try_new(
            scope(),
            "terrascale.database",
            super::super::Action::Read,
            "documents",
            BTreeMap::from([(
                "owner".to_owned(),
                super::super::AttributeValue::String("tenant".to_owned()),
            )]),
            BTreeSet::from(["member:users:2:8".to_owned()]),
            10,
        )
        .expect("request");
        assert!(store.evaluate(&context(1), &request).is_allowed());
        store
            .revoke_principal(scope(), PrincipalUlid::from_wire_bytes([6; 16]), 2)
            .expect("revoke");
        assert!(!store.evaluate(&context(1), &request).is_allowed());
        drop(store);
        let reopened = AuthorizationPolicyStore::open(&path, [14; 32]).expect("reopen");
        assert!(!reopened.evaluate(&context(1), &request).is_allowed());
        std::fs::remove_file(path).expect("remove journal");
    }

    #[test]
    fn deleting_a_complete_record_breaks_the_authentication_chain() {
        let path = test_path("chain");
        {
            let mut store = AuthorizationPolicyStore::open(&path, [15; 32]).expect("open");
            store.publish(scope(), policy(1)).expect("publish");
            store.publish(scope(), policy(2)).expect("publish");
        }
        let bytes = std::fs::read(&path).expect("read journal");
        let first_len =
            usize::try_from(u32::from_le_bytes(bytes[8..12].try_into().unwrap())).unwrap();
        let first_end = 12 + first_len + AUTHENTICATION_BYTES;
        let mut retained = bytes[..8].to_vec();
        retained.extend_from_slice(&bytes[first_end..]);
        std::fs::write(&path, retained).expect("rewrite journal");
        assert!(matches!(
            AuthorizationPolicyStore::open(&path, [15; 32]),
            Err(AuthorizationError::PolicyJournalCorrupt)
        ));
        std::fs::remove_file(path).expect("remove journal");
    }

    #[test]
    fn concurrent_openers_are_serialized_and_expiry_is_enforced() {
        let path = test_path("lock");
        let mut first = AuthorizationPolicyStore::open(&path, [16; 32]).expect("open");
        assert!(matches!(
            AuthorizationPolicyStore::open(&path, [16; 32]),
            Err(AuthorizationError::PolicyStoreUnavailable)
        ));
        first.publish_with_expiry(scope(), policy(1), 5).expect("publish");
        assert!(first.expiries.get(&scope()).is_some_and(|expiry| *expiry == 5));
        drop(first);
        let reopened = AuthorizationPolicyStore::open(&path, [16; 32]).expect("reopen");
        assert_eq!(reopened.expiries.get(&scope()), Some(&5));
        std::fs::remove_file(path).expect("remove journal");
    }
}
