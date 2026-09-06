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

use crate::proto::terrabase::v1 as pb;

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
#[derive(Clone)]
pub struct PlacementBoundary {
    environment: String,
    cluster: String,
    member: String,
    generation: u64,
    epoch: u64,
    databases: BTreeMap<DatabaseRouteScope, String>,
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
        databases: impl IntoIterator<Item = (DatabaseRouteScope, String)>,
    ) -> Result<Self, PlacementError> {
        MemberWorkloadIdentity::try_new(environment, cluster, member)
            .map_err(|_| PlacementError::Invalid)?;
        let mut scopes = BTreeMap::new();
        for (scope, region) in databases {
            RegionCatalog::production().parse(&region).map_err(|_| PlacementError::Invalid)?;
            if scopes.insert(scope, region).is_some() || scopes.len() > 1024 {
                return Err(PlacementError::Invalid);
            }
        }
        if scopes.is_empty() || generation == 0 || epoch == 0 {
            return Err(PlacementError::Invalid);
        }
        Ok(Self {
            environment: environment.into(),
            cluster: cluster.into(),
            member: member.into(),
            generation,
            epoch,
            databases: scopes,
        })
    }

    fn canonicalize(&self, proposal: &mut pb::PlacementProposal) -> Result<(), PlacementError> {
        if proposal.environment != self.environment
            || proposal.cluster != self.cluster
            || proposal.generation < self.generation
            || proposal.epoch < self.epoch
            || proposal.regions.is_empty()
            || proposal.regions.len() > 3
            || proposal.assignments.len() != self.databases.len()
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
        let mut scopes = BTreeSet::new();
        for assignment in &proposal.assignments {
            let scope = DatabaseRouteScope::try_new(
                &assignment.tenant,
                &assignment.project,
                &assignment.database,
            )
            .map_err(|_| PlacementError::Invalid)?;
            if self.databases.get(&scope) != Some(&assignment.region)
                || !scopes.insert(scope)
                || assignment.writer_member == assignment.follower_member
            {
                return Err(PlacementError::Invalid);
            }
            let pair = seats.get(&assignment.region).ok_or(PlacementError::Invalid)?;
            if !pair.contains(&assignment.writer_member)
                || !pair.contains(&assignment.follower_member)
                || !pair.contains(&self.member)
            {
                return Err(PlacementError::Invalid);
            }
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

/// One SQLite journal of pending control intent and durable retry receipts.
/// No API on this type can mark a proposal admitted or a database writable.
pub struct PlacementJournal {
    connection: Mutex<Connection>,
    boundary: PlacementBoundary,
}

impl PlacementJournal {
    /// Opens an owner-private SQLite journal and checks its persisted identity.
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
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        std::fs::File::open(path.parent().ok_or(PlacementError::Storage)?)
            .and_then(|file| file.sync_all())
            .map_err(|_| PlacementError::Storage)?;
        let journal = Self { connection: Mutex::new(connection), boundary };
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
        {
            return Err(PlacementError::Invalid);
        }
        let proposal = request.proposal.as_mut().ok_or(PlacementError::Invalid)?;
        self.boundary.canonicalize(proposal)?;
        let encoded = proposal.encode_to_vec();
        let expected =
            i64::try_from(request.expected_revision).map_err(|_| PlacementError::Conflict)?;
        let mut connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| PlacementError::Storage)?;
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
            return self.response(revision, &previous);
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
            let previous =
                self.response(revision, &bytes)?.proposal.ok_or(PlacementError::Storage)?;
            if proposal.generation <= previous.generation || proposal.epoch <= previous.epoch {
                return Err(PlacementError::Conflict);
            }
        }
        if revision >= MAX_PLACEMENT_PROPOSALS {
            return Err(PlacementError::Exhausted);
        }
        let next = revision + 1;
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
        self.response(next, &encoded)
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
        self.response(revision, &bytes)
    }

    fn response(
        &self,
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
        self.boundary.canonicalize(&mut proposal).map_err(|_| PlacementError::Storage)?;
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

fn prepare_path(path: &Path) -> Result<(), PlacementError> {
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
