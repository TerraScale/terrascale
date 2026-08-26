//! Sharded query fan-out and reduction (SH-04, PB-4036, ADR 0011).
//!
//! A fanned-out query is routed, executed per shard, and reduced:
//!
//! - **Routing** decides which shards own the bucket ranges a query can
//!   match. Every seated shard by default; exactly one shard when the
//!   predicate equals every field of the collection's declared field
//!   [`Shard Key`](crate::sharding::ShardKeyKind), because that equality
//!   pins one bucket and one bucket belongs to one shard.
//! - **Per-shard execution** runs the existing query seam unchanged: each
//!   shard's serving endpoint compiles the query's clauses through
//!   [`QueryStore::compile`](crate::query::QueryStore::compile) and executes
//!   the resulting plan against its own snapshot with
//!   [`QueryStore::execute`](crate::query::QueryStore::execute). The clauses
//!   are the caller's clauses; only the page bounds differ, because a
//!   fan-out must fetch a window from every shard before it can know the
//!   global page.
//! - **Reduction** merges the per-shard pages under the seam's own ordering
//!   comparison, tie-breaks equal keys by the storage canonical key order
//!   the single-shard path's stable sort preserves, applies the caller's
//!   offset and limit once over the merged order, and composes the
//!   per-shard staleness observations honestly: the strictest shard's
//!   observed requirement and the least advanced shard's basis, never a
//!   more permissive answer than the strictest shard's.
//!
//! Failures are never partially answered: a routed shard without a snapshot,
//! a snapshot of another tenant, an unseated Member, or one shard's refusal
//! fails the whole fan-out with the shard named.
//!
//! TQL composes because lowering produces the typed specification this
//! executor executes: a TQL query lowered with
//! [`lower_to_spec`](crate::query::tql::lower_to_spec) fans out over the same
//! clauses the direct execution compiles (PD-2026-08-26-08).
//!
//! Boundaries: the Platform owns which Member serves which shard and where
//! that serving view lives; this module owns routing, per-shard execution,
//! reduction, and the composed staleness vocabulary. No performance fact is
//! claimed or implied by anything here.

use std::cmp::Ordering;

use crate::engine::Engine;
use crate::operations::{QueryInput, RequestValidationError};
use crate::query::{
    self, IndexGenerationRef, IndexNamespace, MAX_PAGE_LIMIT, ObservedGeneration, QueryError,
    QueryOrdering, QueryPaging, QueryPlan, QueryResult, QueryRow, QuerySpec, StaleIndexObservation,
    StalenessReport,
};
use crate::replication::MemberId;
use crate::types::{Document, ReadSnapshot};

use super::{Bucket, ShardId, ShardKeyKind, ShardTopology, ShardingError};

// ---------------------------------------------------------------------------
// Serving endpoints
// ---------------------------------------------------------------------------

/// One shard's serving endpoint as a fanned-out query reaches it: the
/// existing compile/execute query seam, scoped to the Member that owns the
/// shard's write path.
///
/// The trait is the seam's engine-independent face: an in-process Engine
/// implements it through [`MemberServing`], and a later remote serving view
/// implements the same two operations without the fan-out knowing the
/// difference.
pub trait ShardQueryServing {
    /// Compiles one typed query specification into a plan at this endpoint.
    ///
    /// # Errors
    ///
    /// Returns the endpoint's own refusal, which always names the Member
    /// when no endpoint is seated for it.
    fn compile(&self, owner: &MemberId, input: QueryInput) -> Result<QueryPlan, ShardServingError>;

    /// Executes one compiled plan against this shard's serving snapshot.
    ///
    /// # Errors
    ///
    /// Returns the endpoint's own refusal, which always names the Member
    /// when no endpoint is seated for it.
    fn execute(
        &self,
        owner: &MemberId,
        plan: &QueryPlan,
        snapshot: &ReadSnapshot,
    ) -> Result<QueryResult, ShardServingError>;
}

/// One shard endpoint's refusal during a fanned-out operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ShardServingError {
    /// The topology seats a shard whose owning Member this serving set has no
    /// endpoint for. The fan-out fails closed rather than skipping the shard.
    MemberUnserved {
        /// The Member with no endpoint.
        owner: MemberId,
    },
    /// The query seam refused the request at this endpoint.
    Query(QueryError),
    /// The index catalog at this endpoint refused a sharded index operation.
    Index(super::indexing::ShardIndexError),
}

