//! API-key authentication for the public `/api/v1` surface (`SHORT_GOAL`
//! `S-03`): keys are hashed at rest, scoped to database/collection/verbs,
//! optionally bounded with an expiry, and the key-to-principal resolution
//! is a sharded LRU whose entries carry a revocation generation — bumping
//! the generation invalidates every cached admission at once, so a revoked
//! key costs one probe to reject everywhere. Expiry is enforced on every
//! probe including cache hits, so a cached admission never outlives its
//! credential.

use std::collections::HashMap;
use std::collections::VecDeque;
use std::sync::Mutex;
use std::sync::atomic::AtomicU64;
use std::sync::atomic::Ordering;

use blake3::Hasher;

use crate::api::ApiError;

/// The verbs a scope can admit. Refusal is the default for anything a
/// scope does not name.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Verbs(u8);

impl Verbs {
    /// Admit reads (`GET`/list/query paths).
    pub const READ: Self = Self(1 << 0);
    /// Admit writes (create/put/patch/batch/delete).
    pub const WRITE: Self = Self(1 << 1);
    /// Admit administrative actions (schema, indexes, keys, deletion).
    pub const ADMIN: Self = Self(1 << 2);
    /// Every verb — the bootstrap key's scope.
    pub const ALL: Self = Self(0b111);

    /// Whether this scope admits `other`.
    #[must_use]
    pub const fn admits(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }
}

/// One credential's scope: a database (or every database), optional
/// collection narrowing, and the admitted verbs.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Scope {
    /// The database name this key admits, or `None` for every database.
    database: Option<String>,
    /// The collections this key admits, or `None` for every collection.
    collections: Option<Vec<String>>,
    /// The verbs this key admits.
    verbs: Verbs,
}

impl Scope {
    /// A scope admitting every collection of one database.
    #[must_use]
    pub fn database(name: &str, verbs: Verbs) -> Self {
        Self { database: Some(name.to_owned()), collections: None, verbs }
    }

    /// A scope admitting only the named collection in one database.
    #[must_use]
    pub fn database_collection(name: &str, collection: &str, verbs: Verbs) -> Self {
        Self {
            database: Some(name.to_owned()),
            collections: Some(vec![collection.to_owned()]),
            verbs,
        }
    }

    /// A scope admitting every database (bootstrap tooling only).
    #[must_use]
    pub fn every_database(verbs: Verbs) -> Self {
        Self { database: None, collections: None, verbs }
    }

    /// Whether this scope admits `verb` on `database`.
    #[must_use]
    pub fn admits(&self, verb: Verbs, database: &str) -> bool {
        self.verbs.admits(verb) && self.database.as_ref().is_none_or(|name| name == database)
    }

    /// Whether this scope admits an operation over every collection in a database.
    #[must_use]
    pub fn admits_database(&self, verb: Verbs, database: &str) -> bool {
        self.collections.is_none() && self.admits(verb, database)
    }

    /// Whether this scope admits `verb` on one named collection. A database
    /// scope with no collection list admits every collection; a narrowed
    /// scope never widens to a sibling collection.
    #[must_use]
    pub fn admits_collection(&self, verb: Verbs, database: &str, collection: &str) -> bool {
        self.admits(verb, database)
            && self
                .collections
                .as_ref()
                .is_none_or(|allowed| allowed.iter().any(|name| name == collection))
    }

    /// Whether this scope admits `verb` outside any one database (database
    /// creation and other deployment-level actions): only an every-database
    /// scope does, so a database-scoped key can never mint or drop databases.
    #[must_use]
    pub fn admits_global(&self, verb: Verbs) -> bool {
        self.database.is_none() && self.verbs.admits(verb)
    }
}

/// The admitted identity a request proceeds under.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Principal {
    /// The key's stable label (never the key material).
    label: String,
    /// The scope the key was issued with.
    scope: Scope,
    /// When the credential stops admitting, unix milliseconds, if bounded.
    /// `None` is a never-expiring credential (the bootstrap key); every
    /// other credential carries a bound so a leaked key dies on its own.
    expires_at: Option<u64>,
}

impl Principal {
    pub(crate) fn delegated(label: String, scope: Scope, expires_at: u64) -> Self {
        Self { label, scope, expires_at: Some(expires_at) }
    }

