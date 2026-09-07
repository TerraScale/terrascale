//! Durable pending placement proposals received through authenticated control RPCs.
//!
//! A persisted proposal is intent, never accepted membership or writer authority.
//! ADR 0041 supplies peer lists through control gRPC; ADR 0044 requires catch-up
//! and reconciliation before serving. This journal preserves that distinction.

use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;
use std::sync::Mutex;

use prost::Message as _;
use rusqlite::{Connection, OptionalExtension as _, TransactionBehavior, params};
use terrabase::identity::MemberUlid;
use terrabase::regions::RegionCatalog;
use terrabase_cluster::production::{DatabaseRouteScope, MemberWorkloadIdentity};

use crate::lifecycle::LifecycleOperationGrant;
use crate::proto::terrabase::v1 as pb;

mod drain;

/// Maximum retained operation receipts before explicit lifecycle maintenance is required.
pub const MAX_PLACEMENT_PROPOSALS: u64 = 1024;
/// Maximum canonical proposal bytes, independently of the transport message bound.
pub const MAX_PLACEMENT_PROPOSAL_BYTES: usize = 256 * 1024;

/// A bounded, non-secret control-journal failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PlacementError {
    /// The proposal is malformed or violates the configured database/Region boundary.
    Invalid,
    /// The journal belongs to a different configured environment, cluster, or member.
    IdentityMismatch,
    /// The expected revision or proposed generation/epoch is stale or conflicting.
    Conflict,
    /// A retained retry identity was paired with different request bytes or principal.
    RetryMismatch,
    /// The durable journal could not be opened, read, or committed.
    Storage,
    /// The bounded durable receipt journal is full.
    Exhausted,
    /// No proposal has been persisted.
    NotFound,
}

impl std::fmt::Display for PlacementError {
    fn fmt(&self, out: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        out.write_str(match self {
            Self::Invalid => "invalid placement proposal",
            Self::IdentityMismatch => "placement journal identity mismatch",
            Self::Conflict => "placement proposal revision or epoch conflict",
            Self::RetryMismatch => "placement operation identity conflict",
            Self::Storage => "placement journal unavailable or corrupt",
            Self::Exhausted => "placement journal receipt bound reached",
            Self::NotFound => "no placement proposal",
        })
    }
}

impl std::error::Error for PlacementError {}

/// The immutable host admission boundary used to validate control intent.
type DeclarationCatalog = BTreeMap<DatabaseRouteScope, (Option<String>, BTreeSet<String>)>;

/// The immutable host admission boundary used to validate control intent.
#[derive(Clone)]
pub struct PlacementBoundary {
    environment: String,
    cluster: String,
    member: String,
    generation: u64,
    epoch: u64,
    databases: std::sync::Arc<std::sync::RwLock<DeclarationCatalog>>,
}

impl PlacementBoundary {
    /// Validates an exact host identity and its configured database Regions.
    ///
    /// # Errors
    /// Rejects malformed identities, repeated database scopes, or unsupported Regions.
    pub fn new(
        environment: &str,
        cluster: &str,
        member: &str,
        generation: u64,
        epoch: u64,
        databases: impl IntoIterator<Item = (DatabaseRouteScope, Option<String>, BTreeSet<String>)>,
    ) -> Result<Self, PlacementError> {
        MemberWorkloadIdentity::try_new(environment, cluster, member)
            .map_err(|_| PlacementError::Invalid)?;
        let mut scopes = BTreeMap::new();
        let mut database_ids = BTreeSet::new();
        for (scope, local_region, selected_regions) in databases {
            if let Some(local_region) = &local_region {
                RegionCatalog::production()
                    .parse(local_region)
                    .map_err(|_| PlacementError::Invalid)?;
            }
            if selected_regions.is_empty()
                || selected_regions.len() > 3
                || local_region.as_ref().is_some_and(|region| !selected_regions.contains(region))
                || selected_regions
                    .iter()
                    .any(|region| RegionCatalog::production().parse(region).is_err())
                || !database_ids.insert(scope.database())
                || scopes.insert(scope, (local_region, selected_regions)).is_some()
                || scopes.len() > 1024
            {
                return Err(PlacementError::Invalid);
            }
        }
        if generation == 0 || epoch == 0 {
            return Err(PlacementError::Invalid);
        }
        Ok(Self {
            environment: environment.into(),
            cluster: cluster.into(),
            member: member.into(),
            generation,
            epoch,
            databases: std::sync::Arc::new(std::sync::RwLock::new(scopes)),
        })
    }

    fn canonicalize_with(
        &self,
        proposal: &mut pb::PlacementProposal,
        require_declared_databases: bool,
        databases: &DeclarationCatalog,
    ) -> Result<(), PlacementError> {
        if proposal.environment != self.environment
            || proposal.cluster != self.cluster
            || proposal.generation == 0
            || proposal.epoch == 0
            || proposal.regions.is_empty()
            || proposal.regions.len() > 3
            || proposal.assignments.len() > databases.len() * 3
            || proposal.encoded_len() > MAX_PLACEMENT_PROPOSAL_BYTES
        {
            return Err(PlacementError::Invalid);
        }
        let mut seats = BTreeMap::new();
        let mut members = BTreeSet::new();
        for region in &mut proposal.regions {
            RegionCatalog::production()
                .parse(&region.region)
                .map_err(|_| PlacementError::Invalid)?;
            if region.members.len() != 2 {
                return Err(PlacementError::Invalid);
            }
            region.members.sort();
            for member in &region.members {
                MemberUlid::from_text(member).map_err(|_| PlacementError::Invalid)?;
                if !members.insert(member.clone()) {
                    return Err(PlacementError::Invalid);
                }
            }
            if seats.insert(region.region.clone(), region.members.clone()).is_some() {
                return Err(PlacementError::Invalid);
            }
        }
        if !members.contains(&self.member) {
            return Err(PlacementError::Invalid);
        }
        let mut assignments = BTreeSet::new();
        for assignment in &proposal.assignments {
            let scope = DatabaseRouteScope::try_new(
                &assignment.tenant,
                &assignment.project,
                &assignment.database,
            )
            .map_err(|_| PlacementError::Invalid)?;
            let local_region = databases.get(&scope).ok_or(PlacementError::Invalid)?.0.as_deref();
            if !assignments.insert((scope, assignment.region.clone()))
                || !seats.contains_key(&assignment.region)
                || assignment.writer_member == assignment.follower_member
            {
                return Err(PlacementError::Invalid);
            }
            let pair = seats.get(&assignment.region).ok_or(PlacementError::Invalid)?;
            if !pair.contains(&assignment.writer_member)
                || !pair.contains(&assignment.follower_member)
                || (local_region == Some(assignment.region.as_str())
                    && !pair.contains(&self.member))
            {
                return Err(PlacementError::Invalid);
            }
        }
        let mut assignment_regions: BTreeMap<DatabaseRouteScope, BTreeSet<String>> =
            BTreeMap::new();
        for (scope, region) in &assignments {
            assignment_regions.entry(*scope).or_default().insert(region.clone());
        }
        for (scope, regions) in &assignment_regions {
            let (_, selected_regions) = databases.get(scope).ok_or(PlacementError::Invalid)?;
            if regions != selected_regions {
                return Err(PlacementError::Invalid);
            }
        }
        if require_declared_databases
            && databases.keys().any(|scope| !assignment_regions.contains_key(scope))
        {
            return Err(PlacementError::Invalid);
        }
        proposal.regions.sort_by(|a, b| a.region.cmp(&b.region));
        proposal.assignments.sort_by(|a, b| {
            (&a.tenant, &a.project, &a.database, &a.region).cmp(&(
                &b.tenant,
                &b.project,
                &b.database,
                &b.region,
            ))
        });
        Ok(())
    }
}