impl From<QueryError> for ShardServingError {
    fn from(error: QueryError) -> Self {
        Self::Query(error)
    }
}

/// The member-seated serving set of one sharded topology: each owning
/// Member's Engine endpoint, in-process.
///
/// This is the engine-side serving shape of [`ShardTopology`]: the topology
/// names the Member that owns each shard, and the serving set names the
/// Engine that Member serves from. A shard whose owner has no endpoint is
/// refused at use, never skipped.
pub struct MemberServing<'engine> {
    endpoints: Vec<(MemberId, &'engine Engine)>,
}

impl<'engine> MemberServing<'engine> {
    /// Seats one Engine endpoint per Member.
    ///
    /// # Errors
    ///
    /// Returns [`ShardedExecutionError::DuplicateMember`] when one Member is
    /// seated twice.
    pub fn try_new(
        endpoints: Vec<(MemberId, &'engine Engine)>,
    ) -> Result<Self, ShardedExecutionError> {
        for (index, (member, _)) in endpoints.iter().enumerate() {
            if endpoints[..index].iter().any(|(seen, _)| seen == member) {
                return Err(ShardedExecutionError::DuplicateMember { member: member.clone() });
            }
        }
        Ok(Self { endpoints })
    }

    /// Returns the Engine one Member serves from.
    ///
    /// # Errors
    ///
    /// Returns [`ShardServingError::MemberUnserved`] when no endpoint is
    /// seated for the Member.
    pub fn engine_of(&self, owner: &MemberId) -> Result<&'engine Engine, ShardServingError> {
        self.endpoints
            .iter()
            .find(|(seated, _)| seated == owner)
            .map(|(_, engine)| *engine)
            .ok_or(ShardServingError::MemberUnserved { owner: owner.clone() })
    }
}

impl ShardQueryServing for MemberServing<'_> {
    fn compile(&self, owner: &MemberId, input: QueryInput) -> Result<QueryPlan, ShardServingError> {
        self.engine_of(owner)?.queries().compile(input).map_err(ShardServingError::from)
    }

    fn execute(
        &self,
        owner: &MemberId,
        plan: &QueryPlan,
        snapshot: &ReadSnapshot,
    ) -> Result<QueryResult, ShardServingError> {
        self.engine_of(owner)?.queries().execute(plan, snapshot).map_err(ShardServingError::from)
    }
}

// ---------------------------------------------------------------------------
// Typed failures
// ---------------------------------------------------------------------------

/// Typed failures of sharded query and index execution.
///
/// Every case is typed and carries no document value, storage detail, path,
/// or private implementation type, and every case fails the whole operation:
/// a fanned-out answer is never partially served.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ShardedExecutionError {
    /// Routing the operation over the seated topology failed.
    Routing(ShardingError),
    /// The serving set seats one Member twice.
    DuplicateMember {
        /// The twice-seated Member.
        member: MemberId,
    },
    /// The snapshot set carries one shard twice.
    DuplicateSnapshot {
        /// The twice-supplied shard.
        shard: ShardId,
    },
    /// A routed shard has no snapshot in the supplied set.
    SnapshotMissing {
        /// The shard without a snapshot.
        shard: ShardId,
    },
    /// A routed shard's snapshot names another tenant than the query's claim.
    SnapshotTenantMismatch {
        /// The shard whose snapshot names another tenant.
        shard: ShardId,
    },
    /// Building one per-shard page of the query was refused by request
    /// validation, most plausibly a deadline or cancellation that expired
    /// while the fan-out was still fetching.
    InvalidPage(RequestValidationError),
    /// One shard's query endpoint refused the query. The fan-out fails
    /// closed instead of answering from the surviving shards.
    QueryShard {
        /// The shard whose endpoint refused the query.
        shard: ShardId,
        /// The endpoint's refusal.
        error: ShardServingError,
    },
    /// One shard's index endpoint refused a sharded index operation. The
    /// operation fails closed instead of reporting the surviving shards as
    /// the whole table's state.
    IndexShard {
        /// The shard whose endpoint refused the operation.
        shard: ShardId,
        /// The endpoint's refusal.
        error: ShardServingError,
    },
}

// ---------------------------------------------------------------------------
// Snapshots
// ---------------------------------------------------------------------------

