//! The Litestream `replicate` config file format.
//!
//! Litestream has no SIGHUP or hot reload: every settings change is a child
//! restart, so the generated document is deliberately a fixed shape — one
//! watched directory entry per supervisor plus a replica. This module owns
//! the on-disk syntax; [`crate::SupervisorConfig`] owns the semantics.

use std::net::SocketAddr;
use std::time::Duration;

use serde::{Deserialize, Serialize};

use crate::{ReplicaConfig, S3Replica};

/// The complete Litestream config document the supervisor writes.
#[derive(Debug, Deserialize, Eq, PartialEq, Serialize)]
pub(crate) struct LitestreamFile {
    /// Database source entries. Always exactly one watched directory.
    pub(crate) dbs: Vec<WatchedDirectory>,
    /// Prometheus `/metrics` bind address, emitted only when supervision
    /// configures one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) addr: Option<String>,
    /// Deletion deferral: the replica provider's lifecycle owns expiry.
    pub(crate) retention: Retention,
}

/// One `dbs:` entry that watches a directory for matching `SQLite` files.
#[derive(Debug, Deserialize, Eq, PartialEq, Serialize)]
pub(crate) struct WatchedDirectory {
    /// Directory holding one `SQLite` file per tenant database.
    pub(crate) dir: String,
    /// Glob Litestream applies inside `dir` (for example `*.sqlite3`).
    pub(crate) pattern: String,
    /// Always true: new databases start replicating and deletions stop
    /// replicating without a child restart.
    pub(crate) watch: bool,
    /// Always true: the engine location holds databases in nested
    /// per-database directories.
    pub(crate) recursive: bool,
    /// WAL polling interval.
    #[serde(rename = "sync-interval")]
    pub(crate) sync_interval: String,
    /// The single replica every database in `dir` streams to.
    pub(crate) replica: Replica,
}

/// The `replica:` block of a `dbs:` entry.
#[derive(Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(tag = "type", rename_all = "lowercase")]
pub(crate) enum Replica {
    /// Local filesystem replica, used for development and tests.
    File {
        /// Directory Litestream writes replica generations under.
        path: String,
    },
    /// S3-compatible replica, used for OCI Object Storage in production.
    S3 {
        /// S3-compatible endpoint URL.
        endpoint: String,
        /// Destination bucket.
        bucket: String,
        /// Prefix inside the bucket.
        path: String,
        /// Signing region. OCI ignores the value but Litestream requires
        /// the field.
        region: String,
        /// Path-style addressing, which OCI compatibility requires.
        #[serde(rename = "force-path-style")]
        force_path_style: bool,
    },
}

/// Retention settings: deletion is deferred to the provider (D-25).
#[derive(Debug, Deserialize, Eq, PartialEq, Serialize)]
pub(crate) struct Retention {
    /// Always false: cloud lifecycle policies expire backups instead.
    pub(crate) enabled: bool,
}

impl Retention {
    /// The only retention posture the supervisor generates.
    pub(crate) const DEFERRED: Self = Self { enabled: false };
}

/// Formats a whole-seconds duration the way Litestream config parses it.
pub(crate) fn format_seconds(interval: Duration) -> String {
    format!("{}s", interval.as_secs().max(1))
}

/// Renders the config document for one supervised directory and replica.
pub(crate) fn render(
    dir: &std::path::Path,
    pattern: &str,
    replica: &ReplicaConfig,
    sync_interval: Duration,
    metrics_addr: Option<SocketAddr>,
) -> LitestreamFile {
    let replica = match replica {
        ReplicaConfig::File { path } => Replica::File { path: path.to_string_lossy().into_owned() },
        ReplicaConfig::S3(S3Replica { endpoint, bucket, path, region }) => Replica::S3 {
            endpoint: endpoint.clone(),
            bucket: bucket.clone(),
            path: path.clone(),
            region: region.clone(),
            force_path_style: true,
        },
    };
    LitestreamFile {
        dbs: vec![WatchedDirectory {
            dir: dir.to_string_lossy().into_owned(),
            pattern: pattern.to_owned(),
            watch: true,
            recursive: true,
            sync_interval: format_seconds(sync_interval),
            replica,
        }],
        addr: metrics_addr.map(|addr| addr.to_string()),
        retention: Retention::DEFERRED,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    fn config(replica: &ReplicaConfig) -> LitestreamFile {
        render(
            Path::new("/var/lib/terrabase"),
            "*.sqlite3",
            replica,
            Duration::from_secs(1),
            Some("127.0.0.1:19090".parse().expect("metrics address parses")),
        )
    }

    #[test]
    fn file_replica_document_is_deterministic_and_round_trips() {
        let document = config(&ReplicaConfig::File {
            path: Path::new("/var/backups/terrabase").to_path_buf(),
        });
        let first = serde_yaml::to_string(&document).expect("file replica renders");
        let second = serde_yaml::to_string(&document).expect("file replica renders again");
        assert_eq!(first, second);
        assert_eq!(
            serde_yaml::from_str::<LitestreamFile>(&first).expect("file replica round-trips"),
            document
        );
        assert!(first.contains("watch: true"));
        assert!(first.contains("recursive: true"));
        assert!(first.contains("enabled: false"));
        assert!(first.contains("addr: 127.0.0.1:19090"));
    }

    #[test]
    fn s3_replica_document_round_trips_with_path_style() {
        let document = config(&ReplicaConfig::S3(S3Replica {
            endpoint: "https://ns.compat.objectstorage.gru.oraclecloud.com".to_owned(),
            bucket: "terrabase-backups".to_owned(),
            path: "members/gru-1".to_owned(),
            region: "gru".to_owned(),
        }));
        let rendered = serde_yaml::to_string(&document).expect("s3 replica renders");
        assert_eq!(
            serde_yaml::from_str::<LitestreamFile>(&rendered).expect("s3 replica round-trips"),
            document
        );
        assert!(rendered.contains("force-path-style: true"));
        assert!(rendered.contains("sync-interval: 1s"));
    }

    #[test]
    fn metrics_address_is_omitted_when_unset() {
        let document = render(
            Path::new("/data"),
            "*.sqlite3",
            &ReplicaConfig::File { path: Path::new("/backups").to_path_buf() },
            Duration::from_secs(1),
            None,
        );
        let rendered = serde_yaml::to_string(&document).expect("renders");
        assert!(!rendered.contains("addr:"));
    }

    #[test]
    fn sub_second_intervals_clamp_to_one_second() {
        assert_eq!(format_seconds(Duration::from_millis(250)), "1s");
        assert_eq!(format_seconds(Duration::from_secs(5)), "5s");
    }
}
