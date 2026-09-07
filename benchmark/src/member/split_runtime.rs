//! Member-owned online split workers.
//!
//! The member owns worker lifetime and bounded scheduling.  Authentication,
//! placement, and split-key selection remain an injected control-plane
//! authority.  Durable routes are loaded from the engine before a worker is
//! admitted; this module never publishes an in-memory route by itself.

use std::collections::BTreeMap;

use crate::{
    engine::shard_supervisor::{ShardSplitSupervisor, SplitStep, SplitSupervisorError},
    shard_split::{ShardSplit, SplitContext, SplitTrigger},
    types::TenantId,
};

use super::{DatabaseId, Member, MemberError};

/// Authenticated and placement-validated facts for one split attempt.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SplitAdmission {
    /// The operation identity and placement epoch accepted by the authority.
    pub context: SplitContext,
    /// Inclusive lower bound of the moved range, when bounded.
    pub lower: Option<Vec<u8>>,
    /// Upper-exclusive split key selected by the authority.
    pub split_key: Vec<u8>,
}

/// Authority which authenticates an automatic split trigger and supplies its
/// placement-bound plan. Implementations must reject stale epochs and callers
/// without the member's current split permission.
pub trait SplitAdmissionAuthority: Send {
    /// Authority-specific refusal.
    type Error;

    /// Authenticates the trigger and returns a fully bound split plan.
    fn authorize_split(
        &mut self,
        database: &DatabaseId,
        trigger: SplitTrigger,
    ) -> Result<SplitAdmission, Self::Error>;
}

/// Errors while admitting or advancing a member split worker.
#[derive(Debug, Eq, PartialEq)]
pub enum MemberSplitError<AE, OE> {
    /// The catalog could not admit the worker's database lease.
    Member(MemberError),
    /// The authority refused authentication or placement.
    Authority(AE),
    /// The durable route catalog could not be loaded.
    Engine(crate::engine::EngineError),
    /// No persisted route catalog exists for the admitted database.
    MissingRoute,
    /// The production supervisor rejected a lifecycle or adapter operation.
    Supervisor(SplitSupervisorError<OE>),
    /// A worker for this database is already owned by the member.
    AlreadyRunning,
    /// The bounded worker registry is full.
    WorkerCapacity,
    /// The authority returned an invalid split plan.
    InvalidPlan,
}

/// Result of one bounded worker scheduler step.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MemberSplitStep {
    /// A bounded snapshot or journal page was applied.
    Progress,
    /// The durable route was cut over and the split completed.
    Complete {
        /// The durable routing generation published by the completed split.
        generation: u64,
    },
}

struct Worker<O: crate::shard_split::SplitOrchestratorAdapter> {
    supervisor: ShardSplitSupervisor<O>,
}

/// Bounded owner of online split workers for one member.
pub struct MemberSplitRuntime<O, A>
where
    O: crate::shard_split::SplitOrchestratorAdapter,
    A: SplitAdmissionAuthority,
{
    member: Member,
    authority: A,
    orchestrator: O,
    workers: BTreeMap<DatabaseId, Worker<O>>,
    max_workers: usize,
    copy_limit: usize,
    catchup_limit: u32,
}

