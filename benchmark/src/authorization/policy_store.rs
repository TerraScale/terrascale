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
};

#[cfg(unix)]
use std::os::unix::{fs::OpenOptionsExt, fs::PermissionsExt};

use zeroize::Zeroize;

use super::{
    AUTHORIZATION_FORMAT_VERSION, AuthorizationError, ClaimComparison, ClaimPredicate,
    FieldProjection, Operation, Policy, PolicyRule, RelationshipPredicate, ResourceScope,
};
use crate::identity::{
    AuthorizationPolicyUlid, DatabaseUlid, PrincipalGrantUlid, ProjectUlid, TenantUlid,
};

const JOURNAL_MAGIC: &[u8; 8] = b"TBAPJNL1";
const RECORD_VERSION: u8 = 1;
const AUTHENTICATION_BYTES: usize = 32;
const MAX_JOURNAL_BYTES: u64 = 64 * 1024 * 1024;
const MAX_RECORD_BYTES: usize = 4 * 1024 * 1024;
const RECORD_DOMAIN: &[u8] = b"terrabase.authorization.policy-journal.record.v1\0";

/// An immutable view of the latest durable policy for each database scope.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PolicyStoreSnapshot {
    policies: BTreeMap<ResourceScope, Policy>,
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
        let path = path.as_ref().to_path_buf();
        reject_symlink(&path)?;
        let (mut file, created) = open_journal(&path)?;
        validate_open_file(&file)?;

        if created {
            file.write_all(JOURNAL_MAGIC)
                .and_then(|()| file.sync_all())
                .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
            sync_parent(&path)?;
        }

        let snapshot = replay(&mut file, &authentication_key)?;
        file.seek(SeekFrom::End(0))
            .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
        Ok(Self { path, file, authentication_key, snapshot })
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
        if let Some(current) = self.snapshot.policies.get(&scope) {
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

        let payload = encode_record(&scope, &policy)?;
        let payload_len = u32::try_from(payload.len())
            .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
        let tag = authenticate(&self.authentication_key, &payload);

        self.file
            .write_all(&payload_len.to_le_bytes())
            .and_then(|()| self.file.write_all(&payload))
            .and_then(|()| self.file.write_all(tag.as_bytes()))
            .and_then(|()| self.file.sync_data())
            .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
        self.snapshot.policies.insert(scope, policy);
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
    let parent = path.parent().filter(|parent| !parent.as_os_str().is_empty()).unwrap_or(Path::new("."));
    File::open(parent)
        .and_then(|directory| directory.sync_all())
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)
}

fn replay(
    file: &mut File,
    authentication_key: &[u8; AUTHENTICATION_BYTES],
) -> Result<PolicyStoreSnapshot, AuthorizationError> {
    let length = file
        .metadata()
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?
        .len();
    if length > MAX_JOURNAL_BYTES {
        return Err(AuthorizationError::PolicyJournalCorrupt);
    }
    let length = usize::try_from(length).map_err(|_| AuthorizationError::PolicyJournalCorrupt)?;
    file.seek(SeekFrom::Start(0))
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    let mut bytes = Vec::with_capacity(length);
    file.take(MAX_JOURNAL_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| AuthorizationError::PolicyStoreUnavailable)?;
    if bytes.len() != length || !bytes.starts_with(JOURNAL_MAGIC) {
        return Err(AuthorizationError::PolicyJournalCorrupt);
    }

    let mut offset = JOURNAL_MAGIC.len();
    let mut policies = BTreeMap::new();
    while offset < bytes.len() {
        let length_end = offset.checked_add(4).ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        let record_len = bytes
            .get(offset..length_end)
            .and_then(|value| value.try_into().ok())
            .map(u32::from_le_bytes)
            .and_then(|value| usize::try_from(value).ok())
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        if record_len == 0 || record_len > MAX_RECORD_BYTES {
            return Err(AuthorizationError::PolicyJournalCorrupt);
        }
        let payload_end = length_end
            .checked_add(record_len)
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        let tag_end = payload_end
            .checked_add(AUTHENTICATION_BYTES)
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        let payload = bytes
            .get(length_end..payload_end)
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        let tag = bytes
            .get(payload_end..tag_end)
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        if authenticate(authentication_key, payload).as_bytes() != tag {
            return Err(AuthorizationError::PolicyJournalCorrupt);
        }
        let (scope, policy) = decode_record(payload)?;
        apply_replayed(&mut policies, scope, policy)?;
        offset = tag_end;
    }
    Ok(PolicyStoreSnapshot { policies })
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

fn authenticate(key: &[u8; AUTHENTICATION_BYTES], payload: &[u8]) -> blake3::Hash {
    let mut hasher = blake3::Hasher::new_keyed(key);
    hasher.update(RECORD_DOMAIN);
    hasher.update(payload);
    hasher.finalize()
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
        match decoder.byte()? {
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
        let end = self
            .offset
            .checked_add(length)
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        let value = self
            .bytes
            .get(self.offset..end)
            .ok_or(AuthorizationError::PolicyJournalCorrupt)?;
        self.offset = end;
        Ok(value)
    }

    fn byte(&mut self) -> Result<u8, AuthorizationError> {
        Ok(self.take(1)?[0])
    }

    fn array<const N: usize>(&mut self) -> Result<[u8; N], AuthorizationError> {
        self.take(N)?
            .try_into()
            .map_err(|_| AuthorizationError::PolicyJournalCorrupt)
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
        std::env::temp_dir().join(format!(
            "terrabase-policy-store-{name}-{}-{counter}.journal",
            std::process::id()
        ))
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
        let predicate = ClaimPredicate::try_new("owner", "tenant", ClaimComparison::Equals)
            .expect("predicate");
        let relationship = RelationshipPredicate::try_new("member", "users", 2, 8)
            .expect("relationship");
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
        assert!(matches!(
            AuthorizationPolicyStore::open(&path, [10; 32]),
            Err(AuthorizationError::PolicyJournalCorrupt)
        ));
        std::fs::remove_file(path).expect("remove journal");

        let tampered = test_path("tamper");
        {
            let mut store = AuthorizationPolicyStore::open(&tampered, [12; 32]).expect("open");
            store.publish(scope(), policy(1)).expect("publish");
        }
        let mut file = OpenOptions::new().read(true).write(true).open(&tampered).expect("open");
        file.seek(SeekFrom::Start((JOURNAL_MAGIC.len() + 8) as u64)).expect("seek");
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
}