    /// Caps a producer admission by an earlier verified credential expiry.
    pub(crate) fn cap_expiry(mut self, expires_at: u64) -> Self {
        if let Some(current) = self.expires_at {
            self.expires_at = Some(current.min(expires_at));
        }
        self
    }

    /// The wire label responses attribute the request to.
    #[must_use]
    pub fn label(&self) -> &str {
        &self.label
    }

    /// The scope handlers check before touching data.
    #[must_use]
    pub fn scope(&self) -> &Scope {
        &self.scope
    }

    /// When the credential stops admitting, unix milliseconds, if bounded.
    #[must_use]
    pub const fn expires_at(&self) -> Option<u64> {
        self.expires_at
    }

    /// Whether the credential is expired at `now` (unix milliseconds).
    /// The bound is half-open: `now >= expires_at` refuses, matching the
    /// TBEP lane-expiry convention.
    #[must_use]
    pub const fn is_expired_at(&self, now: u64) -> bool {
        match self.expires_at {
            Some(at) => now >= at,
            None => false,
        }
    }
}

/// One shard of the admission cache: key hash → principal, bounded and
/// generation-stamped.
struct Shard {
    entries: HashMap<[u8; 32], (u64, Principal)>,
    order: VecDeque<[u8; 32]>,
}

impl Shard {
    /// Removes an entry and every queue stamp for it.
    fn forget(&mut self, hash: &[u8; 32]) {
        self.entries.remove(hash);
        self.order.retain(|queued| queued != hash);
    }

    /// Marks an entry as most recently used. The queue has one stamp per key.
    fn touch(&mut self, hash: &[u8; 32]) {
        self.order.retain(|queued| queued != hash);
        self.order.push_back(*hash);
    }

    /// Evicts the least recently used entries until the hard bound holds.
    fn evict_over_capacity(&mut self, capacity: usize) {
        while self.entries.len() > capacity {
            let oldest = self.order.pop_front().or_else(|| self.entries.keys().min().copied());
            let Some(oldest) = oldest else {
                break;
            };
            self.entries.remove(&oldest);
        }
    }
}

/// The hashed-key store behind the public surface.
pub struct KeyStore {
    shards: Vec<Mutex<Shard>>,
    capacity_per_shard: usize,
    registered: Mutex<HashMap<[u8; 32], (u64, Principal)>>,
    generation: AtomicU64,
}

const SHARDS: usize = 8;
const CAPACITY_PER_SHARD: usize = 512;

impl KeyStore {
    /// An empty store: no key is admitted (still deny-all, but with the
    /// machinery in place for registrations to arrive from the control
    /// plane).
    #[must_use]
    pub fn empty() -> Self {
        Self::default()
    }

    /// A store admitting exactly the given `(key, scope, label)`
    /// registrations — the shape the control-plane load later fills.
    #[must_use]
    pub fn with_keys(registrations: &[(&str, &str, Scope)]) -> Self {
        let store = Self::default();
        for (key, label, scope) in registrations {
            store.register(key, label, scope.clone());
        }
        store
    }

    /// Registers one credential. The key material itself is hashed and
    /// dropped; only the hash and the principal survive. The credential
    /// never expires (the bootstrap key's shape).
    pub fn register(&self, key: &str, label: &str, scope: Scope) {
        self.register_with_expiry(key, label, scope, None);
    }

    /// Registers one credential with a bounded admission lifetime
    /// (`expires_at`: unix milliseconds, `None` for never-expiring).
    /// Expiry is enforced on every [`Self::admit`] probe, including cache
    /// hits, so a cached admission never outlives its credential. The key
    /// material itself is hashed and dropped; only the hash and the
    /// principal survive.
    pub fn register_with_expiry(
        &self,
        key: &str,
        label: &str,
        scope: Scope,
        expires_at: Option<u64>,
    ) {
        let hash = hash_key(key);
        self.registered.lock().unwrap_or_else(std::sync::PoisonError::into_inner).insert(
            hash,
            (
                self.generation.load(Ordering::Acquire),
                Principal { label: label.to_owned(), scope, expires_at },
            ),
        );
        // A registration after a revocation must not resurrect a stale
        // cache entry: drop any cached admission for this hash so the next
        // probe re-stamps it at the current generation.
        let mut shard =
            self.shard_of(&hash).lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        shard.forget(&hash);
    }