fn read_declarations(connection: &Connection) -> Result<DeclarationCatalog, PlacementError> {
    let orphan: bool = connection.query_row(
        "SELECT EXISTS(SELECT 1 FROM selected_regions s WHERE NOT EXISTS(SELECT 1 FROM declared d WHERE d.tenant=s.tenant AND d.project=s.project AND d.database=s.database))",
        [], |row| row.get(0),
    ).map_err(|_| PlacementError::Storage)?;
    if orphan {
        return Err(PlacementError::Storage);
    }
    let mut statement = connection.prepare(
        "SELECT d.tenant,d.project,d.database,d.region,s.region FROM declared d LEFT JOIN selected_regions s ON d.tenant=s.tenant AND d.project=s.project AND d.database=s.database ORDER BY d.database,s.region",
    ).map_err(|_| PlacementError::Storage)?;
    let mut rows = statement.query([]).map_err(|_| PlacementError::Storage)?;
    let mut catalog = DeclarationCatalog::new();
    let mut identities = BTreeSet::new();
    while let Some(row) = rows.next().map_err(|_| PlacementError::Storage)? {
        let tenant: String = row.get(0).map_err(|_| PlacementError::Storage)?;
        let project: String = row.get(1).map_err(|_| PlacementError::Storage)?;
        let database: String = row.get(2).map_err(|_| PlacementError::Storage)?;
        let local: String = row.get(3).map_err(|_| PlacementError::Storage)?;
        let selected: String = row.get(4).map_err(|_| PlacementError::Storage)?;
        let scope = DatabaseRouteScope::try_new(&tenant, &project, &database)
            .map_err(|_| PlacementError::Storage)?;
        RegionCatalog::production().parse(&selected).map_err(|_| PlacementError::Storage)?;
        let local = if local.is_empty() {
            None
        } else {
            RegionCatalog::production().parse(&local).map_err(|_| PlacementError::Storage)?;
            Some(local)
        };
        if !catalog.contains_key(&scope)
            && (catalog.len() >= 1024 || !identities.insert(scope.database()))
        {
            return Err(PlacementError::Storage);
        }
        let entry = catalog.entry(scope).or_insert_with(|| (local.clone(), BTreeSet::new()));
        if entry.0 != local || !entry.1.insert(selected) || entry.1.len() > 3 {
            return Err(PlacementError::Storage);
        }
    }
    if catalog.values().any(|(local, selected)| {
        selected.is_empty() || local.as_ref().is_some_and(|region| !selected.contains(region))
    }) {
        return Err(PlacementError::Storage);
    }
    Ok(catalog)
}

fn insert_declaration(
    connection: &Connection,
    scope: DatabaseRouteScope,
    local: Option<&str>,
    selected: &BTreeSet<String>,
) -> Result<(), PlacementError> {
    connection
        .execute(
            "INSERT INTO declared VALUES(?1,?2,?3,?4)",
            params![
                scope.tenant().to_text(),
                scope.project().to_text(),
                scope.database().to_text(),
                local.unwrap_or("")
            ],
        )
        .map_err(|_| PlacementError::Storage)?;
    for region in selected {
        connection
            .execute(
                "INSERT INTO selected_regions VALUES(?1,?2,?3,?4)",
                params![
                    scope.tenant().to_text(),
                    scope.project().to_text(),
                    scope.database().to_text(),
                    region
                ],
            )
            .map_err(|_| PlacementError::Storage)?;
    }
    Ok(())
}

fn candidate_declarations(
    boundary: &PlacementBoundary,
    transaction: &rusqlite::Transaction<'_>,
    proposal: &mut pb::PlacementProposal,
    declarations: &[pb::PlacementDatabaseDeclaration],
) -> Result<(DeclarationCatalog, DeclarationCatalog), PlacementError> {
    let durable = read_declarations(transaction)?;
    let mut candidate = durable.clone();
    let mut provided = BTreeSet::new();
    let mut represented = BTreeSet::new();
    for assignment in &proposal.assignments {
        represented.insert(
            DatabaseRouteScope::try_new(
                &assignment.tenant,
                &assignment.project,
                &assignment.database,
            )
            .map_err(|_| PlacementError::Invalid)?,
        );
    }
    for declaration in declarations {
        let scope = DatabaseRouteScope::try_new(
            &declaration.tenant,
            &declaration.project,
            &declaration.database,
        )
        .map_err(|_| PlacementError::Invalid)?;
        let selected: BTreeSet<_> = declaration.selected_regions.iter().cloned().collect();
        if !provided.insert(scope.database())
            || !represented.contains(&scope)
            || selected.is_empty()
            || selected.len() > 3
            || selected.len() != declaration.selected_regions.len()
            || selected.iter().any(|region| RegionCatalog::production().parse(region).is_err())
        {
            return Err(PlacementError::Invalid);
        }
        if let Some((_, existing)) = candidate.get(&scope) {
            if existing != &selected {
                return Err(PlacementError::Conflict);
            }
        } else {
            if candidate.len() >= 1024
                || candidate.keys().any(|existing| existing.database() == scope.database())
            {
                return Err(PlacementError::Conflict);
            }
            candidate.insert(scope, (None, selected));
        }
    }
    // Declarations supply missing control metadata, never local admission.
    boundary.canonicalize_with(proposal, false, &candidate)?;
    Ok((durable, candidate))
}

