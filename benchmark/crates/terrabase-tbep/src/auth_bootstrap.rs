//! Explicit auth consensus bootstrap grants received from authenticated GRU control.
//! This journal contains membership metadata only, never identity records or tokens.

use crate::placement::PlacementError;
use crate::proto::terrabase::v1 as pb;
use prost::Message as _;
use rusqlite::{Connection, OptionalExtension, TransactionBehavior, params};
use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;
use std::sync::Mutex;
use terrabase::identity::{Ulid, process_generator};
use terrabase_cluster::production::{DatabaseRouteScope, MemberWorkloadIdentity};

/// Immutable host and configured database boundary for auth bootstrap control.
#[derive(Clone)]
pub struct AuthBootstrapBoundary {
    environment: String,
    cluster: String,
    member: String,
    scopes: BTreeSet<DatabaseRouteScope>,
    seats: BTreeMap<String, String>,
}

impl AuthBootstrapBoundary {
    /// Bind grant admission to this host, exact database tuples, and known TLS seats.
    /// This does not turn those seats into voters; only an explicit grant can do so.
    ///
    /// # Errors
    /// Rejects invalid identities, empty scopes, or seats outside GRU/SFO/FRA.
    pub fn new(
        environment: &str,
        cluster: &str,
        member: &str,
        scopes: BTreeSet<DatabaseRouteScope>,
        seats: BTreeMap<String, String>,
    ) -> Result<Self, PlacementError> {
        MemberWorkloadIdentity::try_new(environment, cluster, member)
            .map_err(|_| PlacementError::Invalid)?;
        if scopes.is_empty()
            || scopes.len() > 1024
            || !seats.contains_key(member)
            || !(2..=6).contains(&seats.len())
        {
            return Err(PlacementError::Invalid);
        }
        for (member, region) in &seats {
            Ulid::from_text(member).map_err(|_| PlacementError::Invalid)?;
            if !["GRU", "SFO", "FRA"].contains(&region.as_str()) {
                return Err(PlacementError::Invalid);
            }
        }
        Ok(Self {
            environment: environment.into(),
            cluster: cluster.into(),
            member: member.into(),
            scopes,
            seats,
        })
    }

    fn validate(
        &self,
        grant: &pb::AuthGroupBootstrapGrant,
        incarnation: &str,
    ) -> Result<(), PlacementError> {
        Ulid::from_text(&grant.group_id).map_err(|_| PlacementError::Invalid)?;
        let scope = DatabaseRouteScope::try_new(&grant.tenant, &grant.project, &grant.database)
            .map_err(|_| PlacementError::Invalid)?;
        if grant.environment != self.environment
            || grant.cluster != self.cluster
            || !self.scopes.contains(&scope)
            || grant.generation != 1
            || grant.authority_revision == 0
            || grant.genesis_node == 0
            || grant.encoded_len() > 16 * 1024
            || grant.regions.is_empty()
            || grant.regions.len() > 3
            || grant.voters.len() != grant.regions.len() * 2
        {
            return Err(PlacementError::Invalid);
        }
        if !grant.regions.windows(2).all(|pair| pair[0] < pair[1])
            || !grant.voters.windows(2).all(|pair| pair[0].node_id < pair[1].node_id)
        {
            return Err(PlacementError::Invalid);
        }
        let mut counts: BTreeMap<&str, usize> =
            grant.regions.iter().map(|region| (region.as_str(), 0)).collect();
        let mut members = BTreeSet::new();
        let mut local = false;
        let mut genesis = false;
        for voter in &grant.voters {
            let expected =
                MemberWorkloadIdentity::try_new(&self.environment, &self.cluster, &voter.member)
                    .map_err(|_| PlacementError::Invalid)?;
            Ulid::from_text(&voter.storage_incarnation).map_err(|_| PlacementError::Invalid)?;
            if voter.node_id == 0
                || voter.spiffe_uri != expected.as_str()
                || self.seats.get(&voter.member) != Some(&voter.region)
                || !members.insert(&voter.member)
            {
                return Err(PlacementError::Invalid);
            }
            *counts.get_mut(voter.region.as_str()).ok_or(PlacementError::Invalid)? += 1;
            genesis |= voter.node_id == grant.genesis_node;
            if voter.member == self.member {
                if voter.storage_incarnation != incarnation {
                    return Err(PlacementError::IdentityMismatch);
                }
                local = true;
            }
        }
        if !local || !genesis || counts.values().any(|count| *count != 2) {
            return Err(PlacementError::Invalid);
        }
        Ok(())
    }
}

