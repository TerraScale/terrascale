use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use tokio::sync::watch;

use crate::ReplicationMemoryProfile;

const CGROUP_ROOT: &str = "/sys/fs/cgroup";
const SELF_CGROUP: &str = "/proc/self/cgroup";
const SAMPLE_INTERVAL: Duration = Duration::from_millis(100);
const MAX_SAMPLE_AGE: Duration = Duration::from_secs(1);

#[derive(Clone, Debug)]
struct CgroupPaths {
    current: PathBuf,
    limit: PathBuf,
}

#[derive(Clone, Copy, Debug)]
struct CgroupSample {
    current_bytes: usize,
    limit_bytes: usize,
    updated_at: Instant,
    valid: bool,
}

/// A finite cgroup memory observer used as a second admission signal.
///
/// `MemoryBudget` accounts logical reservations. This monitor observes actual
/// cgroup usage separately and does not treat those logical reservations as an
/// RSS guarantee.
#[derive(Clone, Debug)]
pub struct CgroupMemoryMonitor {
    paths: Arc<CgroupPaths>,
    sample: Arc<Mutex<CgroupSample>>,
}

impl CgroupMemoryMonitor {
    /// Resolve this process's cgroup v2 files and require a finite hard limit
    /// at least as large as the configured replication process limit.
    ///
    /// # Errors
    /// Fails closed if the cgroup path cannot be found, the hard memory limit
    /// is unlimited, or the configured process profile exceeds that limit.
    pub async fn open(profile: ReplicationMemoryProfile) -> Result<Self, MemoryPressureError> {
        let membership = tokio::fs::read_to_string(SELF_CGROUP)
            .await
            .map_err(|error| MemoryPressureError::CgroupUnavailable(error.to_string()))?;
        let directory = resolve_cgroup_v2_directory(&membership, Path::new(CGROUP_ROOT))?;
        let paths = CgroupPaths {
            current: directory.join("memory.current"),
            limit: directory.join("memory.max"),
        };
        let sample = read_sample(&paths).await?;
        if sample.limit_bytes < profile.process_limit_bytes() {
            return Err(MemoryPressureError::CgroupLimitBelowProcessLimit {
                cgroup_limit: sample.limit_bytes,
                process_limit: profile.process_limit_bytes(),
            });
        }
        Ok(Self {
            paths: Arc::new(paths),
            sample: Arc::new(Mutex::new(CgroupSample {
                updated_at: Instant::now(),
                valid: true,
                ..sample
            })),
        })
    }

    /// Refresh cgroup memory usage and its finite limit asynchronously.
    ///
    /// # Errors
    /// Returns an error when either cgroup file is missing, malformed, or
    /// becomes unlimited.
    pub async fn refresh(&self) -> Result<(), MemoryPressureError> {
        let sample = match read_sample(&self.paths).await {
            Ok(sample) => sample,
            Err(error) => {
                self.invalidate();
                return Err(error);
            }
        };
        let Ok(mut current) = self.sample.lock() else {
            return Err(MemoryPressureError::MonitorPoisoned);
        };
        *current = CgroupSample {
            updated_at: Instant::now(),
            valid: true,
            ..sample
        };
        Ok(())
    }

    /// Start periodic asynchronous sampling until shutdown is signalled.
    #[must_use]
    pub fn spawn_sampler(
        &self,
        mut shutdown: watch::Receiver<bool>,
    ) -> tokio::task::JoinHandle<()> {
        let monitor = self.clone();
        tokio::spawn(async move {
            let mut interval = tokio::time::interval(SAMPLE_INTERVAL);
            interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
            loop {
                tokio::select! {
                    changed = shutdown.changed() => {
                        if changed.is_err() || *shutdown.borrow() {
                            break;
                        }
                    }
                    _ = interval.tick() => {
                        if let Err(error) = monitor.refresh().await {
                            tracing::warn!(%error, "cgroup memory sampling failed; replication admission is failing closed");
                        }
                    }
                }
            }
        })
    }

    /// Whether a proposed logical reservation fits the fresh observed cgroup
    /// sample and the configured process profile's half-headroom safety margin.
    #[must_use]
    pub fn can_admit(&self, additional_bytes: usize, profile: ReplicationMemoryProfile) -> bool {
        let Ok(sample) = self.sample.lock() else {
            return false;
        };
        if !sample.valid || sample.updated_at.elapsed() > MAX_SAMPLE_AGE {
            return false;
        }
        is_admissible(
            sample.current_bytes,
            sample.limit_bytes,
            additional_bytes,
            profile.process_limit_bytes(),
            profile.headroom_bytes(),
        )
    }

    /// Last successfully sampled cgroup usage and limit, if the sample is fresh.
    #[must_use]
    pub fn snapshot(&self) -> Option<(usize, usize)> {
        let sample = self.sample.lock().ok()?;
        (sample.valid && sample.updated_at.elapsed() <= MAX_SAMPLE_AGE)
            .then_some((sample.current_bytes, sample.limit_bytes))
    }

    fn invalidate(&self) {
        if let Ok(mut sample) = self.sample.lock() {
            sample.valid = false;
        }
    }
}