/// The per-shard read snapshots one fanned-out query executes against.
///
/// Snapshot acquisition stays with the caller, which mirrors the Platform's
/// production role: the fan-out never invents a serving view, it executes
/// against the one it is handed.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ShardSnapshots {
    entries: Vec<(ShardId, ReadSnapshot)>,
}

impl ShardSnapshots {
    /// Collects one snapshot per shard.
    ///
    /// # Errors
    ///
    /// Returns [`ShardedExecutionError::DuplicateSnapshot`] when one shard is
    /// supplied twice.
    pub fn try_new(entries: Vec<(ShardId, ReadSnapshot)>) -> Result<Self, ShardedExecutionError> {
        for (index, (shard, _)) in entries.iter().enumerate() {
            if entries[..index].iter().any(|(seen, _)| seen == shard) {
                return Err(ShardedExecutionError::DuplicateSnapshot { shard: *shard });
            }
        }
        Ok(Self { entries })
    }

    /// Returns the snapshot one shard executes against, when supplied.
    #[must_use]
    pub fn of(&self, shard: ShardId) -> Option<&ReadSnapshot> {
        self.entries.iter().find(|(seated, _)| *seated == shard).map(|(_, snapshot)| snapshot)
    }

    /// Returns the shards this set carries, in the order supplied.
    #[must_use]
    pub fn shards(&self) -> Vec<ShardId> {
        self.entries.iter().map(|(shard, _)| *shard).collect()
    }
}

// ---------------------------------------------------------------------------
// Routing
// ---------------------------------------------------------------------------

/// The routing outcome of one query: the shards it must reach, each with the
/// Member that owns it at the epoch the write path would validate against.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct QueryRouting {
    routes: Vec<ShardRoute>,
}

impl QueryRouting {
    fn new(routes: Vec<ShardRoute>) -> Self {
        Self { routes }
    }

    /// Returns every route, in the seated table's canonical shard order.
    #[must_use]
    pub fn routes(&self) -> &[ShardRoute] {
        &self.routes
    }

    /// Returns the routed shards, in canonical order.
    #[must_use]
    pub fn shards(&self) -> Vec<ShardId> {
        self.routes.iter().map(ShardRoute::shard).collect()
    }

    /// Returns the bucket a Shard Key equality predicate pinned, when one
    /// did. `None` means the query fans out over every seated shard.
    #[must_use]
    pub fn pinned_bucket(&self) -> Option<Bucket> {
        self.routes.first().and_then(|route| match route.reason() {
            RouteReason::ShardKeyPinned { bucket } => Some(*bucket),
            RouteReason::FanOut => None,
        })
    }
}

/// One routed shard: the shard, its owning Member, its ownership epoch, and
/// why the query reaches it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardRoute {
    shard: ShardId,
    owner: MemberId,
    epoch: u64,
    reason: RouteReason,
}

impl ShardRoute {
    fn new(shard: ShardId, owner: MemberId, epoch: u64, reason: RouteReason) -> Self {
        Self { shard, owner, epoch, reason }
    }

    /// Returns the routed shard.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the Member that owns the shard's write path.
    #[must_use]
    pub fn owner(&self) -> &MemberId {
        &self.owner
    }

    /// Returns the ownership epoch the route names.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns why the query reaches this shard.
    #[must_use]
    pub const fn reason(&self) -> &RouteReason {
        &self.reason
    }
}

/// Why one query reaches one shard.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RouteReason {
    /// The query's predicate does not pin the Shard Key, so every seated
    /// shard owns bucket ranges the query can match.
    FanOut,
    /// The predicate equals every declared Shard Key field, so one bucket —
    /// and the one shard that owns it — holds every possible match.
    ShardKeyPinned {
        /// The bucket the equality predicate pinned.
        bucket: Bucket,
    },
}

// ---------------------------------------------------------------------------
// The fan-out executor
// ---------------------------------------------------------------------------

/// The fanned-out query executor over one seated shard topology.
///
/// The executor owns routing, per-shard page windows, reduction, and
/// staleness composition. It owns no data, no Member, and no snapshot: the
/// caller supplies the serving set and the per-shard snapshots.
#[derive(Clone, Debug)]
pub struct ShardedQueries {
    topology: ShardTopology,
    page_limit: u32,
}