/// Owner-private SQLite journal of immutable, explicit auth genesis grants.
/// A new storage incarnation cannot accept a replay of a lost disk's grant.
pub struct AuthBootstrapJournal {
    connection: Mutex<Connection>,
    boundary: AuthBootstrapBoundary,
}

impl AuthBootstrapJournal {
    /// Open control state and create a new incarnation only for new storage.
    /// GRU must explicitly admit that incarnation before a grant is accepted.
    ///
    /// # Errors
    /// Refuses unsafe paths, changed host identity, corrupt state, or failed persistence.
    pub fn open(path: &Path, boundary: AuthBootstrapBoundary) -> Result<Self, PlacementError> {
        crate::placement::prepare_path(path)?;
        let mut connection = Connection::open(path).map_err(|_| PlacementError::Storage)?;
        connection
            .busy_timeout(std::time::Duration::from_secs(2))
            .map_err(|_| PlacementError::Storage)?;
        connection
            .execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;")
            .map_err(|_| PlacementError::Storage)?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| PlacementError::Storage)?;
        transaction.execute_batch("CREATE TABLE IF NOT EXISTS auth_identity(id INTEGER PRIMARY KEY CHECK(id=1),environment TEXT NOT NULL,cluster TEXT NOT NULL,member TEXT NOT NULL,incarnation TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS auth_grants(group_id TEXT PRIMARY KEY,tenant TEXT NOT NULL,project TEXT NOT NULL,database_id TEXT NOT NULL,principal TEXT NOT NULL,operation TEXT NOT NULL UNIQUE,grant BLOB NOT NULL,state_reserved INTEGER NOT NULL DEFAULT 0,UNIQUE(tenant,project,database_id));").map_err(|_|PlacementError::Storage)?;
        transaction
            .execute(
                "INSERT OR IGNORE INTO auth_identity VALUES(1,?1,?2,?3,?4)",
                params![
                    boundary.environment,
                    boundary.cluster,
                    boundary.member,
                    process_generator().generate().to_text()
                ],
            )
            .map_err(|_| PlacementError::Storage)?;
        let identity: (String, String, String) = transaction
            .query_row(
                "SELECT environment,cluster,member FROM auth_identity WHERE id=1",
                [],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
            )
            .map_err(|_| PlacementError::Storage)?;
        if identity
            != (boundary.environment.clone(), boundary.cluster.clone(), boundary.member.clone())
        {
            return Err(PlacementError::IdentityMismatch);
        }
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        std::fs::File::open(path.parent().ok_or(PlacementError::Storage)?)
            .and_then(|directory| directory.sync_all())
            .map_err(|_| PlacementError::Storage)?;
        let journal = Self { connection: Mutex::new(connection), boundary };
        journal.identity()?;
        Ok(journal)
    }

    /// Return this host's durable challenge for independent GRU voter admission.
    ///
    /// # Errors
    /// Refuses corrupt or unavailable control state.
    pub fn identity(&self) -> Result<pb::AuthStorageIdentity, PlacementError> {
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let incarnation: String = connection
            .query_row("SELECT incarnation FROM auth_identity WHERE id=1", [], |row| row.get(0))
            .map_err(|_| PlacementError::Storage)?;
        Ulid::from_text(&incarnation).map_err(|_| PlacementError::Storage)?;
        Ok(pb::AuthStorageIdentity {
            environment: self.boundary.environment.clone(),
            cluster: self.boundary.cluster.clone(),
            member: self.boundary.member.clone(),
            storage_incarnation: incarnation,
        })
    }

    /// Persist an immutable grant after the caller verifies the exact GRU mTLS principal.
    ///
    /// # Errors
    /// Refuses stale incarnation, placement drift, conflicting retries or group replacement.
    pub fn bootstrap(
        &self,
        principal: &str,
        request: pb::AuthGroupBootstrapRequest,
    ) -> Result<pb::AuthGroupBootstrapReceipt, PlacementError> {
        Ulid::from_text(&request.operation_id).map_err(|_| PlacementError::Invalid)?;
        if principal.is_empty() || principal.len() > 1024 || principal.chars().any(char::is_control)
        {
            return Err(PlacementError::Invalid);
        }
        let identity = self.identity()?;
        let grant = request.grant.ok_or(PlacementError::Invalid)?;
        self.boundary.validate(&grant, &identity.storage_incarnation)?;
        let encoded = grant.encode_to_vec();
        let mut connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| PlacementError::Storage)?;
        let prior:Option<(String,String,Vec<u8>)>=transaction.query_row("SELECT principal,operation,grant FROM auth_grants WHERE group_id=?1 OR operation=?2 OR (tenant=?3 AND project=?4 AND database_id=?5)",params![grant.group_id,request.operation_id,grant.tenant,grant.project,grant.database],|row|Ok((row.get(0)?,row.get(1)?,row.get(2)?))).optional().map_err(|_|PlacementError::Storage)?;
        if let Some((stored_principal, operation, stored)) = prior {
            if stored_principal != principal
                || operation != request.operation_id
                || stored != encoded
            {
                return Err(PlacementError::RetryMismatch);
            }
        } else {
            let count: u64 = transaction
                .query_row("SELECT COUNT(*) FROM auth_grants", [], |row| row.get(0))
                .map_err(|_| PlacementError::Storage)?;
            if count >= 1024 {
                return Err(PlacementError::Exhausted);
            }
            transaction.execute("INSERT INTO auth_grants(group_id,tenant,project,database_id,principal,operation,grant) VALUES(?1,?2,?3,?4,?5,?6,?7)",params![grant.group_id,grant.tenant,grant.project,grant.database,principal,request.operation_id,encoded]).map_err(|_|PlacementError::Storage)?;
        }
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        Ok(pb::AuthGroupBootstrapReceipt {
            group_id: grant.group_id.clone(),
            generation: grant.generation,
            authority_revision: grant.authority_revision,
            storage_incarnation: identity.storage_incarnation,
            status: pb::AuthGroupBootstrapStatus::Persisted as i32,
            grant: Some(grant),
            operation_id: request.operation_id,
        })
    }

    /// Reserve initial state-file creation once. A repeated call never permits recreation.
    /// Persist this before creating the Raft file; after interruption, require that file
    /// and its identity to exist, or refuse recovery and perform explicit voter replacement.
    ///
    /// # Errors
    /// Refuses an absent grant or unavailable durable state.
    pub fn reserve_state_creation(&self, group: &str) -> Result<bool, PlacementError> {
        let mut connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| PlacementError::Storage)?;
        let reserved: Option<bool> = transaction
            .query_row("SELECT state_reserved FROM auth_grants WHERE group_id=?1", [group], |row| {
                row.get(0)
            })
            .optional()
            .map_err(|_| PlacementError::Storage)?;
        let reserved = reserved.ok_or(PlacementError::NotFound)?;
        if !reserved {
            transaction
                .execute("UPDATE auth_grants SET state_reserved=1 WHERE group_id=?1", [group])
                .map_err(|_| PlacementError::Storage)?;
        }
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        Ok(!reserved)
    }
}

#[cfg(test)]
pub(crate) mod tests;