fn reconcile_persisted_declarations(
    transaction: &rusqlite::Transaction<'_>,
    boundary: &PlacementBoundary,
) -> Result<(), PlacementError> {
    let mut scopes = boundary.databases.write().map_err(|_| PlacementError::Storage)?;
    let configured = scopes.clone();
    let mut database_ids: BTreeMap<_, DatabaseRouteScope> =
        configured.keys().map(|scope| (scope.database(), *scope)).collect();
    let mut persisted_declared = BTreeSet::new();
    let mut persisted_selected: BTreeMap<DatabaseRouteScope, BTreeSet<String>> = BTreeMap::new();
    let mut statement = transaction
        .prepare("SELECT tenant,project,database,region FROM declared")
        .map_err(|_| PlacementError::Storage)?;
    let mut rows = statement.query([]).map_err(|_| PlacementError::Storage)?;
    while let Some(row) = rows.next().map_err(|_| PlacementError::Storage)? {
        let tenant: String = row.get(0).map_err(|_| PlacementError::Storage)?;
        let project: String = row.get(1).map_err(|_| PlacementError::Storage)?;
        let database: String = row.get(2).map_err(|_| PlacementError::Storage)?;
        let region: String = row.get(3).map_err(|_| PlacementError::Storage)?;
        let scope = DatabaseRouteScope::try_new(&tenant, &project, &database)
            .map_err(|_| PlacementError::Storage)?;
        persisted_declared.insert(scope);
        let local_region = if region.is_empty() {
            None
        } else {
            RegionCatalog::production().parse(&region).map_err(|_| PlacementError::Storage)?;
            Some(region)
        };
        let selected = scopes.get(&scope).map(|(_, selected)| selected.clone()).unwrap_or_default();
        if scopes.get(&scope).is_some_and(|(existing, _)| {
            existing.as_ref().is_some_and(|region| Some(region) != local_region.as_ref())
        }) || (!scopes.contains_key(&scope) && scopes.len() >= 1024)
            || database_ids
                .insert(scope.database(), scope)
                .is_some_and(|existing| existing != scope)
        {
            return Err(PlacementError::Storage);
        }
        scopes.insert(scope, (local_region, selected));
    }
    drop(rows);
    drop(statement);
    let mut statement = transaction
        .prepare("SELECT tenant,project,database,region FROM selected_regions")
        .map_err(|_| PlacementError::Storage)?;
    let mut rows = statement.query([]).map_err(|_| PlacementError::Storage)?;
    while let Some(row) = rows.next().map_err(|_| PlacementError::Storage)? {
        let tenant: String = row.get(0).map_err(|_| PlacementError::Storage)?;
        let project: String = row.get(1).map_err(|_| PlacementError::Storage)?;
        let database: String = row.get(2).map_err(|_| PlacementError::Storage)?;
        let region: String = row.get(3).map_err(|_| PlacementError::Storage)?;
        let scope = DatabaseRouteScope::try_new(&tenant, &project, &database)
            .map_err(|_| PlacementError::Storage)?;
        if database_ids.get(&scope.database()) != Some(&scope) {
            return Err(PlacementError::Storage);
        }
        RegionCatalog::production().parse(&region).map_err(|_| PlacementError::Storage)?;
        persisted_selected.entry(scope).or_default().insert(region);
    }
    drop(rows);
    drop(statement);
    if persisted_selected.keys().any(|scope| !scopes.contains_key(scope)) {
        return Err(PlacementError::Storage);
    }
    persist_declarations(
        transaction,
        &mut scopes,
        &configured,
        &persisted_declared,
        &persisted_selected,
    )
}

fn persist_declarations(
    transaction: &rusqlite::Transaction<'_>,
    scopes: &mut DeclarationCatalog,
    configured: &DeclarationCatalog,
    persisted_declared: &BTreeSet<DatabaseRouteScope>,
    persisted_selected: &BTreeMap<DatabaseRouteScope, BTreeSet<String>>,
) -> Result<(), PlacementError> {
    for (scope, (local_region, selected_regions)) in scopes.iter_mut() {
        if persisted_declared.contains(scope) {
            let persisted = persisted_selected.get(scope).ok_or(PlacementError::Storage)?;
            if configured.get(scope).is_some_and(|(_, selected)| selected != persisted)
                || configured
                    .get(scope)
                    .and_then(|(local, _)| local.as_ref())
                    .is_some_and(|local| Some(local) != local_region.as_ref())
            {
                return Err(PlacementError::Storage);
            }
            selected_regions.clone_from(persisted);
        }
        if selected_regions.is_empty()
            || local_region.as_ref().is_some_and(|region| !selected_regions.contains(region))
            || selected_regions.len() > 3
        {
            return Err(PlacementError::Storage);
        }
        transaction
            .execute(
                "INSERT OR IGNORE INTO declared VALUES (?1,?2,?3,?4)",
                params![
                    scope.tenant().to_text(),
                    scope.project().to_text(),
                    scope.database().to_text(),
                    local_region.as_deref().unwrap_or("")
                ],
            )
            .map_err(|_| PlacementError::Storage)?;
        for region in selected_regions.iter() {
            transaction
                .execute(
                    "INSERT OR IGNORE INTO selected_regions VALUES (?1,?2,?3,?4)",
                    params![
                        scope.tenant().to_text(),
                        scope.project().to_text(),
                        scope.database().to_text(),
                        region
                    ],
                )
                .map_err(|_| PlacementError::Storage)?;
        }
    }
    Ok(())
}

/// One `SQLite` journal of pending control intent and durable retry receipts.
/// No API on this type can mark a proposal admitted or a database writable.
pub struct PlacementJournal {
    connection: Mutex<Connection>,
    boundary: PlacementBoundary,
    trust: Mutex<Option<transition::TransitionTrust>>,
}

impl PlacementJournal {
    /// Durably fences a lifecycle operation before its native effect.
    ///
    /// Returns `true` for a new operation and `false` for an exact retry.
    pub fn admit_lifecycle_operation(
        &self,
        grant: &LifecycleOperationGrant,
    ) -> Result<bool, PlacementError> {
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let transaction =
            connection.unchecked_transaction().map_err(|_| PlacementError::Storage)?;
        let current: Option<i64> = transaction
            .query_row("SELECT epoch FROM lifecycle_authority WHERE id=1", [], |row| row.get(0))
            .optional()
            .map_err(|_| PlacementError::Storage)?;
        if current.is_some_and(|epoch| i64::try_from(grant.authority_epoch).ok() < Some(epoch)) {
            return Err(PlacementError::Conflict);
        }
        let action = serde_json::to_string(&grant.action).map_err(|_| PlacementError::Storage)?;
        let revision = i64::try_from(grant.accepted_placement_revision)
            .map_err(|_| PlacementError::Invalid)?;
        let epoch = i64::try_from(grant.authority_epoch).map_err(|_| PlacementError::Invalid)?;
        let existing: Option<(String, String, String, String, String, String, String, i64, String, i64, String, String)> = transaction
            .query_row(
                "SELECT action,environment,tenant,project,database,region,member,placement_revision,proof_digest,authority_epoch,authority_lease,restore_request_digest FROM lifecycle_operations WHERE operation_id=?1",
                [grant.operation_id.as_str()],
                |row| Ok((row.get(0)?,row.get(1)?,row.get(2)?,row.get(3)?,row.get(4)?,row.get(5)?,row.get(6)?,row.get(7)?,row.get(8)?,row.get(9)?,row.get(10)?,row.get(11)?)),
            ).optional().map_err(|_| PlacementError::Storage)?;
        if let Some(row) = existing {
            let same = row.0 == action
                && row.1 == grant.environment
                && row.2 == grant.tenant
                && row.3 == grant.project
                && row.4 == grant.database
                && row.5 == grant.region
                && row.6 == grant.member
                && row.7 == revision
                && row.8 == grant.accepted_proof_digest
                && row.11 == grant.accepted_restore_request_digest;
            if !same {
                return Err(PlacementError::RetryMismatch);
            }
            transaction.execute("UPDATE lifecycle_operations SET authority_epoch=?2,authority_lease=?3,restore_request_digest=?4,issued_at=?5,expires_at=?6 WHERE operation_id=?1", params![grant.operation_id, epoch, grant.authority_lease, grant.accepted_restore_request_digest, grant.issued_at_unix_millis, grant.expires_at_unix_millis]).map_err(|_| PlacementError::Storage)?;
            transaction.execute("INSERT INTO lifecycle_authority(id,epoch) VALUES (1,?1) ON CONFLICT(id) DO UPDATE SET epoch=MAX(epoch,excluded.epoch)", params![epoch]).map_err(|_| PlacementError::Storage)?;
            transaction.commit().map_err(|_| PlacementError::Storage)?;
            return Ok(false);
        }
        transaction.execute("INSERT INTO lifecycle_operations VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15)", params![grant.operation_id, action, grant.environment, grant.tenant, grant.project, grant.database, grant.region, grant.member, i64::try_from(grant.accepted_placement_revision).map_err(|_| PlacementError::Invalid)?, grant.accepted_proof_digest, epoch, grant.authority_lease, grant.accepted_restore_request_digest, grant.issued_at_unix_millis, grant.expires_at_unix_millis]).map_err(|_| PlacementError::Conflict)?;
        transaction.execute("INSERT INTO lifecycle_authority(id,epoch) VALUES (1,?1) ON CONFLICT(id) DO UPDATE SET epoch=MAX(epoch,excluded.epoch)", params![epoch]).map_err(|_| PlacementError::Storage)?;
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        Ok(true)
    }