impl ShardedQueries {
    /// Builds the executor over one seated topology with the seam's own
    /// maximum page as the per-shard page bound.
    #[must_use]
    pub fn new(topology: ShardTopology) -> Self {
        Self { topology, page_limit: MAX_PAGE_LIMIT }
    }

    /// Sets the per-shard page bound, the number of rows one shard serves
    /// per page before the fan-out fetches the next page from it.
    ///
    /// The bound is an execution detail of the fan-out, never a query
    /// contract: every bound produces the same reduced result, which the
    /// equivalence tests hold.
    ///
    /// # Errors
    ///
    /// Returns [`ShardedExecutionError::InvalidPage`] for a bound of zero or
    /// above the seam's [`MAX_PAGE_LIMIT`], because no per-shard page may ask
    /// the query seam for an out-of-bound page.
    pub fn try_with_page_limit(mut self, page_limit: u32) -> Result<Self, ShardedExecutionError> {
        if page_limit == 0 || page_limit > MAX_PAGE_LIMIT {
            return Err(ShardedExecutionError::InvalidPage(RequestValidationError::InvalidQuery));
        }
        self.page_limit = page_limit;
        Ok(self)
    }

    /// Returns the seated topology this executor routes over.
    #[must_use]
    pub fn topology(&self) -> &ShardTopology {
        &self.topology
    }

    /// Routes one query: the shards it must reach and why.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::ShardWithoutOwner`] when the seated pair
    /// stopped covering a shard the table maps buckets to.
    pub fn route(&self, spec: &QuerySpec) -> Result<QueryRouting, ShardingError> {
        let pin = self.pinned_bucket(spec);
        let mut routes = Vec::new();
        for shard in self.topology.table().shards() {
            if let Some(pinned) = pin
                && shard != self.topology.table().shard_of(pinned)
            {
                continue;
            }
            let owner =
                self.topology.owner_of(shard).ok_or(ShardingError::ShardWithoutOwner { shard })?;
            let epoch =
                self.topology.epoch_of(shard).ok_or(ShardingError::ShardWithoutOwner { shard })?;
            let reason = match pin {
                Some(bucket) => RouteReason::ShardKeyPinned { bucket },
                None => RouteReason::FanOut,
            };
            routes.push(ShardRoute::new(shard, owner, epoch, reason));
        }
        Ok(QueryRouting::new(routes))
    }

    /// The bucket the query's predicate pins, when it equals every declared
    /// Shard Key field of the queried collection.
    ///
    /// The equality literals are resolved through the same Shard Key
    /// rendering a document routes by, so a pinned query and a routed
    /// document with those field values always agree. A collection that is
    /// identity-keyed, a predicate that misses one declared field, a
    /// non-equality condition, or a literal the Shard Key cannot render
    /// yields no pin: the query fans out rather than prune to a wrong shard.
    fn pinned_bucket(&self, spec: &QuerySpec) -> Option<Bucket> {
        let collection = spec.source().collection();
        let ShardKeyKind::Fields(fields) = self.topology.shard_keys().declaration_for(collection)
        else {
            return None;
        };
        let mut pinned = std::collections::BTreeMap::new();
        for field in fields {
            let value =
                spec.predicate().conditions().iter().find_map(|condition| match condition {
                    crate::query::QueryCondition::Equals { field: name, value }
                        if name == field.as_str() =>
                    {
                        Some(value.clone())
                    }
                    _ => None,
                })?;
            pinned.insert(field.as_str().to_owned(), value);
        }
        // The field-keyed resolution never reads the identity, so the pin
        // passes a placeholder identity through the same resolution path.
        let placeholder = crate::types::DocumentId::try_new("shard-key/pin").ok()?;
        let key = self
            .topology
            .shard_keys()
            .resolve(collection, &placeholder, Some(&crate::types::DocumentValue::Object(pinned)))
            .ok()?;
        Some(key.bucket())
    }

