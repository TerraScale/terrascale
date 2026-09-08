//! Durable member outbox; the host polls this authenticated Unix control route.
use super::*;
use terrabase::usage::{BudgetConsumption, VerifiedAllocation};
use terrabase_cluster::wal_driver::WalEngineHandle;

pub(crate) struct PoolRuntime {
    pub engines: Arc<dyn Fn(&str) -> Result<Arc<dyn WalEngineHandle>, String> + Send + Sync>,
    pub databases: Arc<dyn Fn() -> Result<Vec<String>, String> + Send + Sync>,
    pub limits: PoolLimits,
}
#[derive(Clone, Copy)]
pub(crate) struct PoolLimits {
    pub maximum: i64,
    pub low_water: i64,
    pub lifetime_seconds: u64,
}
impl Default for PoolLimits {
    fn default() -> Self {
        Self { maximum: 1024, low_water: 128, lifetime_seconds: 900 }
    }
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct RefillRequest {
    refill_operation_id: String,
    pool_id: String,
    organization_id: String,
    database_id: String,
    branch_id: String,
    holder_id: String,
    region: String,
    selected_regions: Vec<String>,
    placement: String,
    requested_local_units: i64,
    available_local_units: i64,
    pool_max_local_units: i64,
    low_water_local_units: i64,
    policy_epoch: u64,
    consent_epoch: u64,
    writer_epoch: u64,
    expires_at: String,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct PoolWork {
    request: RefillRequest,
    consumptions: Vec<BudgetConsumption>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Poll {
    #[serde(default)]
    after_database: String,
}
#[derive(Serialize)]
struct PollReply {
    next_database: String,
    work: Option<PoolWork>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Ack {
    database_id: String,
    refill_operation_id: String,
}

impl UsageAdmissionJournal {
    pub(crate) fn with_pool_runtime(mut self, runtime: PoolRuntime) -> Result<Self, String> {
        let limits = runtime.limits;
        if limits.maximum <= 0
            || limits.maximum > 1024
            || limits.low_water < 0
            || limits.low_water >= limits.maximum
            || limits.lifetime_seconds == 0
            || limits.lifetime_seconds > 900
        {
            return Err("invalid regional pool limits".into());
        }
        self.connection.lock().map_err(|_| "pool journal lock unavailable")?.execute_batch(
            "CREATE TABLE IF NOT EXISTS pool_work(database_id TEXT PRIMARY KEY, payload BLOB NOT NULL) STRICT, WITHOUT ROWID;
             CREATE TABLE IF NOT EXISTS pool_reported(database_id TEXT PRIMARY KEY, through_sequence INTEGER NOT NULL) STRICT, WITHOUT ROWID;"
        ).map_err(|_| "pool journal unavailable")?;
        self.pool_runtime = Some(runtime);
        Ok(self)
    }
    pub(super) fn install_pool_credit(
        &self,
        allocation: &VerifiedAllocation,
    ) -> Result<(), String> {
        let Some(runtime) = &self.pool_runtime else {
            return Ok(());
        };
        let handle = (runtime.engines)(&allocation.grant().database_id)?;
        let engine = handle.engine();
        let tenant = terrabase::types::TenantId::try_new(&allocation.grant().organization_id)
            .map_err(|_| "invalid pool tenant")?;
        engine
            .install_usage_budget(&engine.scope_for(&tenant), allocation)
            .map_err(|_| "pool credit installation failed".into())
    }
    fn valid_refills(&self, database: &str) -> Result<Vec<VerifiedAllocation>, String> {
        let ids = {
            let connection = self.connection.lock().map_err(|_| "pool journal lock unavailable")?;
            let mut query = connection
                .prepare("SELECT operation FROM allocations WHERE database_id=?1")
                .map_err(|_| "pool journal read failed")?;
            query
                .query_map([database], |r| r.get::<_, String>(0))
                .map_err(|_| "pool journal read failed")?
                .collect::<Result<Vec<_>, _>>()
                .map_err(|_| "pool journal read failed")?
        };
        let mut allocations = Vec::new();
        for id in ids {
            if let Ok(Some(allocation)) = self.resolve_allocation(&id, database) {
                allocations.push(allocation);
            }
        }
        allocations.sort_by_key(|a| a.grant().pool_sequence);
        Ok(allocations)
    }
    /// Selects a spendable refill while preserving the original operation's admission binding.
    pub(crate) fn pool_for_operation(
        &self,
        database: &str,
        operation: &str,
        units: u64,
    ) -> Result<Option<VerifiedAllocation>, String> {
        let prior: Option<Vec<u8>> = self.connection.lock().map_err(|_| "pool journal lock unavailable")?.query_row(
            "SELECT payload_digest FROM operation_admissions WHERE database_id=?1 AND operation=?2", params![database, operation], |r| r.get(0)).optional().map_err(|_| "pool journal read failed")?;
        let allocations = self.valid_refills(database)?;
        for allocation in allocations {
            if let Some(prior) = &prior {
                if prior == allocation.payload_digest().as_slice() {
                    return Ok(Some(allocation));
                }
                continue;
            }
            if units > allocation.local_units() as u64 {
                continue;
            }
            if let Some(runtime) = &self.pool_runtime {
                self.install_pool_credit(&allocation)?;
                let handle = (runtime.engines)(database)?;
                let engine = handle.engine();
                let tenant =
                    terrabase::types::TenantId::try_new(&allocation.grant().organization_id)
                        .map_err(|_| "invalid pool tenant")?;
                if engine
                    .usage_budget_remaining(
                        &engine.scope_for(&tenant),
                        &allocation.grant().refill_operation_id,
                    )
                    .map_err(|_| "pool credit read failed")?
                    .unwrap_or(0)
                    < units as i64
                {
                    continue;
                }
            }
            return Ok(Some(allocation));
        }
        Ok(None)
    }
    fn poll_pool(&self, after: &str) -> Result<PollReply, String> {
        let runtime = self.pool_runtime.as_ref().ok_or("pool runtime unavailable")?;
        let mut databases = (runtime.databases)()?;
        databases.sort();
        let Some(database) =
            databases.iter().find(|db| db.as_str() > after).or_else(|| databases.first())
        else {
            return Ok(PollReply { next_database: String::new(), work: None });
        };
        let config = self
            .allocation_verifier
            .lock()
            .map_err(|_| "pool verifier unavailable")?
            .clone()
            .ok_or("pool verifier unavailable")?;
        // Revalidate the local writer before exposing work, including pending work after restart.
        let (organization, holder, placement, writer_epoch, mut regions) =
            config.resolver.binding(database)?;
        if holder != self.member_id {
            return Err("pool holder is not local".into());
        }
        regions = regions.into_iter().map(|r| r.to_ascii_lowercase()).collect();
        regions.sort();
        let pending: Option<Vec<u8>> = self
            .connection
            .lock()
            .map_err(|_| "pool journal lock unavailable")?
            .query_row("SELECT payload FROM pool_work WHERE database_id=?1", [database], |r| {
                r.get(0)
            })
            .optional()
            .map_err(|_| "pool journal read failed")?;
        if let Some(pending) = pending {
            let work: PoolWork =
                serde_json::from_slice(&pending).map_err(|_| "pool journal corrupt")?;
            if work.request.holder_id != holder
                || work.request.placement != placement
                || work.request.writer_epoch != writer_epoch
            {
                return Err("pending pool binding is fenced".into());
            }
            return Ok(PollReply { next_database: database.clone(), work: Some(work) });
        }
        let handle = (runtime.engines)(database)?;
        let engine = handle.engine();
        let tenant = terrabase::types::TenantId::try_new(&organization)
            .map_err(|_| "invalid pool tenant")?;
        let scope = engine.scope_for(&tenant);
        let allocations = self.valid_refills(database)?;
        let mut available = 0i64;
        for allocation in &allocations {
            self.install_pool_credit(allocation)?;
            available = available
                .checked_add(
                    engine
                        .usage_budget_remaining(&scope, &allocation.grant().refill_operation_id)
                        .map_err(|_| "pool credit read failed")?
                        .unwrap_or(0),
                )
                .ok_or("pool credit overflow")?;
        }
        let through: u64 = self
            .connection
            .lock()
            .map_err(|_| "pool journal lock unavailable")?
            .query_row(
                "SELECT through_sequence FROM pool_reported WHERE database_id=?1",
                [database],
                |r| r.get(0),
            )
            .optional()
            .map_err(|_| "pool journal read failed")?
            .unwrap_or(0);
        let consumptions = engine
            .usage_budget_consumptions(&scope, through, 64)
            .map_err(|_| "pool consumption read failed")?;
        let requested = if available <= runtime.limits.low_water {
            runtime.limits.maximum.checked_sub(available).ok_or("pool credit overflow")?
        } else {
            0
        };
        if requested == 0 && consumptions.is_empty() {
            return Ok(PollReply { next_database: database.clone(), work: None });
        }
        let expires = unix_millis()
            .checked_add(runtime.limits.lifetime_seconds * 1000)
            .ok_or("pool expiry overflow")?;
        let expires = chrono::DateTime::from_timestamp_millis(
            i64::try_from(expires).map_err(|_| "pool expiry invalid")?,
        )
        .ok_or("pool expiry invalid")?
        .to_rfc3339();
        let pool_id = allocations
            .last()
            .map(|a| a.grant().pool_id.clone())
            .unwrap_or_else(|| format!("{holder}/{database}/{writer_epoch}/{placement}"));
        let work = PoolWork {
            request: RefillRequest {
                refill_operation_id: terrabase::identity::DatabaseUlid::generate().to_text(),
                pool_id,
                organization_id: organization,
                database_id: database.clone(),
                branch_id: String::new(),
                holder_id: holder,
                region: self.region.to_ascii_lowercase(),
                selected_regions: regions,
                placement,
                requested_local_units: requested,
                available_local_units: available,
                pool_max_local_units: runtime.limits.maximum,
                low_water_local_units: runtime.limits.low_water,
                policy_epoch: config.policy_epoch,
                consent_epoch: config.consent_epoch,
                writer_epoch,
                expires_at: expires,
            },
            consumptions,
        };
        let bytes = serde_json::to_vec(&work).map_err(|_| "pool work encoding failed")?;
        let mut connection = self.connection.lock().map_err(|_| "pool journal lock unavailable")?;
        let tx = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| "pool journal transaction failed")?;
        tx.execute("INSERT OR IGNORE INTO pool_work VALUES(?1,?2)", params![database, bytes])
            .map_err(|_| "pool work persistence failed")?;
        let bytes: Vec<u8> = tx
            .query_row("SELECT payload FROM pool_work WHERE database_id=?1", [database], |r| {
                r.get(0)
            })
            .map_err(|_| "pool work persistence failed")?;
        tx.commit().map_err(|_| "pool work sync failed")?;
        Ok(PollReply {
            next_database: database.clone(),
            work: Some(serde_json::from_slice(&bytes).map_err(|_| "pool journal corrupt")?),
        })
    }
    fn acknowledge_pool(&self, ack: Ack) -> Result<(), String> {
        let mut connection = self.connection.lock().map_err(|_| "pool journal lock unavailable")?;
        let tx = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| "pool journal transaction failed")?;
        let bytes: Option<Vec<u8>> = tx
            .query_row(
                "SELECT payload FROM pool_work WHERE database_id=?1",
                [&ack.database_id],
                |r| r.get(0),
            )
            .optional()
            .map_err(|_| "pool work read failed")?;
        if let Some(bytes) = bytes {
            let work: PoolWork =
                serde_json::from_slice(&bytes).map_err(|_| "pool journal corrupt")?;
            if work.request.refill_operation_id != ack.refill_operation_id {
                return Err("pool acknowledgment identity mismatch".into());
            }
            if let Some(last) = work.consumptions.last() {
                tx.execute("INSERT INTO pool_reported VALUES(?1,?2) ON CONFLICT(database_id) DO UPDATE SET through_sequence=MAX(through_sequence,excluded.through_sequence)", params![ack.database_id, last.sequence]).map_err(|_| "pool cursor persistence failed")?;
            }
            tx.execute("DELETE FROM pool_work WHERE database_id=?1", [&ack.database_id])
                .map_err(|_| "pool work persistence failed")?;
        }
        tx.commit().map_err(|_| "pool work sync failed")
    }
}

pub(super) async fn work(
    State(journal): State<Arc<UsageAdmissionJournal>>,
    Json(poll): Json<Poll>,
) -> axum::response::Response {
    match tokio::task::spawn_blocking(move || journal.poll_pool(&poll.after_database)).await {
        Ok(Ok(reply)) => Json(reply).into_response(),
        _ => StatusCode::SERVICE_UNAVAILABLE.into_response(),
    }
}
pub(super) async fn ack(
    State(journal): State<Arc<UsageAdmissionJournal>>,
    Json(ack): Json<Ack>,
) -> StatusCode {
    match tokio::task::spawn_blocking(move || journal.acknowledge_pool(ack)).await {
        Ok(Ok(())) => StatusCode::OK,
        _ => StatusCode::CONFLICT,
    }
}