    /// Admits one presented key, resolving its principal through the
    /// sharded cache. A miss costs one hash probe against the registered
    /// hashes; a revoked key is rejected once per shard before the stale
    /// entry drops. An expired credential is rejected on every probe —
    /// cache hits included — and its stale entry is evicted, so expiry
    /// never lingers past one rejected probe per shard.
    ///
    /// # Errors
    ///
    /// Returns [`ApiError::malformed_credential`] when the presented key
    /// cannot be key material at all, and [`ApiError::unauthorized`] when
    /// no live registration matches it or the credential has expired.
    pub fn admit(&self, key: &str) -> Result<Principal, ApiError> {
        if key.len() < 16 || !key.is_ascii() || key.contains([' ', '\t', '\n', '\r']) {
            return Err(ApiError::malformed_credential());
        }
        let hash = hash_key(key);
        let generation = self.generation.load(Ordering::Acquire);
        let now = now_millis();
        let shard = self.shard_of(&hash);
        let mut shard = shard.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Some((seen_at, principal)) = shard.entries.get(&hash).cloned() {
            if seen_at == generation {
                // The cached admission carries the credential's expiry, so
                // a cache hit cannot outlive it: expired entries drop here
                // instead of admitting.
                if principal.is_expired_at(now) {
                    shard.forget(&hash);
                    return Err(ApiError::unauthorized());
                }
                shard.touch(&hash);
                return Ok(principal);
            }
            // A stale generation entry is a revoked credential's cache
            // remnant: drop it and fall through to the honest probe.
            shard.forget(&hash);
        }
        let registered = self.registered.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        // A registration stamped at an older generation was revoked: the
        // revocation generation gates registrations, not only the cache.
        let Some((registered_at, principal)) = registered.get(&hash).cloned() else {
            return Err(ApiError::unauthorized());
        };
        drop(registered);
        if registered_at != generation {
            return Err(ApiError::unauthorized());
        }
        if principal.is_expired_at(now) {
            return Err(ApiError::unauthorized());
        }
        shard.entries.insert(hash, (generation, principal.clone()));
        shard.touch(&hash);
        shard.evict_over_capacity(self.capacity_per_shard);
        Ok(principal)
    }

    /// Revokes every credential at once: the generation bump invalidates
    /// every cached admission without walking the shards, and every
    /// registration stamped at an older generation stops admitting on its
    /// next probe. Re-registering at the new generation restores a key.
    pub fn revoke_all(&self) {
        self.generation.fetch_add(1, Ordering::Release);
    }

    fn shard_of(&self, hash: &[u8; 32]) -> &Mutex<Shard> {
        let index = usize::from(hash[0]) % self.shards.len();
        &self.shards[index]
    }
}

impl Default for KeyStore {
    fn default() -> Self {
        Self {
            shards: (0..SHARDS)
                .map(|_| Mutex::new(Shard { entries: HashMap::new(), order: VecDeque::new() }))
                .collect(),
            capacity_per_shard: CAPACITY_PER_SHARD,
            registered: Mutex::new(HashMap::new()),
            generation: AtomicU64::new(1),
        }
    }
}

/// Hashes one presented key for registration and admission both — the store
/// never retains key material.
fn hash_key(key: &str) -> [u8; 32] {
    let mut hasher = Hasher::new();
    hasher.update(key.as_bytes());
    *hasher.finalize().as_bytes()
}

/// Wall-clock unix milliseconds for expiry checks. A clock that reads
/// before the epoch saturates to `u64::MAX` (fail-closed: every bounded
/// credential reads expired while never-expiring credentials keep
/// admitting), so a broken clock cannot stretch a leaked key's lifetime.
fn now_millis() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(u64::MAX, |elapsed| elapsed.as_millis().try_into().unwrap_or(u64::MAX))
}

#[cfg(test)]
mod tests {
    use super::CAPACITY_PER_SHARD;
    use super::KeyStore;
    use super::SHARDS;
    use super::Scope;
    use super::Verbs;
    use super::hash_key;
    use std::sync::Arc;