    /// Executes one typed query specification across the routed shards and
    /// reduces the per-shard answers.
    ///
    /// Every routed shard executes the caller's clauses through the query
    /// seam against its own snapshot; the reduced rows, matched count,
    /// ordering, and composed staleness are the single-shard shape. A routed
    /// shard that fails fails the whole fan-out, naming the shard.
    ///
    /// # Errors
    ///
    /// Returns [`ShardedExecutionError::Routing`] when routing fails,
    /// [`ShardedExecutionError::SnapshotMissing`] or
    /// [`ShardedExecutionError::SnapshotTenantMismatch`] for a routed shard
    /// whose snapshot is absent or names another tenant,
    /// [`ShardedExecutionError::InvalidPage`] when a per-shard page is
    /// refused by request validation, and
    /// [`ShardedExecutionError::QueryShard`] when one shard's endpoint
    /// refuses the query.
    pub fn execute(
        &self,
        serving: &dyn ShardQueryServing,
        spec: &QuerySpec,
        snapshots: &ShardSnapshots,
    ) -> Result<ShardedQueryResult, ShardedExecutionError> {
        let routing = self.route(spec).map_err(ShardedExecutionError::Routing)?;
        for route in routing.routes() {
            let snapshot = snapshots
                .of(route.shard())
                .ok_or(ShardedExecutionError::SnapshotMissing { shard: route.shard() })?;
            if snapshot.tenant() != spec.tenant_claim() {
                return Err(ShardedExecutionError::SnapshotTenantMismatch { shard: route.shard() });
            }
        }
        let needed = fetch_window(spec.paging());
        let mut executions = Vec::with_capacity(routing.routes().len());
        for route in routing.routes() {
            let snapshot = snapshots
                .of(route.shard())
                .ok_or(ShardedExecutionError::SnapshotMissing { shard: route.shard() })?;
            executions.push(self.execute_shard(serving, route.clone(), spec, snapshot, needed)?);
        }
        let namespaces: Vec<IndexNamespace> = spec.staleness().indexes().to_vec();
        let (staleness, namespace_detail) = compose_staleness(&namespaces, &executions);
        let merged = merge(spec.ordering(), &executions);
        let matched: u64 = executions.iter().map(|execution| execution.matched).sum();
        let rows = page(merged, spec.paging());
        let index_generation = staleness
            .observations()
            .first()
            .filter(|observation| observation.used().is_some())
            .and_then(|observation| {
                observation
                    .used()
                    .map(|used| IndexGenerationRef::new(observation.namespace().clone(), used))
            });
        Ok(ShardedQueryResult {
            rows,
            matched,
            ordering: spec.ordering().clone(),
            staleness,
            index_generation,
            per_shard: executions
                .iter()
                .map(|execution| ShardQueryOutcome {
                    shard: execution.route.shard,
                    owner: execution.route.owner.clone(),
                    epoch: execution.route.epoch,
                    reason: execution.route.reason,
                    matched: execution.matched,
                    rows: execution.row_count(),
                    staleness: execution.staleness.clone(),
                })
                .collect(),
            namespaces: namespace_detail,
        })
    }

    /// Executes one shard's pages until the fan-out's fetch window is
    /// satisfied or the shard is exhausted, and returns that shard's rows in
    /// the shard's own ordered pages.
    fn execute_shard(
        &self,
        serving: &dyn ShardQueryServing,
        route: ShardRoute,
        spec: &QuerySpec,
        snapshot: &ReadSnapshot,
        needed: Option<u64>,
    ) -> Result<ShardExecution, ShardedExecutionError> {
        let page_size = usize::try_from(self.page_limit).unwrap_or(usize::MAX);
        // The first page initializes the loop-carried facts, so no assignment
        // below is dead: every page of one query reports the same pre-paging
        // matched count and the same staleness report.
        let (matched, staleness, first) = self.serve_page(serving, &route, spec, snapshot, 0)?;
        let mut rows = first;
        let mut served = u64::try_from(rows.len()).unwrap_or(u64::MAX);
        let mut offset = served;
        let mut more = rows.len() == page_size && needed.is_none_or(|needed| served < needed);
        while more {
            let (_, _, page) = self.serve_page(serving, &route, spec, snapshot, offset)?;
            served += u64::try_from(page.len()).unwrap_or(u64::MAX);
            offset += u64::try_from(page.len()).unwrap_or(u64::MAX);
            let full = page.len() == page_size;
            rows.extend(page);
            more = full && needed.is_none_or(|needed| served < needed);
        }
        if let Some(needed) = needed {
            rows.truncate(usize::try_from(needed).unwrap_or(usize::MAX));
        }
        Ok(ShardExecution { route, rows, matched, staleness })
    }