    /// Verifies retained signed authorization against the immutable admitted operation.
    /// Renewal may update the observation lease; the original signed mutation grant
    /// remains valid evidence for durable execution and is never rewritten here.
    pub fn verify_admitted_lifecycle_operation(
        &self,
        encoded: &[u8],
    ) -> Result<LifecycleOperationGrant, PlacementError> {
        let grant = self.verify_lifecycle_grant(encoded)?;
        // Admission already enforced wall-clock validity. Replay checks the original
        // signed structure without expiring an operation that was durably authorized.
        grant.validate(grant.issued_at_unix_millis).map_err(|_| PlacementError::Invalid)?;
        let action = serde_json::to_string(&grant.action).map_err(|_| PlacementError::Invalid)?;
        let revision = i64::try_from(grant.accepted_placement_revision)
            .map_err(|_| PlacementError::Invalid)?;
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let admitted: bool = connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM lifecycle_operations WHERE operation_id=?1 AND action=?2 AND environment=?3 AND tenant=?4 AND project=?5 AND database=?6 AND region=?7 AND member=?8 AND placement_revision=?9 AND proof_digest=?10 AND restore_request_digest=?11)",
            params![grant.operation_id, action, grant.environment, grant.tenant, grant.project,
                grant.database, grant.region, grant.member, revision, grant.accepted_proof_digest,
                grant.accepted_restore_request_digest],
            |row| row.get(0),
        ).map_err(|_| PlacementError::Storage)?;
        if !admitted {
            return Err(PlacementError::RetryMismatch);
        }
        Ok(grant)
    }

    /// Verifies one signed lifecycle grant with the retained authority pins.
    pub fn verify_lifecycle_grant(
        &self,
        encoded: &[u8],
    ) -> Result<crate::lifecycle::LifecycleOperationGrant, PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        trust.verify_lifecycle_grant(encoded)
    }

    /// Verifies that a lifecycle request carries the currently accepted GRU
    /// proof and names the exact local database/member proposal binding.
    pub fn verify_lifecycle_authority(
        &self,
        environment: &str,
        tenant: &str,
        project: &str,
        database: &str,
        region: &str,
        member: &str,
        revision: u64,
        _authority_epoch: u64,
        _authority_lease: &str,
        signature: &[u8],
    ) -> Result<(u64, String), PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let chain = self.verified_chain(&connection, trust)?;
        let (_, accepted_revision, accepted) = chain.last().ok_or(PlacementError::NotFound)?;
        if *accepted_revision != revision || environment.is_empty() {
            return Err(PlacementError::Conflict);
        }
        let supplied = trust
            .decode_with_prior(signature, chain.iter().rev().nth(1).map(|(_, _, proof)| proof))
            .map_err(|_| PlacementError::Conflict)?;
        if supplied.digest != accepted.digest
            || supplied.proof.authority_epoch != accepted.proof.authority_epoch
            || supplied.proof.lease_id != accepted.proof.lease_id
        {
            return Err(PlacementError::Conflict);
        }
        let bytes: Vec<u8> = connection
            .query_row(
                "SELECT proposal FROM proposals WHERE revision=?1",
                [i64::try_from(revision).map_err(|_| PlacementError::Invalid)?],
                |row| row.get(0),
            )
            .map_err(|_| PlacementError::Storage)?;
        let proposal =
            pb::PlacementProposal::decode(bytes.as_slice()).map_err(|_| PlacementError::Storage)?;
        if proposal.environment != environment
            || !proposal.assignments.iter().any(|item| {
                item.tenant == tenant
                    && item.project == project
                    && item.database == database
                    && item.region == region
                    && (item.writer_member == member || item.follower_member == member)
            })
        {
            return Err(PlacementError::Conflict);
        }
        Ok((accepted.proof.authority_epoch, accepted.proof.lease_id.clone()))
    }

    /// Opens an owner-private `SQLite` journal and checks its persisted identity.
    /// The parent directory must already exist with mode 0700.
    ///
    /// # Errors
    /// Rejects unsafe paths, invalid identity, corrupt schema, or unavailable storage.
    pub fn open(path: &Path, boundary: PlacementBoundary) -> Result<Self, PlacementError> {
        prepare_path(path)?;
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
        let version: i64 = transaction
            .query_row("PRAGMA user_version", [], |row| row.get(0))
            .map_err(|_| PlacementError::Storage)?;
        if version != 0 && version != 1 {
            return Err(PlacementError::Storage);
        }
        transaction.execute_batch(
            "CREATE TABLE IF NOT EXISTS identity (id INTEGER PRIMARY KEY CHECK(id=1), environment TEXT NOT NULL, cluster TEXT NOT NULL, member TEXT NOT NULL);
             CREATE TABLE IF NOT EXISTS proposals (revision INTEGER PRIMARY KEY, operation TEXT NOT NULL UNIQUE, principal TEXT NOT NULL, expected_revision INTEGER NOT NULL, proposal BLOB NOT NULL);
             CREATE TABLE IF NOT EXISTS accepted (sequence INTEGER PRIMARY KEY, proposal_revision INTEGER NOT NULL, proof BLOB NOT NULL, digest TEXT NOT NULL UNIQUE);
             CREATE TABLE IF NOT EXISTS transition_drains (prior_digest TEXT PRIMARY KEY, operation TEXT NOT NULL UNIQUE, prepared BLOB NOT NULL, prepared_digest TEXT NOT NULL);
             CREATE TABLE IF NOT EXISTS declared (tenant TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL PRIMARY KEY, region TEXT NOT NULL);
             CREATE TABLE IF NOT EXISTS selected_regions (tenant TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL, region TEXT NOT NULL, PRIMARY KEY(tenant,project,database,region));
             CREATE TABLE IF NOT EXISTS lifecycle_authority (id INTEGER PRIMARY KEY CHECK(id=1), epoch INTEGER NOT NULL);
             CREATE TABLE IF NOT EXISTS lifecycle_operations (operation_id TEXT PRIMARY KEY, action TEXT NOT NULL, environment TEXT NOT NULL, tenant TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL, region TEXT NOT NULL, member TEXT NOT NULL, placement_revision INTEGER NOT NULL, proof_digest TEXT NOT NULL, authority_epoch INTEGER NOT NULL, authority_lease TEXT NOT NULL, restore_request_digest TEXT NOT NULL, issued_at INTEGER NOT NULL, expires_at INTEGER NOT NULL);
             PRAGMA user_version=1;"
        ).map_err(|_| PlacementError::Storage)?;
        transaction
            .execute(
                "INSERT OR IGNORE INTO identity VALUES (1,?1,?2,?3)",
                params![boundary.environment, boundary.cluster, boundary.member],
            )
            .map_err(|_| PlacementError::Storage)?;
        let identity: (String, String, String) = transaction
            .query_row("SELECT environment,cluster,member FROM identity WHERE id=1", [], |row| {
                Ok((row.get(0)?, row.get(1)?, row.get(2)?))
            })
            .map_err(|_| PlacementError::Storage)?;
        if identity
            != (boundary.environment.clone(), boundary.cluster.clone(), boundary.member.clone())
        {
            return Err(PlacementError::IdentityMismatch);
        }
        reconcile_persisted_declarations(&transaction, &boundary)?;
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        std::fs::File::open(path.parent().ok_or(PlacementError::Storage)?)
            .and_then(|file| file.sync_all())
            .map_err(|_| PlacementError::Storage)?;
        let journal =
            Self { connection: Mutex::new(connection), boundary, trust: Mutex::new(None) };
        match journal.latest() {
            Ok(_) | Err(PlacementError::NotFound) => Ok(journal),
            Err(error) => Err(error),
        }
    }

    /// Persists one validated proposal using CAS and a durable operation identity.
    /// The caller must authenticate and authorize `principal` before this method.
    ///
    /// # Errors
    /// Rejects stale proposals, conflicting retries, boundary violations, or failed commits.
    pub fn stage(
        &self,
        principal: &str,
        operation: &str,
        mut request: pb::PlacementStageRequest,
    ) -> Result<pb::PlacementProposalResponse, PlacementError> {
        if principal.is_empty()
            || principal.len() > 128
            || operation.is_empty()
            || operation.len() > 128
            || principal.chars().any(char::is_control)
            || operation.chars().any(char::is_control)
            || request.declarations.len() > 1024
            || request.encoded_len() > MAX_PLACEMENT_PROPOSAL_BYTES * 2
        {
            return Err(PlacementError::Invalid);
        }
        let proposal = request.proposal.as_mut().ok_or(PlacementError::Invalid)?;
        let expected =
            i64::try_from(request.expected_revision).map_err(|_| PlacementError::Conflict)?;
        let mut connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| PlacementError::Storage)?;
        let (durable, candidate) =
            candidate_declarations(&self.boundary, &transaction, proposal, &request.declarations)?;
        let encoded = proposal.encode_to_vec();
        let retained: Option<(u64, String, i64, Vec<u8>)> = transaction.query_row(
            "SELECT revision,principal,expected_revision,proposal FROM proposals WHERE operation=?1", [operation],
            |row| Ok((row_revision(row)?, row.get(1)?, row.get(2)?, row.get(3)?)),
        ).optional().map_err(|_| PlacementError::Storage)?;
        if let Some((revision, previous_principal, previous_expected, previous)) = retained {
            if previous_principal != principal
                || previous_expected != expected
                || previous != encoded
            {
                return Err(PlacementError::RetryMismatch);
            }
            return self.response(&transaction, revision, &previous);
        }
        self.boundary.canonicalize_with(proposal, true, &candidate)?;
        if proposal.generation < self.boundary.generation || proposal.epoch < self.boundary.epoch {
            return Err(PlacementError::Conflict);
        }
        let head: Option<(u64, Vec<u8>)> = transaction
            .query_row(
                "SELECT revision,proposal FROM proposals ORDER BY revision DESC LIMIT 1",
                [],
                |row| Ok((row_revision(row)?, row.get(1)?)),
            )
            .optional()
            .map_err(|_| PlacementError::Storage)?;
        let revision = head.as_ref().map_or(0, |(revision, _)| *revision);
        if request.expected_revision != revision {
            return Err(PlacementError::Conflict);
        }
        if let Some((_, bytes)) = head {
            let previous = self
                .response(&transaction, revision, &bytes)?
                .proposal
                .ok_or(PlacementError::Storage)?;
            if proposal.generation <= previous.generation || proposal.epoch <= previous.epoch {
                return Err(PlacementError::Conflict);
            }
        }
        if revision >= MAX_PLACEMENT_PROPOSALS {
            return Err(PlacementError::Exhausted);
        }
        let next = revision + 1;
        for (scope, (local, selected)) in &candidate {
            if !durable.contains_key(scope) {
                insert_declaration(&transaction, *scope, local.as_deref(), selected)?;
            }
        }
        transaction
            .execute(
                "INSERT INTO proposals VALUES (?1,?2,?3,?4,?5)",
                params![
                    i64::try_from(next).map_err(|_| PlacementError::Exhausted)?,
                    operation,
                    principal,
                    expected,
                    encoded
                ],
            )
            .map_err(|_| PlacementError::Storage)?;
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        *self.boundary.databases.write().map_err(|_| PlacementError::Storage)? = candidate;
        self.response(&connection, next, &encoded)
    }

    /// Reads the latest persisted proposal; never infers admission from its presence.
    ///
    /// # Errors
    /// Returns `NotFound` before the first proposal, or a journal/boundary failure.
    pub fn latest(&self) -> Result<pb::PlacementProposalResponse, PlacementError> {
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let head: Option<(u64, Vec<u8>)> = connection
            .query_row(
                "SELECT revision,proposal FROM proposals ORDER BY revision DESC LIMIT 1",
                [],
                |row| Ok((row_revision(row)?, row.get(1)?)),
            )
            .optional()
            .map_err(|_| PlacementError::Storage)?;
        let (revision, bytes) = head.ok_or(PlacementError::NotFound)?;
        self.response(&connection, revision, &bytes)
    }

    fn response(
        &self,
        connection: &Connection,
        revision: u64,
        bytes: &[u8],
    ) -> Result<pb::PlacementProposalResponse, PlacementError> {
        if bytes.len() > MAX_PLACEMENT_PROPOSAL_BYTES
            || revision == 0
            || revision > MAX_PLACEMENT_PROPOSALS
        {
            return Err(PlacementError::Storage);
        }
        let mut proposal =
            pb::PlacementProposal::decode(bytes).map_err(|_| PlacementError::Storage)?;
        let catalog = read_declarations(connection)?;
        self.boundary
            .canonicalize_with(&mut proposal, false, &catalog)
            .map_err(|_| PlacementError::Storage)?;
        if proposal.encode_to_vec() != bytes {
            return Err(PlacementError::Storage);
        }
        Ok(pb::PlacementProposalResponse {
            revision,
            proposal: Some(proposal),
            status: pb::PlacementProposalStatus::Pending as i32,
        })
    }
}

