//! Opt-in Litestream backup supervision for the local engine location.
//!
//! Supervision is enabled with `TERRABASE_BACKUP_SUPERVISION=1` and
//! configured entirely through environment variables so the daemon's TOML
//! surface stays free of process management. D-29 keeps backup replication
//! out of the hot path: when supervision is requested but cannot start, the
//! daemon logs the failure loudly and marks the Backup component unhealthy
//! but keeps serving. Backup is not required for readiness.

use std::net::SocketAddr;
use std::path::Path;

use terrabase::observability::Component;
use terrabase::observability::HealthStatus;
use terrabase::observability::MetricsRegistry;
use terrabase_backup::LitestreamSupervisor;
use terrabase_backup::ReplicaConfig;
use terrabase_backup::S3Replica;
use terrabase_backup::SupervisorConfig;

/// Enables supervision when set to exactly `1`.
const SUPERVISION_ENV: &str = "TERRABASE_BACKUP_SUPERVISION";
/// Local filesystem replica directory for the default replica.
const REPLICA_DIR_ENV: &str = "TERRABASE_BACKUP_REPLICA_DIR";
/// Watched-file glob override.
const PATTERN_ENV: &str = "TERRABASE_BACKUP_PATTERN";
/// Prometheus `/metrics` bind address for the Litestream child.
const METRICS_ADDR_ENV: &str = "TERRABASE_BACKUP_METRICS_ADDR";
/// S3-compatible endpoint URL. The full S3 variable set selects the S3
/// replica instead of the local file replica.
const S3_ENDPOINT_ENV: &str = "TERRABASE_BACKUP_S3_ENDPOINT";
/// S3-compatible destination bucket.
const S3_BUCKET_ENV: &str = "TERRABASE_BACKUP_S3_BUCKET";
/// Prefix inside the S3-compatible bucket dedicated to this member.
const S3_PATH_ENV: &str = "TERRABASE_BACKUP_S3_PATH";
/// Signing region. OCI ignores the value; Litestream requires the field.
const S3_REGION_ENV: &str = "TERRABASE_BACKUP_S3_REGION";

/// Reads one environment variable.
type Vars<'a> = &'a dyn Fn(&str) -> Option<String>;

fn process_env(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.is_empty())
}

/// Starts backup supervision over the engine location when the environment
/// requests it.
///
/// Failure is deliberately fail-open for the serving path (D-29: backup is
/// disaster recovery, never in the hot path): the error is logged on
/// standard error and the Backup health component turns unhealthy so
/// operators see it, but the member keeps serving. `None` means no child is
/// supervised.
pub(crate) fn start(
    engine_location: &Path,
    metrics: &MetricsRegistry,
) -> Option<LitestreamSupervisor> {
    if std::env::var(SUPERVISION_ENV).ok().as_deref() != Some("1") {
        return None;
    }
    let lookup = process_env;
    let failure = match supervisor_config(engine_location, &lookup) {
        Ok(config) => match LitestreamSupervisor::start(config) {
            Ok(supervisor) => {
                metrics.set_component_health(Component::Backup, HealthStatus::Healthy);
                return Some(supervisor);
            }
            Err(error) => error.to_string(),
        },
        // An incomplete or invalid environment must be just as loud as a
        // spawn failure, and just as non-fatal for serving.
        Err(detail) => detail,
    };
    metrics.set_component_health(Component::Backup, HealthStatus::Unhealthy);
    eprintln!("terrabase: backup supervision is not running; serving continues: {failure}");
    None
}

fn supervisor_config(engine_location: &Path, vars: Vars<'_>) -> Result<SupervisorConfig, String> {
    let replica = replica_from_environment(vars)?;
    let mut config = SupervisorConfig::new(engine_location.to_path_buf(), replica);
    if let Some(pattern) = vars(PATTERN_ENV) {
        config.pattern = pattern;
    }
    if let Some(address) = vars(METRICS_ADDR_ENV) {
        config.metrics_addr = Some(
            address
                .parse::<SocketAddr>()
                .map_err(|error| format!("{METRICS_ADDR_ENV} is not a socket address: {error}"))?,
        );
    }
    Ok(config)
}

