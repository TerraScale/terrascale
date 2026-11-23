//! Replication status of a database's equal active-active peer regions.

use std::time::Duration;

use super::{Database, Result};
use serde::Deserialize;

/// The replication status of every peer region of one database, as seen by
/// the region that answered.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DatabaseRegions {
    pub database_id: String,
    #[serde(default, alias = "observedAtUtc")]
    pub observed_at: String,
    pub regions: Vec<ReplicaStatus>,
}

impl DatabaseRegions {
    /// The regions whose replication link is healthy.
    pub fn healthy(&self) -> impl Iterator<Item = &ReplicaStatus> {
        self.regions.iter().filter(|region| region.is_healthy())
    }

    /// The status of `region`, matched case-insensitively.
    #[must_use]
    pub fn region(&self, region: &str) -> Option<&ReplicaStatus> {
        self.regions
            .iter()
            .find(|status| status.region.eq_ignore_ascii_case(region))
    }
}

/// One peer region of a database. Every region accepts reads and writes.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReplicaStatus {
    pub region: String,
    #[serde(default)]
    pub lag_ms: Option<f64>,
    /// The causal bookmark this region has applied.
    #[serde(default)]
    pub applied_bookmark: Option<String>,
    /// Whether the answering region currently reaches this peer.
    #[serde(default)]
    pub healthy: Option<bool>,
}

impl ReplicaStatus {
    /// Whether the region is reachable; a status without health data counts
    /// as healthy.
    #[must_use]
    pub fn is_healthy(&self) -> bool {
        self.healthy.unwrap_or(true)
    }

    /// The replication lag, when known; a negative lag means unknown.
    #[must_use]
    pub fn lag(&self) -> Option<Duration> {
        self.lag_ms
            .filter(|lag| lag.is_finite() && *lag >= 0.0)
            .map(|lag| Duration::from_secs_f64(lag / 1000.0))
    }
}

impl Database {
    /// `GET P/databases/{database}/regions`: replication status of every
    /// peer region. Requires `databases:read`.
    pub async fn get_database_regions(&self) -> Result<DatabaseRegions> {
        self.get_json(&["regions"], &[]).await
    }
}
