//! API-key authentication for the public `/api/v1` surface (`SHORT_GOAL`
//! `S-03`): keys are hashed at rest, scoped to database/collection/verbs, and
//! the key-to-principal resolution is a sharded LRU whose entries carry a
//! revocation generation — bumping the generation invalidates every cached
//! admission at once, so a revoked key costs one probe to reject everywhere.

use std::collections::HashMap;
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
}

/// The admitted identity a request proceeds under.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Principal {
    /// The key's stable label (never the key material).
    label: String,
    /// The scope the key was issued with.
    scope: Scope,
}

impl Principal {
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
}

/// One shard of the admission cache: key hash → principal, bounded and
/// generation-stamped.
struct Shard {
    entries: HashMap<[u8; 32], (u64, Principal)>,
    order: std::collections::VecDeque<[u8; 32]>,
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
    /// dropped; only the hash and the principal survive.
    pub fn register(&self, key: &str, label: &str, scope: Scope) {
        let hash = hash_key(key);
        self.registered.lock().unwrap_or_else(std::sync::PoisonError::into_inner).insert(
            hash,
            (self.generation.load(Ordering::Acquire), Principal { label: label.to_owned(), scope }),
        );
        // A registration after a revocation must not resurrect a stale
        // cache entry: drop any cached admission for this hash so the next
        // probe re-stamps it at the current generation.
        let mut shard =
            self.shard_of(&hash).lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        shard.entries.remove(&hash);
    }

    /// Admits one presented key, resolving its principal through the
    /// sharded cache. A miss costs one hash probe against the registered
    /// hashes; a revoked key is rejected once per shard before the stale
    /// entry drops.
    ///
    /// # Errors
    ///
    /// Returns [`ApiError::malformed_credential`] when the presented key
    /// cannot be key material at all, and [`ApiError::unauthorized`] when
    /// no live registration matches it.
    pub fn admit(&self, key: &str) -> Result<Principal, ApiError> {
        if key.len() < 16 || !key.is_ascii() || key.contains([' ', '\t', '\n', '\r']) {
            return Err(ApiError::malformed_credential());
        }
        let hash = hash_key(key);
        let generation = self.generation.load(Ordering::Acquire);
        let shard = self.shard_of(&hash);
        let mut shard = shard.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Some((seen_at, principal)) = shard.entries.get(&hash).cloned() {
            if seen_at == generation {
                return Ok(principal);
            }
            // A stale generation entry is a revoked credential's cache
            // remnant: drop it and fall through to the honest probe.
            shard.entries.remove(&hash);
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
        shard.entries.insert(hash, (generation, principal.clone()));
        shard.order.push_back(hash);
        if shard.order.len() > self.capacity_per_shard
            && let Some(oldest) = shard.order.pop_front()
        {
            // The newest stamp for `oldest` (a repeated admission refreshed
            // it) is not evictable here; only stale stamps go.
            shard.entries.retain(|stamp, (seen_at, _)| *seen_at == generation || *stamp != oldest);
        }
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
                .map(|_| {
                    Mutex::new(Shard {
                        entries: HashMap::new(),
                        order: std::collections::VecDeque::new(),
                    })
                })
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

#[cfg(test)]
mod tests {
    use super::KeyStore;
    use super::Scope;
    use super::Verbs;

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
    fn revoke_all_invalidates_cached_admissions_at_once() {
        let store = KeyStore::with_keys(&[(
            "tskey_test_abcdefghijklmnop",
            "ops",
            Scope::every_database(Verbs::READ),
        )]);
        assert!(store.admit("tskey_test_abcdefghijklmnop").is_ok());
        store.revoke_all();
        assert!(store.admit("tskey_test_abcdefghijklmnop").is_err());
    }

    #[test]
    fn scope_admits_only_what_it_names() {
        let scope = Scope::database("prod", Verbs::READ);
        assert!(scope.admits(Verbs::READ, "prod"));
        assert!(!scope.admits(Verbs::WRITE, "prod"), "write is not admitted");
        assert!(!scope.admits(Verbs::READ, "other"), "another database is not admitted");
        assert!(Scope::every_database(Verbs::ALL).admits(Verbs::ADMIN, "any"));
    }
}