fn replica_from_environment(vars: Vars<'_>) -> Result<ReplicaConfig, String> {
    if let Some(endpoint) = vars(S3_ENDPOINT_ENV) {
        let missing = [
            (S3_BUCKET_ENV, vars(S3_BUCKET_ENV)),
            (S3_PATH_ENV, vars(S3_PATH_ENV)),
            (S3_REGION_ENV, vars(S3_REGION_ENV)),
        ]
        .into_iter()
        .filter_map(|(name, value)| value.is_none().then_some(name))
        .collect::<Vec<_>>()
        .join(", ");
        if !missing.is_empty() {
            return Err(format!("{S3_ENDPOINT_ENV} is set but missing: {missing}"));
        }
        return Ok(ReplicaConfig::S3(S3Replica {
            endpoint,
            bucket: vars(S3_BUCKET_ENV).expect("checked above"),
            path: vars(S3_PATH_ENV).expect("checked above"),
            region: vars(S3_REGION_ENV).expect("checked above"),
        }));
    }
    let path = vars(REPLICA_DIR_ENV).ok_or_else(|| {
        format!("set {REPLICA_DIR_ENV} (or the {S3_ENDPOINT_ENV} set) when {SUPERVISION_ENV}=1")
    })?;
    Ok(ReplicaConfig::File { path: std::path::PathBuf::from(path) })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn vars<'a>(pairs: &'a [(&'a str, &'a str)]) -> impl Fn(&str) -> Option<String> + 'a {
        let map: HashMap<&str, &str> = pairs.iter().copied().collect();
        move |name| map.get(name).map(|value| (*value).to_owned())
    }

    #[test]
    fn replica_requires_a_destination_variable() {
        let error =
            replica_from_environment(&vars(&[])).expect_err("missing replica variables must fail");
        assert!(error.contains("TERRABASE_BACKUP_REPLICA_DIR"), "got: {error}");
    }

    #[test]
    fn partial_s3_variables_are_rejected_with_the_missing_names() {
        let error = replica_from_environment(&vars(&[
            (S3_ENDPOINT_ENV, "https://endpoint"),
            (S3_BUCKET_ENV, "terrabase-backups"),
        ]))
        .expect_err("incomplete S3 variables must fail");
        assert!(error.contains(S3_PATH_ENV) && error.contains(S3_REGION_ENV), "got: {error}");
    }

    #[test]
    fn full_s3_variables_build_the_s3_replica() {
        let config = supervisor_config(
            Path::new("/engine"),
            &vars(&[
                (S3_ENDPOINT_ENV, "https://ns.compat.objectstorage.gru.oraclecloud.com"),
                (S3_BUCKET_ENV, "terrabase-backups"),
                (S3_PATH_ENV, "members/gru-1"),
                (S3_REGION_ENV, "gru"),
                (PATTERN_ENV, "*.db"),
                (METRICS_ADDR_ENV, "127.0.0.1:19090"),
            ]),
        )
        .expect("complete variables build a config");
        assert_eq!(
            config.replica,
            ReplicaConfig::S3(S3Replica {
                endpoint: "https://ns.compat.objectstorage.gru.oraclecloud.com".to_owned(),
                bucket: "terrabase-backups".to_owned(),
                path: "members/gru-1".to_owned(),
                region: "gru".to_owned(),
            })
        );
        assert_eq!(config.pattern, "*.db");
        assert_eq!(config.metrics_addr, Some("127.0.0.1:19090".parse().expect("address parses")));
    }

    #[test]
    fn invalid_metrics_address_is_rejected() {
        let error = supervisor_config(
            Path::new("/engine"),
            &vars(&[(REPLICA_DIR_ENV, "/backups"), (METRICS_ADDR_ENV, "not-an-address")]),
        )
        .expect_err("an invalid metrics address must fail");
        assert!(error.contains(METRICS_ADDR_ENV), "got: {error}");
    }

    #[test]
    fn file_replica_defaults_apply_without_optional_variables() {
        let config =
            supervisor_config(Path::new("/engine"), &vars(&[(REPLICA_DIR_ENV, "/backups")]))
                .expect("the file replica builds with one variable");
        assert_eq!(
            config.replica,
            ReplicaConfig::File { path: std::path::PathBuf::from("/backups") }
        );
        assert_eq!(config.pattern, terrabase_backup::DEFAULT_PATTERN);
        assert_eq!(config.metrics_addr, None);
    }
}