pub(crate) fn prepare_path(path: &Path) -> Result<(), PlacementError> {
    use std::fs::{File, OpenOptions};
    if !path.is_absolute()
        || path.components().any(|part| {
            matches!(part, std::path::Component::ParentDir | std::path::Component::CurDir)
        })
    {
        return Err(PlacementError::Storage);
    }
    for ancestor in path.ancestors() {
        match std::fs::symlink_metadata(ancestor) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(PlacementError::Storage);
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound && ancestor == path => {}
            Err(_) => return Err(PlacementError::Storage),
        }
    }
    let parent = path.parent().ok_or(PlacementError::Storage)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::{MetadataExt as _, OpenOptionsExt as _};
        let metadata = std::fs::metadata(parent).map_err(|_| PlacementError::Storage)?;
        if !metadata.is_dir() || metadata.mode() & 0o777 != 0o700 {
            return Err(PlacementError::Storage);
        }
        let opened = OpenOptions::new().write(true).create_new(true).mode(0o600).open(path);
        match opened {
            Ok(file) => file.sync_all().map_err(|_| PlacementError::Storage)?,
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {}
            Err(_) => return Err(PlacementError::Storage),
        }
        let file = std::fs::symlink_metadata(path).map_err(|_| PlacementError::Storage)?;
        if !file.is_file() || file.mode() & 0o777 != 0o600 || file.uid() != metadata.uid() {
            return Err(PlacementError::Storage);
        }
    }
    #[cfg(not(unix))]
    {
        let file = OpenOptions::new()
            .write(true)
            .create(true)
            .truncate(false)
            .open(path)
            .map_err(|_| PlacementError::Storage)?;
        file.sync_all().map_err(|_| PlacementError::Storage)?;
    }
    File::open(parent).and_then(|file| file.sync_all()).map_err(|_| PlacementError::Storage)
}

