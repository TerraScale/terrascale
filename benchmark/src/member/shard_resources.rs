//! Physical shard reservations charged to the existing Member resident budget.
use super::*;

struct ShardResources {
    member: std::sync::Weak<MemberInner>,
    opening: bool,
}

impl crate::engine::ShardResourceReservation for ShardResources {
    fn opened(&mut self) {
        if let Some(inner) = self.member.upgrade() {
            let mut catalog = lock_unpoisoned(&inner.catalog);
            if self.opening {
                catalog.opening_databases = catalog.opening_databases.saturating_sub(1);
                catalog.metrics.opens = catalog.metrics.opens.saturating_add(1);
                self.opening = false;
                inner.catalog_changed.notify_all();
            }
        }
    }
}

impl Drop for ShardResources {
    fn drop(&mut self) {
        if let Some(inner) = self.member.upgrade() {
            let mut catalog = lock_unpoisoned(&inner.catalog);
            catalog.open_databases = catalog.open_databases.saturating_sub(1);
            catalog.resident_rss_bytes =
                catalog.resident_rss_bytes.saturating_sub(inner.database_rss_bytes);
            if self.opening {
                catalog.opening_databases = catalog.opening_databases.saturating_sub(1);
            }
            inner.catalog_changed.notify_all();
        }
    }
}

pub(super) fn factory(
    inner: &Arc<MemberInner>,
    database: DatabaseId,
) -> crate::engine::ShardResourceFactory {
    let member = Arc::downgrade(inner);
    Arc::new(move || {
        let inner = member.upgrade().ok_or(EngineOpenError::StorageUnavailable)?;
        loop {
            let mut catalog = lock_unpoisoned(&inner.catalog);
            if !catalog.databases.contains_key(&database) {
                return Err(EngineOpenError::StorageUnavailable);
            }
            let can_open = catalog.open_databases < inner.max_open_databases
                && catalog.resident_rss_bytes.saturating_add(inner.database_rss_bytes)
                    <= inner.rss_budget_bytes
                && catalog.opening_databases < inner.max_concurrent_opens;
            if can_open {
                catalog.open_databases += 1;
                catalog.opening_databases += 1;
                catalog.resident_rss_bytes =
                    catalog.resident_rss_bytes.saturating_add(inner.database_rss_bytes);
                return Ok(Box::new(ShardResources { member: member.clone(), opening: true })
                    as Box<dyn crate::engine::ShardResourceReservation>);
            }
            if let Some(close) = take_lru_eviction(&mut catalog) {
                drop(catalog);
                perform_close(&inner, close).map_err(|_| EngineOpenError::StorageUnavailable)?;
                continue;
            }
            if catalog.opening_databases > 0 {
                drop(wait_catalog(&inner, catalog));
                continue;
            }
            return Err(EngineOpenError::StorageUnavailable);
        }
    })
}