    /// Serves one bounded page of one shard and returns the page's matched
    /// count, staleness report, and rows.
    fn serve_page(
        &self,
        serving: &dyn ShardQueryServing,
        route: &ShardRoute,
        spec: &QuerySpec,
        snapshot: &ReadSnapshot,
        offset: u64,
    ) -> Result<(u64, StalenessReport, Vec<QueryRow>), ShardedExecutionError> {
        let page_spec = page_spec(spec, offset, self.page_limit)?;
        let plan = serving
            .compile(route.owner(), QueryInput::typed(page_spec))
            .map_err(|error| ShardedExecutionError::QueryShard { shard: route.shard(), error })?;
        let result = serving
            .execute(route.owner(), &plan, snapshot)
            .map_err(|error| ShardedExecutionError::QueryShard { shard: route.shard(), error })?;
        Ok((result.matched(), result.staleness().clone(), result.rows().to_vec()))
    }
}

/// One shard's contribution to a fanned-out result.
struct ShardExecution {
    route: ShardRoute,
    rows: Vec<QueryRow>,
    matched: u64,
    staleness: StalenessReport,
}

impl ShardExecution {
    /// The number of rows this shard contributed.
    fn row_count(&self) -> u64 {
        u64::try_from(self.rows.len()).unwrap_or(u64::MAX)
    }
}

// ---------------------------------------------------------------------------
// Paging windows and the caller's page
// ---------------------------------------------------------------------------

/// The number of rows each shard must serve before the merge can produce the
/// caller's page: every row the caller's offset and limit can reach must be
/// inside one shard's first `offset + limit` rows, so that window per shard
/// is sufficient and no larger window is needed.
fn fetch_window(paging: QueryPaging) -> Option<u64> {
    paging.limit().map(|limit| paging.offset().saturating_add(u64::from(limit)))
}

/// Builds one per-shard page of the caller's query: identical clauses with
/// the page bounds replaced by the fan-out's next bounded window.
fn page_spec(
    spec: &QuerySpec,
    offset: u64,
    page_limit: u32,
) -> Result<QuerySpec, ShardedExecutionError> {
    QuerySpec::try_new(
        spec.tenant_claim().clone(),
        spec.source().clone(),
        spec.predicate().clone(),
        spec.ordering().clone(),
        spec.projection().clone(),
        QueryPaging::try_new(offset, Some(page_limit))
            .map_err(ShardedExecutionError::InvalidPage)?,
        spec.staleness().clone(),
        spec.deadline(),
        spec.cancellation(),
    )
    .map_err(ShardedExecutionError::InvalidPage)
}

/// Applies the caller's offset and limit to the merged order.
fn page(merged: Vec<QueryRow>, paging: QueryPaging) -> Vec<QueryRow> {
    let offset = usize::try_from(paging.offset()).unwrap_or(usize::MAX);
    let limit =
        paging.limit().map_or(usize::MAX, |limit| usize::try_from(limit).unwrap_or(usize::MAX));
    merged.into_iter().skip(offset).take(limit).collect()
}

// ---------------------------------------------------------------------------
// Reduction
// ---------------------------------------------------------------------------

/// Merges the per-shard pages into one order.
///
/// Rows compare under the seam's own ordering comparison first; equal keys
/// tie-break by the storage canonical key (collection, then document
/// identity), which is exactly the order the single-shard path's stable sort
/// preserves, so a fanned-out result and a single-shard result over the same
/// documents are one order. The first-routed shard wins a remaining tie,
/// which cannot arise for documents that route to distinct shards.
fn merge(ordering: &QueryOrdering, executions: &[ShardExecution]) -> Vec<QueryRow> {
    let mut streams: Vec<(&ShardExecution, usize)> = executions
        .iter()
        .filter(|execution| !execution.rows.is_empty())
        .map(|execution| (execution, 0))
        .collect();
    let total: usize = executions.iter().map(|execution| execution.rows.len()).sum();
    let mut merged = Vec::with_capacity(total);
    loop {
        let mut best: Option<usize> = None;
        for (position, (execution, cursor)) in streams.iter().enumerate() {
            let Some(row) = execution.rows.get(*cursor) else {
                continue;
            };
            best = match best {
                None => Some(position),
                Some(current) => {
                    let held = streams[current].0.rows.get(streams[current].1).expect("held row");
                    if order_under(ordering, row, held) == Ordering::Less {
                        Some(position)
                    } else {
                        Some(current)
                    }
                }
            };
        }
        let Some(position) = best else {
            return merged;
        };
        let (execution, cursor) = &mut streams[position];
        merged.push(execution.rows[*cursor].clone());
        *cursor += 1;
    }
}