fn row_revision(row: &rusqlite::Row<'_>) -> rusqlite::Result<u64> {
    let value: i64 = row.get(0)?;
    u64::try_from(value).map_err(|_| rusqlite::Error::IntegralValueOutOfRange(0, value))
}

#[cfg(test)]
pub(crate) mod tests;

pub mod transition;

impl PlacementJournal {
    /// Installs explicitly provisioned root proof pins and verifies the retained chain.
    /// Root pins cannot be replaced on a live journal. Member receipt inventories
    /// are admitted by signed accepted epochs and reconstructed from that chain.
    ///
    /// # Errors
    /// Rejects repeated configuration or a retained proof that fails validation.
    pub fn configure_transition_trust(
        &self,
        trust: transition::TransitionTrust,
    ) -> Result<(), PlacementError> {
        let mut configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        if configured.is_some() {
            return Err(PlacementError::Conflict);
        }
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        self.verified_chain(&connection, &trust)?;
        *configured = Some(trust);
        Ok(())
    }

    /// Verifies and commits an accepted epoch independently of control liveness.
    /// Catch-up and physical writer admission are still required before serving.
    ///
    /// # Errors
    /// Rejects unconfigured pins, wrong proposal/identity, signature, chain or fence proofs.
    pub fn accept(
        &self,
        operation: &str,
        request: &pb::PlacementAcceptRequest,
    ) -> Result<pb::PlacementAcceptanceResponse, PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        let mut connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| PlacementError::Storage)?;
        let chain = self.verified_chain(&transaction, trust)?;
        let predecessor = match chain
            .iter()
            .position(|(_, _, proof)| proof.proof.request.operation_id == operation)
        {
            Some(index) => index.checked_sub(1).map(|previous| &chain[previous].2),
            None => chain.last().map(|(_, _, proof)| proof),
        };
        let verified = trust.decode_with_prior(&request.proof, predecessor)?;
        if operation != verified.proof.request.operation_id {
            return Err(PlacementError::RetryMismatch);
        }
        if let Some((_, revision, prior)) =
            chain.iter().find(|(_, _, prior)| prior.proof.request.operation_id == operation)
        {
            if *revision != request.proposal_revision || prior.digest != verified.digest {
                return Err(PlacementError::RetryMismatch);
            }
            return Ok(acceptance_response(*revision, prior));
        }
        let latest_revision: u64 = transaction
            .query_row(
                "SELECT revision FROM proposals ORDER BY revision DESC LIMIT 1",
                [],
                row_revision,
            )
            .optional()
            .map_err(|_| PlacementError::Storage)?
            .ok_or(PlacementError::NotFound)?;
        if request.proposal_revision != latest_revision {
            return Err(PlacementError::Conflict);
        }
        self.match_proposal(&transaction, request.proposal_revision, &verified)?;
        verified.follows(chain.last().map(|(_, _, prior)| prior))?;
        drain::match_successor(
            &transaction,
            trust,
            chain.last().map(|(_, _, prior)| prior),
            &verified,
        )?;
        if chain.len() >= usize::try_from(MAX_PLACEMENT_PROPOSALS).unwrap_or(usize::MAX) {
            return Err(PlacementError::Exhausted);
        }
        transaction
            .execute(
                "INSERT INTO accepted VALUES (?1,?2,?3,?4)",
                params![
                    i64::try_from(chain.len() + 1).map_err(|_| PlacementError::Exhausted)?,
                    i64::try_from(request.proposal_revision)
                        .map_err(|_| PlacementError::Invalid)?,
                    request.proof,
                    verified.digest,
                ],
            )
            .map_err(|_| PlacementError::Storage)?;
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        Ok(acceptance_response(request.proposal_revision, &verified))
    }

    /// Returns retained membership acceptance without depending on current GRU availability.
    ///
    /// # Errors
    /// Rejects missing proofs or invalid retained chain.
    pub fn accepted(&self) -> Result<pb::PlacementAcceptanceResponse, PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let chain = self.verified_chain(&connection, trust)?;
        let (_, revision, proof) = chain.last().ok_or(PlacementError::NotFound)?;
        Ok(accepted_response(*revision, proof))
    }

    /// Returns the digest of the currently accepted signed placement proof.
    /// Lifecycle grants bind to this historical proof evidence.
    pub fn accepted_proof_digest(&self) -> Result<String, PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let chain = self.verified_chain(&connection, trust)?;
        let (_, _, proof) = chain.last().ok_or(PlacementError::NotFound)?;
        Ok(proof.digest.clone())
    }

    /// Returns the currently accepted authority pins from the validated chain.
    ///
    /// # Errors
    /// Fails when no accepted record exists or the retained chain is unavailable.
    pub fn accepted_authority(&self) -> Result<(u64, String, String), PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let chain = self.verified_chain(&connection, trust)?;
        let (_, _, proof) = chain.last().ok_or(PlacementError::NotFound)?;
        if proof.proof.authority_epoch == 0
            || proof.proof.lease_id.is_empty()
            || proof.digest.is_empty()
        {
            return Err(PlacementError::Invalid);
        }
        Ok((proof.proof.authority_epoch, proof.proof.lease_id.clone(), proof.digest.clone()))
    }

    /// Returns the digest of the canonical proposal backing the accepted proof.
    /// This is the placement binding used by data-plane readiness receipts.
    pub fn accepted_placement_digest(&self) -> Result<String, PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let chain = self.verified_chain(&connection, trust)?;
        let (_, revision, _) = chain.last().ok_or(PlacementError::NotFound)?;
        let bytes: Vec<u8> = connection
            .query_row(
                "SELECT proposal FROM proposals WHERE revision=?1",
                [i64::try_from(*revision).map_err(|_| PlacementError::Storage)?],
                |row| row.get(0),
            )
            .map_err(|_| PlacementError::Storage)?;
        Ok(transition::hash(&bytes))
    }

    fn verified_chain(
        &self,
        connection: &Connection,
        trust: &transition::TransitionTrust,
    ) -> Result<Vec<(u64, u64, transition::VerifiedProof)>, PlacementError> {
        let mut statement = connection
            .prepare(
                "SELECT sequence,proposal_revision,proof,digest FROM accepted ORDER BY sequence",
            )
            .map_err(|_| PlacementError::Storage)?;
        let mut rows = statement.query([]).map_err(|_| PlacementError::Storage)?;
        let mut chain: Vec<(u64, u64, transition::VerifiedProof)> = Vec::new();
        while let Some(row) = rows.next().map_err(|_| PlacementError::Storage)? {
            let sequence = row_revision(row).map_err(|_| PlacementError::Storage)?;
            let revision: i64 = row.get(1).map_err(|_| PlacementError::Storage)?;
            let revision = u64::try_from(revision).map_err(|_| PlacementError::Storage)?;
            let bytes: Vec<u8> = row.get(2).map_err(|_| PlacementError::Storage)?;
            let digest: String = row.get(3).map_err(|_| PlacementError::Storage)?;
            if chain.len() >= usize::try_from(MAX_PLACEMENT_PROPOSALS).unwrap_or(usize::MAX)
                || sequence != u64::try_from(chain.len()).unwrap_or(u64::MAX) + 1
            {
                return Err(PlacementError::Storage);
            }
            let verified =
                trust.decode_with_prior(&bytes, chain.last().map(|(_, _, prior)| prior))?;
            if digest != verified.digest {
                return Err(PlacementError::Storage);
            }
            self.match_proposal(connection, revision, &verified)?;
            verified.follows(chain.last().map(|(_, _, previous)| previous))?;
            drain::match_successor(
                connection,
                trust,
                chain.last().map(|(_, _, previous)| previous),
                &verified,
            )?;
            chain.push((sequence, revision, verified));
        }
        drain::validate_intents(connection, trust, &chain)?;
        Ok(chain)
    }

    fn match_proposal(
        &self,
        connection: &Connection,
        revision: u64,
        proof: &transition::VerifiedProof,
    ) -> Result<(), PlacementError> {
        let bytes: Vec<u8> = connection
            .query_row(
                "SELECT proposal FROM proposals WHERE revision=?1",
                [i64::try_from(revision).map_err(|_| PlacementError::Invalid)?],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| PlacementError::Storage)?
            .ok_or(PlacementError::NotFound)?;
        let proposal =
            self.response(connection, revision, &bytes)?.proposal.ok_or(PlacementError::Storage)?;
        if proof.proof.request.scope_id
            != format!("{}/{}", self.boundary.environment, self.boundary.cluster)
            || proof.proof.request.epoch != proposal.epoch
            || proof.proof.request.generation != proposal.generation
        {
            return Err(PlacementError::Invalid);
        }
        let mut expected = BTreeMap::new();
        for seats in &proposal.regions {
            for member in &seats.members {
                let identity = MemberWorkloadIdentity::try_new(
                    &self.boundary.environment,
                    &self.boundary.cluster,
                    member,
                )
                .map_err(|_| PlacementError::Invalid)?;
                expected.insert(identity.as_str().to_owned(), seats.region.clone());
            }
        }
        if proof.proof.request.members.len() != expected.len() {
            return Err(PlacementError::Invalid);
        }
        let local = MemberWorkloadIdentity::try_new(
            &self.boundary.environment,
            &self.boundary.cluster,
            &self.boundary.member,
        )
        .map_err(|_| PlacementError::Invalid)?;
        for member in &proof.proof.request.members {
            if expected.get(&member.identity) != Some(&member.region)
                || (member.identity == local.as_str()
                    && member.placement_digest != transition::hash(&bytes))
            {
                return Err(PlacementError::Invalid);
            }
        }
        Ok(())
    }
}

