use tb_core::{MemoryBudget, MemoryCategory};

/// Fixed-cardinality process and memory-category values for a metrics scrape.
///
/// This intentionally omits tenant and worker identities. Capture reads only
/// the process memory controller; it never asks storage for filesystem stats.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MemoryMetricsSnapshot {
    process_limit_bytes: usize,
    headroom_bytes: usize,
    usable_limit_bytes: usize,
    reserved_bytes: usize,
    reserved_items: usize,
    categories: [usize; MemoryCategory::COUNT],
}

impl MemoryMetricsSnapshot {
    /// Capture only aggregate and fixed-category admission counters.
    #[must_use]
    pub fn capture(budget: &MemoryBudget) -> Self {
        let snapshot = budget.snapshot();
        Self {
            process_limit_bytes: snapshot.process_limit_bytes,
            headroom_bytes: snapshot.headroom_bytes,
            usable_limit_bytes: snapshot.usable_limit_bytes,
            reserved_bytes: snapshot.reserved_bytes,
            reserved_items: snapshot.reserved_items,
            categories: snapshot.categories,
        }
    }

    /// Configured process memory limit, including headroom.
    #[must_use]
    pub const fn process_limit_bytes(&self) -> usize {
        self.process_limit_bytes
    }

    /// Explicit process memory excluded from admission for runtime overhead.
    #[must_use]
    pub const fn headroom_bytes(&self) -> usize {
        self.headroom_bytes
    }

    /// Usable memory admission limit after headroom is subtracted.
    #[must_use]
    pub const fn usable_limit_bytes(&self) -> usize {
        self.usable_limit_bytes
    }

    /// Bytes currently held by admitted work across all fixed categories.
    #[must_use]
    pub const fn reserved_bytes(&self) -> usize {
        self.reserved_bytes
    }

    /// In-flight work items currently held by the process controller.
    #[must_use]
    pub const fn reserved_items(&self) -> usize {
        self.reserved_items
    }

    /// Reserved bytes in the stable category order from [`MemoryCategory::ALL`].
    #[must_use]
    pub const fn categories(&self) -> &[usize; MemoryCategory::COUNT] {
        &self.categories
    }

    /// Stable, low-cardinality Prometheus label for one memory category.
    #[must_use]
    pub const fn category_label(category: MemoryCategory) -> &'static str {
        match category {
            MemoryCategory::DatabaseState => "database_state",
            MemoryCategory::ReadCache => "read_cache",
            MemoryCategory::WriteBuffer => "write_buffer",
            MemoryCategory::TransactionVersion => "transaction_version",
            MemoryCategory::Index => "index",
            MemoryCategory::Replication => "replication",
            MemoryCategory::Compaction => "compaction",
            MemoryCategory::Snapshot => "snapshot",
            MemoryCategory::Query => "query",
            MemoryCategory::Transfer => "transfer",
            MemoryCategory::Observability => "observability",
            MemoryCategory::RuntimeOverhead => "runtime_overhead",
            MemoryCategory::Other => "other",
        }
    }
}

#[cfg(test)]
mod tests {
    use tb_core::{Attribution, BudgetConfig, TenantId, WorkerId};

    use super::*;

    #[test]
    fn snapshot_exports_aggregate_and_fixed_category_values_only() {
        let budget = MemoryBudget::new(BudgetConfig::new(1024, 128).expect("valid budget"));
        let attribution = Attribution {
            tenant: TenantId::from_bytes([9; 16]),
            worker: WorkerId(44),
            category: MemoryCategory::Transfer,
        };
        let _reservation = budget.try_reserve(attribution, 17).expect("admitted bytes");

        let snapshot = MemoryMetricsSnapshot::capture(&budget);

        assert_eq!(snapshot.process_limit_bytes(), 1024);
        assert_eq!(snapshot.headroom_bytes(), 128);
        assert_eq!(snapshot.usable_limit_bytes(), 896);
        assert_eq!(snapshot.reserved_bytes(), 17);
        assert_eq!(snapshot.categories()[MemoryCategory::Transfer as usize], 17);
        assert_eq!(
            MemoryMetricsSnapshot::category_label(MemoryCategory::Transfer),
            "transfer"
        );
        assert_eq!(MemoryCategory::ALL.len(), MemoryCategory::COUNT);
    }
}