/// Compares two rows under the merged order: the seam's ordering comparison,
/// then the canonical key.
fn order_under(ordering: &QueryOrdering, left: &QueryRow, right: &QueryRow) -> Ordering {
    query::compare_documents_under_ordering(ordering, left.document(), right.document())
        .then_with(|| canonical_key(left.document()).cmp(&canonical_key(right.document())))
}

/// The storage canonical key of one document: collection, then document
/// identity, the order the tenant's document view is served in.
fn canonical_key(document: &Document) -> (&str, &str) {
    (document.collection().as_str(), document.id().as_str())
}

// ---------------------------------------------------------------------------
// Staleness composition
// ---------------------------------------------------------------------------

/// Composes the per-shard staleness reports of one fanned-out execution.
///
/// Per namespace, the composed observation names the strictest shard's
/// observed requirement and the least advanced shard's supplied basis, and
/// reports the answer stale when any shard reported stale or when that
/// composition does not meet the strictest requirement. A shard that
/// reported nothing for a namespace counts as stale with no basis: a missing
/// observation can never confirm freshness.
fn compose_staleness(
    namespaces: &[IndexNamespace],
    executions: &[ShardExecution],
) -> (StalenessReport, Vec<ShardedNamespaceStaleness>) {
    let mut observations = Vec::with_capacity(namespaces.len());
    let mut detailed = Vec::with_capacity(namespaces.len());
    for namespace in namespaces {
        let mut per_shard = Vec::with_capacity(executions.len());
        for execution in executions {
            let (observed, used, stale) = match execution.staleness.observation_for(namespace) {
                Some(observation) => {
                    (observation.observed(), observation.used(), observation.stale())
                }
                None => (None, None, true),
            };
            per_shard.push(ShardStalenessObservation {
                shard: execution.route.shard,
                observed,
                used,
                stale,
            });
        }
        let composed_observed = per_shard
            .iter()
            .filter_map(|observation| observation.observed)
            .max_by_key(|generation| (generation.epoch(), generation.generation()));
        let composed_used = if per_shard.iter().any(|observation| observation.used().is_none()) {
            None
        } else {
            per_shard
                .iter()
                .filter_map(|observation| observation.used)
                .min_by_key(|generation| (generation.epoch(), generation.generation()))
        };
        let stale = per_shard.iter().any(|observation| observation.stale)
            || !meets(composed_observed, composed_used);
        let composed =
            StaleIndexObservation::new(namespace.clone(), composed_observed, composed_used, stale);
        detailed.push(ShardedNamespaceStaleness {
            namespace: namespace.clone(),
            per_shard,
            composed: composed.clone(),
        });
        observations.push(composed);
    }
    (StalenessReport::new(observations), detailed)
}

/// Reports whether a supplied basis meets an observed requirement: same
/// epoch, and a generation at least the observed one. Different epochs are
/// never comparable, so they never meet.
fn meets(observed: Option<ObservedGeneration>, used: Option<ObservedGeneration>) -> bool {
    match (observed, used) {
        (Some(observed), Some(used)) => {
            observed.epoch() == used.epoch() && used.generation() >= observed.generation()
        }
        _ => false,
    }
}

// ---------------------------------------------------------------------------
// Results
// ---------------------------------------------------------------------------

/// The reduced result of one fanned-out query: the seam's single-shard shape
/// plus the per-shard facts that produced it.
#[derive(Clone, Debug, PartialEq)]
pub struct ShardedQueryResult {
    rows: Vec<QueryRow>,
    matched: u64,
    ordering: QueryOrdering,
    staleness: StalenessReport,
    index_generation: Option<IndexGenerationRef>,
    per_shard: Vec<ShardQueryOutcome>,
    namespaces: Vec<ShardedNamespaceStaleness>,
}

impl ShardedQueryResult {
    /// Returns the result rows in result order.
    #[must_use]
    pub fn rows(&self) -> &[QueryRow] {
        &self.rows
    }

    /// Returns the number of rows the predicate matched before paging, the
    /// sum of the per-shard matched counts.
    #[must_use]
    pub const fn matched(&self) -> u64 {
        self.matched
    }