fn acceptance_response(
    revision: u64,
    proof: &transition::VerifiedProof,
) -> pb::PlacementAcceptanceResponse {
    pb::PlacementAcceptanceResponse {
        proposal_revision: revision,
        epoch: proof.proof.request.epoch,
        generation: proof.proof.request.generation,
        proof_digest: proof.digest.clone(),
        status: pb::PlacementAcceptanceStatus::AdmittedCatchingUp as i32,
    }
}

fn accepted_response(
    revision: u64,
    proof: &transition::VerifiedProof,
) -> pb::PlacementAcceptanceResponse {
    pb::PlacementAcceptanceResponse {
        proposal_revision: revision,
        epoch: proof.proof.request.epoch,
        generation: proof.proof.request.generation,
        proof_digest: proof.digest.clone(),
        status: pb::PlacementAcceptanceStatus::AdmittedCatchingUp as i32,
    }
}

impl terrabase_cluster::production::DurableMembershipSource for PlacementJournal {
    fn load_admitted_placement(
        &self,
    ) -> Result<
        terrabase_cluster::production::ProductionPlacement,
        terrabase_cluster::production::MembershipSourceError,
    > {
        use terrabase_cluster::production::{
            MembershipSourceError, ProductionPlacement, ProductionRegionSeats,
        };
        let observed = (|| -> Result<ProductionPlacement, PlacementError> {
            let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
            let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
            let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
            let chain = self.verified_chain(&connection, trust)?;
            let (_, revision, accepted) = chain.last().ok_or(PlacementError::NotFound)?;
            drain::refuse_draining(&connection, trust, accepted)?;
            let bytes: Vec<u8> = connection
                .query_row(
                    "SELECT proposal FROM proposals WHERE revision=?1",
                    [i64::try_from(*revision).map_err(|_| PlacementError::Storage)?],
                    |row| row.get(0),
                )
                .map_err(|_| PlacementError::Storage)?;
            let proposal = self
                .response(&connection, *revision, &bytes)?
                .proposal
                .ok_or(PlacementError::Storage)?;
            let regions = proposal
                .regions
                .into_iter()
                .map(|seats| {
                    let region = RegionCatalog::production()
                        .parse(&seats.region)
                        .map_err(|_| PlacementError::Invalid)?;
                    let members = [
                        MemberUlid::from_text(&seats.members[0])
                            .map_err(|_| PlacementError::Invalid)?,
                        MemberUlid::from_text(&seats.members[1])
                            .map_err(|_| PlacementError::Invalid)?,
                    ];
                    ProductionRegionSeats::try_new(region, members)
                        .map_err(|_| PlacementError::Invalid)
                })
                .collect::<Result<Vec<_>, _>>()?;
            ProductionPlacement::try_new(
                MemberUlid::from_text(&self.boundary.member)
                    .map_err(|_| PlacementError::Invalid)?,
                proposal.generation,
                proposal.epoch,
                regions,
            )
            .map_err(|_| PlacementError::Invalid)
        })();
        observed.map_err(|error| match error {
            PlacementError::Storage => MembershipSourceError::Unavailable,
            _ => MembershipSourceError::InvalidProof,
        })
    }
}