    fn keys_for_shard(shard_index: usize, count: usize) -> Vec<String> {
        let mut keys = Vec::with_capacity(count);
        let mut candidate = 0;
        while keys.len() < count {
            let key = format!("tskey_cache_{candidate:08}");
            candidate += 1;
            if usize::from(hash_key(&key)[0]) % SHARDS == shard_index {
                keys.push(key);
            }
        }
        keys
    }

    #[test]
    fn admit_resolves_a_registered_key_and_caches_it() {
        let store = KeyStore::with_keys(&[(
            "tskey_test_abcdefghijklmnop",
            "bootstrap",
            Scope::every_database(Verbs::ALL),
        )]);
        let first = store.admit("tskey_test_abcdefghijklmnop").expect("admits");
        assert_eq!(first.label(), "bootstrap");
        // The second admission is a cache hit on the same principal.
        let second = store.admit("tskey_test_abcdefghijklmnop").expect("admits");
        assert_eq!(first, second);
    }

    #[test]
    fn unregistered_and_malformed_keys_are_typed_errors() {
        let store = KeyStore::with_keys(&[]);
        let rejected = store.admit("tskey_test_abcdefghijklmnop").expect_err("not registered");
        assert_eq!(rejected.code(), "unauthorized");
        assert_eq!(rejected.status(), axum::http::StatusCode::UNAUTHORIZED);
        assert_eq!(store.admit("short").expect_err("too short").code(), "malformed_credential");
        assert!(store.admit("has space inside it padding!").is_err());
    }

    #[test]
    fn expiry_is_enforced_on_first_probe_and_cache_hits() {
        let live = "tskey_test_livecredential01";
        let dead = "tskey_test_deadcredential01";
        let store = KeyStore::empty();
        let now = super::now_millis();
        store.register_with_expiry(
            live,
            "live",
            Scope::every_database(Verbs::READ),
            Some(now.saturating_add(60_000)),
        );
        store.register_with_expiry(
            dead,
            "dead",
            Scope::every_database(Verbs::READ),
            Some(now.saturating_sub(1).max(1)),
        );

        let admitted = store.admit(live).expect("live credential admits");
        assert_eq!(admitted.label(), "live");
        assert_eq!(admitted.expires_at(), Some(now.saturating_add(60_000)));
        // The second admission is a cache hit and must still admit.
        assert!(store.admit(live).is_ok(), "live cache hit admits");
        // An already-expired credential refuses without ever caching.
        assert_eq!(store.admit(dead).expect_err("dead refuses").code(), "unauthorized");
        {
            let shard = store.shards[usize::from(super::hash_key(dead)[0]) % SHARDS]
                .lock()
                .expect("shard lock");
            assert!(
                !shard.entries.contains_key(&super::hash_key(dead)),
                "expired credentials leave no cache entry"
            );
        }
    }

    #[test]
    fn cache_hits_stop_admitting_once_the_credential_expires() {
        let key = "tskey_test_rotatingcred0001";
        let store = KeyStore::empty();
        let now = super::now_millis();
        store.register_with_expiry(
            key,
            "short-lived",
            Scope::every_database(Verbs::READ),
            Some(now.saturating_add(60_000)),
        );
        assert!(store.admit(key).is_ok(), "warms the cache");
        // Re-issuing the same key material with a past bound must turn the
        // next probe — a cache hit without the re-issue — into a refusal.
        store.register_with_expiry(
            key,
            "short-lived",
            Scope::every_database(Verbs::READ),
            Some(now.saturating_sub(1).max(1)),
        );
        assert_eq!(
            store.admit(key).expect_err("expired cache entry refuses").code(),
            "unauthorized"
        );
        // Re-issuing with a future bound restores admission.
        store.register_with_expiry(
            key,
            "short-lived",
            Scope::every_database(Verbs::READ),
            Some(now.saturating_add(60_000)),
        );
        assert!(store.admit(key).is_ok(), "renewed credential admits");
    }

    #[test]
    fn expiry_bound_is_half_open_and_unbounded_keys_ignore_the_clock() {
        let principal = super::Principal {
            label: "probe".to_owned(),
            scope: Scope::every_database(Verbs::READ),
            expires_at: Some(1_000),
        };
        assert!(!principal.is_expired_at(999), "before the bound admits");
        assert!(principal.is_expired_at(1_000), "at the bound refuses");
        assert!(principal.is_expired_at(1_001), "past the bound refuses");
        let bootstrap = super::Principal {
            label: "bootstrap".to_owned(),
            scope: Scope::every_database(Verbs::ALL),
            expires_at: None,
        };
        assert!(!bootstrap.is_expired_at(u64::MAX), "never-expiring admits at any clock");
        assert_eq!(bootstrap.expires_at(), None);
    }