impl<O, A> MemberSplitRuntime<O, A>
where
    O: crate::shard_split::SplitOrchestratorAdapter + Clone,
    A: SplitAdmissionAuthority,
{
    /// Creates a worker owner with explicit bounds for each scheduler step.
    pub fn new(
        member: Member,
        authority: A,
        orchestrator: O,
        max_workers: usize,
        copy_limit: usize,
        catchup_limit: u32,
    ) -> Result<Self, MemberSplitError<A::Error, O::Error>> {
        if max_workers == 0 || copy_limit == 0 || catchup_limit == 0 {
            return Err(MemberSplitError::WorkerCapacity);
        }
        Ok(Self {
            member,
            authority,
            orchestrator,
            workers: BTreeMap::new(),
            max_workers,
            copy_limit,
            catchup_limit,
        })
    }

    /// Starts a worker after authenticated authority admission and route load.
    pub fn start(
        &mut self,
        database: &DatabaseId,
        trigger: SplitTrigger,
    ) -> Result<(), MemberSplitError<A::Error, O::Error>> {
        if self.workers.contains_key(database) {
            return Err(MemberSplitError::AlreadyRunning);
        }
        if self.workers.len() >= self.max_workers {
            return Err(MemberSplitError::WorkerCapacity);
        }
        let admission = self
            .authority
            .authorize_split(database, trigger)
            .map_err(MemberSplitError::Authority)?;
        if admission.context.split_id() == 0
            || admission.context.database_incarnation() == 0
            || admission.split_key.is_empty()
        {
            return Err(MemberSplitError::InvalidPlan);
        }
        let lease = self.member.acquire_existing(database).map_err(MemberSplitError::Member)?;
        let engine = lease.shared_engine();
        let tenant = TenantId::try_new(&lease.database_owner().tenant().to_text())
            .map_err(|_| MemberSplitError::Engine(crate::engine::EngineError::InvalidDocument))?;
        let scope = engine.scope_for(&tenant);
        let router = engine
            .load_shard_router(&scope)
            .map_err(MemberSplitError::Engine)?
            .map(|(router, _)| router)
            .ok_or(MemberSplitError::MissingRoute)?;
        let split =
            ShardSplit::plan_with_context(admission.lower, admission.split_key, admission.context)
                .map_err(|_| {
                    MemberSplitError::Engine(crate::engine::EngineError::InvalidDocument)
                })?;
        let supervisor =
            ShardSplitSupervisor::start(engine, scope, split, self.orchestrator.clone(), router)
                .map_err(MemberSplitError::Supervisor)?;
        self.workers.insert(database.clone(), Worker { supervisor });
        Ok(())
    }

    /// Resumes a durable in-flight split after a member restart. A missing
    /// record is reported as `Ok(false)` and does not create a worker.
    pub fn resume(
        &mut self,
        database: &DatabaseId,
    ) -> Result<bool, MemberSplitError<A::Error, O::Error>> {
        if self.workers.contains_key(database) {
            return Err(MemberSplitError::AlreadyRunning);
        }
        if self.workers.len() >= self.max_workers {
            return Err(MemberSplitError::WorkerCapacity);
        }
        let lease = self.member.acquire_existing(database).map_err(MemberSplitError::Member)?;
        let engine = lease.shared_engine();
        let tenant = TenantId::try_new(&lease.database_owner().tenant().to_text())
            .map_err(|_| MemberSplitError::Engine(crate::engine::EngineError::InvalidDocument))?;
        let scope = engine.scope_for(&tenant);
        let router = engine
            .load_shard_router(&scope)
            .map_err(MemberSplitError::Engine)?
            .map(|(router, _)| router)
            .ok_or(MemberSplitError::MissingRoute)?;
        let worker = ShardSplitSupervisor::resume(engine, scope, self.orchestrator.clone(), router)
            .map_err(MemberSplitError::Supervisor)?;
        let Some(supervisor) = worker else { return Ok(false) };
        self.workers.insert(database.clone(), Worker { supervisor });
        Ok(true)
    }

    /// Advances one worker by one bounded copy or catch-up page.
    pub fn step(
        &mut self,
        database: &DatabaseId,
    ) -> Result<MemberSplitStep, MemberSplitError<A::Error, O::Error>> {
        let worker = self
            .workers
            .get_mut(database)
            .ok_or(MemberSplitError::Member(MemberError::UnknownDatabase(database.clone())))?;
        let result = worker
            .supervisor
            .step(self.copy_limit, self.catchup_limit)
            .map_err(MemberSplitError::Supervisor)?;
        let public_result = match result {
            SplitStep::Progress => MemberSplitStep::Progress,
            SplitStep::Complete { generation } => MemberSplitStep::Complete { generation },
        };
        if matches!(public_result, MemberSplitStep::Complete { .. }) {
            self.workers.remove(database);
        }
        Ok(public_result)
    }

    /// Advances every currently owned worker once, preserving bounded work.
    pub fn step_all(
        &mut self,
    ) -> Vec<(DatabaseId, Result<MemberSplitStep, MemberSplitError<A::Error, O::Error>>)> {
        let databases = self.workers.keys().cloned().collect::<Vec<_>>();
        databases
            .into_iter()
            .map(|database| {
                let result = self.step(&database);
                (database, result)
            })
            .collect()
    }

    /// Drops all worker ownership. Durable split state remains for restart.
    pub fn shutdown(&mut self) {
        self.workers.clear();
    }

    /// Returns whether a worker is currently owned for a database.
    #[must_use]
    pub fn is_running(&self, database: &DatabaseId) -> bool {
        self.workers.contains_key(database)
    }

    /// Loads the persisted serving router used by request routing. The
    /// returned generation is included in write admission tokens by the
    /// caller and is rechecked by the engine at commit time.
    pub fn router(
        &self,
        database: &DatabaseId,
    ) -> Result<crate::shard_split::ShardRouter, MemberSplitError<A::Error, O::Error>> {
        let lease = self.member.acquire_existing(database).map_err(MemberSplitError::Member)?;
        let tenant = TenantId::try_new(&lease.database_owner().tenant().to_text())
            .map_err(|_| MemberSplitError::Engine(crate::engine::EngineError::InvalidDocument))?;
        lease
            .load_shard_router(&lease.scope_for(&tenant))
            .map_err(MemberSplitError::Engine)?
            .map(|(router, _)| router)
            .ok_or(MemberSplitError::MissingRoute)
    }
}