impl terrabase_cluster::wal_route::DurableWalAssignmentSource for PlacementJournal {
    fn load_assignment(
        &self,
        scope: DatabaseRouteScope,
    ) -> Result<
        terrabase_cluster::wal_route::WalAssignment,
        terrabase_cluster::wal_route::WalAssignmentError,
    > {
        use terrabase_cluster::wal_route::{WalAssignment, WalAssignmentError};
        let observed = (|| -> Result<WalAssignment, PlacementError> {
            let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
            let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
            let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
            let chain = self.verified_chain(&connection, trust)?;
            let (_, revision, accepted) = chain.last().ok_or(PlacementError::NotFound)?;
            drain::refuse_draining(&connection, trust, accepted)?;
            let bytes: Vec<u8> = connection
                .query_row(
                    "SELECT proposal FROM proposals WHERE revision=?1",
                    [i64::try_from(*revision).map_err(|_| PlacementError::Storage)?],
                    |row| row.get(0),
                )
                .map_err(|_| PlacementError::Storage)?;
            let proposal = self
                .response(&connection, *revision, &bytes)?
                .proposal
                .ok_or(PlacementError::Storage)?;
            let local_region = read_declarations(&connection)?
                .get(&scope)
                .cloned()
                .map(|(local_region, _)| local_region)
                .ok_or(PlacementError::Invalid)?;
            let local_region = local_region.ok_or(PlacementError::Invalid)?;
            let assignment = proposal
                .assignments
                .iter()
                .find(|entry| {
                    entry.tenant == scope.tenant().to_text()
                        && entry.project == scope.project().to_text()
                        && entry.database == scope.database().to_text()
                        && entry.region == local_region
                })
                .ok_or(PlacementError::Invalid)?;
            Ok(WalAssignment {
                scope,
                generation: proposal.generation,
                epoch: proposal.epoch,
                writer: MemberWorkloadIdentity::try_new(
                    &proposal.environment,
                    &proposal.cluster,
                    &assignment.writer_member,
                )
                .map_err(|_| PlacementError::Invalid)?,
                follower: MemberWorkloadIdentity::try_new(
                    &proposal.environment,
                    &proposal.cluster,
                    &assignment.follower_member,
                )
                .map_err(|_| PlacementError::Invalid)?,
            })
        })();
        observed.map_err(|error| match error {
            PlacementError::Storage => WalAssignmentError::Unavailable,
            _ => WalAssignmentError::InvalidProof,
        })
    }
}

#[cfg(test)]
mod drain_tests;
#[cfg(test)]
mod transition_tests;

#[cfg(test)]
mod declaration_tests;

impl PlacementJournal {
    /// Registers a durably installed CLOSED database scope for future proposals.
    /// The authenticated admission receiver calls this only after persisting the
    /// immutable provisioning tuple and installing its encrypted Member entry.
    /// This does not grant membership, a writer role, or public access.
    ///
    /// # Errors
    /// Rejects changed identity/Region set, bounds, and failed durable registration.
    pub fn register_database(
        &self,
        scope: DatabaseRouteScope,
        local_region: &str,
        selected_regions: &BTreeSet<String>,
    ) -> Result<(), PlacementError> {
        RegionCatalog::production().parse(local_region).map_err(|_| PlacementError::Invalid)?;
        if selected_regions.is_empty()
            || selected_regions.len() > 3
            || !selected_regions.contains(local_region)
            || selected_regions
                .iter()
                .any(|region| RegionCatalog::production().parse(region).is_err())
        {
            return Err(PlacementError::Invalid);
        }
        let mut connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| PlacementError::Storage)?;
        let mut scopes = read_declarations(&transaction)?;
        if let Some((local, selected)) = scopes.get(&scope) {
            if selected != selected_regions
                || local.as_deref().is_some_and(|existing| existing != local_region)
            {
                return Err(PlacementError::Conflict);
            }
            if local.is_none() {
                let changed = transaction.execute(
                    "UPDATE declared SET region=?1 WHERE tenant=?2 AND project=?3 AND database=?4 AND region=''",
                    params![local_region, scope.tenant().to_text(), scope.project().to_text(), scope.database().to_text()],
                ).map_err(|_| PlacementError::Storage)?;
                if changed != 1 {
                    return Err(PlacementError::Conflict);
                }
            }
        } else {
            if scopes.len() >= 1024
                || scopes.keys().any(|existing| existing.database() == scope.database())
            {
                return Err(PlacementError::Conflict);
            }
            insert_declaration(&transaction, scope, Some(local_region), selected_regions)?;
        }
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        scopes.insert(scope, (Some(local_region.to_owned()), selected_regions.clone()));
        *self.boundary.databases.write().map_err(|_| PlacementError::Storage)? = scopes;
        Ok(())
    }
}

impl PlacementJournal {
    /// Loads the verified accepted proposal, independently of newer pending intent.
    ///
    /// # Errors
    /// Refuses absent or invalid accepted authority and unavailable durable storage.
    pub fn accepted_proposal(&self) -> Result<pb::PlacementProposalResponse, PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let chain = self.verified_chain(&connection, trust)?;
        let (_, revision, _) = chain.last().ok_or(PlacementError::NotFound)?;
        let bytes: Vec<u8> = connection
            .query_row(
                "SELECT proposal FROM proposals WHERE revision=?1",
                [i64::try_from(*revision).map_err(|_| PlacementError::Storage)?],
                |row| row.get(0),
            )
            .map_err(|_| PlacementError::Storage)?;
        self.response(&connection, *revision, &bytes)
    }
}