fn resolve_cgroup_v2_directory(
    membership: &str,
    cgroup_mount: &Path,
) -> Result<PathBuf, MemoryPressureError> {
    let relative = membership
        .lines()
        .find_map(|line| line.strip_prefix("0::"))
        .ok_or(MemoryPressureError::CgroupV2Required)?;
    let relative = relative.trim_start_matches('/');
    let mut directory = cgroup_mount.to_path_buf();
    if !relative.is_empty() {
        directory.push(relative);
    }
    Ok(directory)
}

async fn read_sample(paths: &CgroupPaths) -> Result<CgroupSample, MemoryPressureError> {
    let (current, limit) = tokio::try_join!(
        tokio::fs::read_to_string(&paths.current),
        tokio::fs::read_to_string(&paths.limit),
    )
    .map_err(|error| MemoryPressureError::CgroupUnavailable(error.to_string()))?;
    let current_bytes = parse_cgroup_bytes(&current)
        .ok_or_else(|| MemoryPressureError::MalformedCgroupValue(paths.current.clone()))?;
    let limit_bytes = parse_cgroup_bytes(&limit)
        .ok_or_else(|| MemoryPressureError::MalformedCgroupValue(paths.limit.clone()))?;
    if limit.trim() == "max" || limit_bytes == 0 || limit_bytes >= usize::MAX / 2 {
        return Err(MemoryPressureError::UnlimitedCgroup);
    }
    Ok(CgroupSample {
        current_bytes,
        limit_bytes,
        updated_at: Instant::now(),
        valid: true,
    })
}

fn parse_cgroup_bytes(value: &str) -> Option<usize> {
    value
        .trim()
        .parse::<u64>()
        .ok()
        .and_then(|bytes| usize::try_from(bytes).ok())
}

fn is_admissible(
    current_bytes: usize,
    cgroup_limit_bytes: usize,
    additional_bytes: usize,
    process_limit_bytes: usize,
    headroom_bytes: usize,
) -> bool {
    current_bytes
        .checked_add(additional_bytes)
        .is_some_and(|next| {
            next <= cgroup_limit_bytes
                && next
                    .checked_add(headroom_bytes / 2)
                    .is_some_and(|with_margin| with_margin <= process_limit_bytes)
        })
}

/// Cgroup sampling and finite hard-limit failures.
#[derive(Debug, thiserror::Error)]
pub enum MemoryPressureError {
    /// Cgroup files could not be found or read.
    #[error("finite cgroup memory accounting is unavailable: {0}")]
    CgroupUnavailable(String),
    /// The process is not in a cgroup v2 hierarchy.
    #[error("replication network mode requires cgroup v2 memory accounting")]
    CgroupV2Required,
    /// The cgroup hard cap is unlimited or zero.
    #[error("replication network mode requires a finite cgroup memory.max")]
    UnlimitedCgroup,
    /// The finite cgroup hard cap is lower than the configured process cap.
    #[error("cgroup limit {cgroup_limit} is below process limit {process_limit}")]
    CgroupLimitBelowProcessLimit {
        cgroup_limit: usize,
        process_limit: usize,
    },
    /// A cgroup file did not contain a valid integer byte count.
    #[error("malformed cgroup memory file: {0}")]
    MalformedCgroupValue(PathBuf),
    /// The cgroup sample lock is poisoned.
    #[error("cgroup memory monitor state is poisoned")]
    MonitorPoisoned,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{MIN_REPLICATION_HEADROOM_BYTES, MIN_REPLICATION_PROCESS_BYTES};

    #[test]
    fn resolves_only_unified_cgroup_v2_membership() {
        assert_eq!(
            resolve_cgroup_v2_directory("0::/terrabase/server\n", Path::new("/cg"))
                .expect("v2 membership"),
            PathBuf::from("/cg/terrabase/server")
        );
        assert_eq!(
            resolve_cgroup_v2_directory("0::/\n", Path::new("/cg")).expect("root membership"),
            PathBuf::from("/cg")
        );
        assert!(matches!(
            resolve_cgroup_v2_directory("5:memory:/legacy\n", Path::new("/cg")),
            Err(MemoryPressureError::CgroupV2Required)
        ));
    }

    #[test]
    fn rejects_admission_above_process_margin_or_cgroup_limit() {
        let profile = ReplicationMemoryProfile::new(
            MIN_REPLICATION_PROCESS_BYTES,
            MIN_REPLICATION_HEADROOM_BYTES,
        )
        .expect("network profile");
        assert!(is_admissible(
            1024,
            8 * 1024 * 1024 * 1024,
            1024,
            profile.process_limit_bytes(),
            profile.headroom_bytes(),
        ));
        assert!(!is_admissible(
            profile.process_limit_bytes() - profile.headroom_bytes() / 2,
            8 * 1024 * 1024 * 1024,
            1,
            profile.process_limit_bytes(),
            profile.headroom_bytes(),
        ));
        assert!(!is_admissible(
            1024,
            1024,
            1,
            profile.process_limit_bytes(),
            profile.headroom_bytes(),
        ));
    }

    #[test]
    fn rejects_unlimited_cgroup_value() {
        assert!(parse_cgroup_bytes("max").is_none());
        assert_eq!(parse_cgroup_bytes("4096\n"), Some(4096));
    }
}