    /// Returns the ordering the result is in.
    #[must_use]
    pub fn ordering(&self) -> &QueryOrdering {
        &self.ordering
    }

    /// Returns the composed per-namespace staleness report, in the plan's
    /// namespace order: the same shape one shard's execution reports, with
    /// the strictest observed requirement and the least advanced basis of
    /// the shards the query reached.
    #[must_use]
    pub const fn staleness(&self) -> &StalenessReport {
        &self.staleness
    }

    /// Returns the generation the composed staleness gate accepted for this
    /// query, or `None` when no reached shard supplied a basis. Like the
    /// seam's own reference, it names an accepted generation, not a claim
    /// that index data served the query.
    #[must_use]
    pub const fn index_generation(&self) -> Option<&IndexGenerationRef> {
        self.index_generation.as_ref()
    }

    /// Returns one outcome per reached shard, in routing order.
    #[must_use]
    pub fn per_shard(&self) -> &[ShardQueryOutcome] {
        &self.per_shard
    }

    /// Returns the per-shard staleness detail of one required namespace.
    #[must_use]
    pub fn namespace_staleness(
        &self,
        namespace: &IndexNamespace,
    ) -> Option<&ShardedNamespaceStaleness> {
        self.namespaces.iter().find(|detail| detail.namespace() == namespace)
    }
}

/// One reached shard's contribution to a fanned-out result.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardQueryOutcome {
    shard: ShardId,
    owner: MemberId,
    epoch: u64,
    reason: RouteReason,
    matched: u64,
    rows: u64,
    staleness: StalenessReport,
}

impl ShardQueryOutcome {
    /// Returns the shard that produced this outcome.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the Member that owns the shard's write path.
    #[must_use]
    pub fn owner(&self) -> &MemberId {
        &self.owner
    }

    /// Returns the ownership epoch the route was executed under.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns why the query reached this shard.
    #[must_use]
    pub const fn reason(&self) -> &RouteReason {
        &self.reason
    }

    /// Returns the number of rows this shard's predicate matched before
    /// paging.
    #[must_use]
    pub const fn matched(&self) -> u64 {
        self.matched
    }

    /// Returns the number of rows this shard contributed to the reduced
    /// answer before the caller's page was applied.
    #[must_use]
    pub const fn rows(&self) -> u64 {
        self.rows
    }

    /// Returns this shard's own staleness report, as its execution reported
    /// it and before any composition.
    #[must_use]
    pub const fn staleness(&self) -> &StalenessReport {
        &self.staleness
    }
}

/// The staleness of one required index namespace across the shards a query
/// reached: every shard's own observation, and the composed observation the
/// reduced answer rests on.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardedNamespaceStaleness {
    namespace: IndexNamespace,
    per_shard: Vec<ShardStalenessObservation>,
    composed: StaleIndexObservation,
}

impl ShardedNamespaceStaleness {
    /// Returns the namespace this detail reports.
    #[must_use]
    pub fn namespace(&self) -> &IndexNamespace {
        &self.namespace
    }

    /// Returns every reached shard's own observation, in routing order.
    #[must_use]
    pub fn per_shard(&self) -> &[ShardStalenessObservation] {
        &self.per_shard
    }

    /// Returns the composed observation: the strictest shard's observed
    /// requirement, the least advanced shard's supplied basis, and a stale
    /// verdict that is never more permissive than the strictest shard's.
    #[must_use]
    pub const fn composed(&self) -> &StaleIndexObservation {
        &self.composed
    }
}

/// One shard's staleness observation of one required index namespace.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ShardStalenessObservation {
    shard: ShardId,
    observed: Option<ObservedGeneration>,
    used: Option<ObservedGeneration>,
    stale: bool,
}

impl ShardStalenessObservation {
    /// Returns the shard this observation came from.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the generation this shard's snapshot observed for the
    /// namespace, when one was observable.
    #[must_use]
    pub const fn observed(&self) -> Option<ObservedGeneration> {
        self.observed
    }

    /// Returns the generation this shard's execution accepted for the
    /// namespace, when one was supplied.
    #[must_use]
    pub const fn used(&self) -> Option<ObservedGeneration> {
        self.used
    }

    /// Reports whether this shard's accepted generation is older than, from a
    /// different epoch than, or absent from its own observation.
    #[must_use]
    pub const fn stale(&self) -> bool {
        self.stale
    }
}