    #[test]
    fn revoke_all_invalidates_cached_admissions_at_once() {
        let key = "tskey_test_abcdefghijklmnop";
        let store = KeyStore::with_keys(&[(key, "ops", Scope::every_database(Verbs::READ))]);
        assert!(store.admit(key).is_ok());
        store.revoke_all();
        assert!(store.admit(key).is_err());
        store.register(key, "restored", Scope::every_database(Verbs::READ));
        assert_eq!(store.admit(key).expect("re-registered key").label(), "restored");
    }

    #[test]
    fn admission_cache_is_hard_bounded_and_refreshes_lru_hits() {
        let keys = keys_for_shard(0, CAPACITY_PER_SHARD + 1);
        let store = KeyStore::empty();
        for key in &keys {
            store.register(key, "cache-test", Scope::every_database(Verbs::READ));
        }

        for key in &keys[..CAPACITY_PER_SHARD] {
            assert!(store.admit(key).is_ok());
        }
        {
            let shard = store.shards[0].lock().expect("shard lock");
            assert_eq!(shard.entries.len(), CAPACITY_PER_SHARD);
            assert_eq!(shard.order.len(), CAPACITY_PER_SHARD);
            assert_eq!(shard.order.front(), Some(&hash_key(&keys[0])));
        }

        // A hit moves the first key behind the other current entries.
        assert!(store.admit(&keys[0]).is_ok());
        assert!(store.admit(&keys[CAPACITY_PER_SHARD]).is_ok());

        let shard = store.shards[0].lock().expect("shard lock");
        assert_eq!(shard.entries.len(), CAPACITY_PER_SHARD);
        assert_eq!(shard.order.len(), CAPACITY_PER_SHARD);
        assert!(shard.entries.contains_key(&hash_key(&keys[0])));
        assert!(!shard.entries.contains_key(&hash_key(&keys[1])));
        assert_eq!(shard.order.front(), Some(&hash_key(&keys[2])));
    }

    #[test]
    fn concurrent_admissions_keep_each_shard_bounded() {
        let keys: Vec<_> =
            (0..SHARDS).flat_map(|shard| keys_for_shard(shard, CAPACITY_PER_SHARD + 8)).collect();
        let store = Arc::new(KeyStore::empty());
        for key in &keys {
            store.register(key, "concurrent-cache-test", Scope::every_database(Verbs::READ));
        }

        let handles: Vec<_> = (0..4)
            .map(|_| {
                let store = Arc::clone(&store);
                let keys = keys.clone();
                std::thread::spawn(move || {
                    for _ in 0..2 {
                        for key in &keys {
                            assert!(store.admit(key).is_ok());
                        }
                    }
                })
            })
            .collect();
        for handle in handles {
            handle.join().expect("admission worker");
        }

        for shard in &store.shards {
            let shard = shard.lock().expect("shard lock");
            assert!(shard.entries.len() <= CAPACITY_PER_SHARD);
            assert!(shard.order.len() <= CAPACITY_PER_SHARD);
        }
    }

    #[test]
    fn scope_admits_only_what_it_names() {
        let scope = Scope::database("prod", Verbs::READ);
        assert!(scope.admits(Verbs::READ, "prod"));
        assert!(!scope.admits(Verbs::WRITE, "prod"), "write is not admitted");
        assert!(!scope.admits(Verbs::READ, "other"), "another database is not admitted");
        assert!(Scope::every_database(Verbs::ALL).admits(Verbs::ADMIN, "any"));
    }

    #[test]
    fn global_admission_needs_an_every_database_scope() {
        assert!(Scope::every_database(Verbs::ADMIN).admits_global(Verbs::ADMIN));
        assert!(!Scope::every_database(Verbs::READ).admits_global(Verbs::ADMIN));
        assert!(!Scope::database("prod", Verbs::ALL).admits_global(Verbs::ADMIN));
    }
}
