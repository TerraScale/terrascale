use std::sync::atomic::{AtomicU8, Ordering};

/// Monotonic process lifecycle used by health and readiness handlers.
pub struct Lifecycle {
    phase: AtomicU8,
}

impl Lifecycle {
    const STARTING: u8 = 0;
    const READY: u8 = 1;
    const DRAINING: u8 = 2;
    const FAILED: u8 = 3;
    const STOPPED: u8 = 4;

    /// Create a lifecycle that remains unready until local storage has opened.
    #[must_use]
    pub const fn new() -> Self {
        Self {
            phase: AtomicU8::new(Self::STARTING),
        }
    }

    /// Mark the process ready after local durable storage recovery succeeds.
    ///
    /// # Errors
    ///
    /// Returns [`LifecycleError`] unless the process is still starting.
    pub fn mark_ready(&self) -> Result<(), LifecycleError> {
        self.phase
            .compare_exchange(
                Self::STARTING,
                Self::READY,
                Ordering::AcqRel,
                Ordering::Acquire,
            )
            .map(|_| ())
            .map_err(|phase| LifecycleError {
                phase: Self::phase_name(phase),
            })
    }

    /// Stop readiness and reject new work while bounded in-flight work drains.
    ///
    /// Returns `true` when this call entered or observed the draining state.
    pub fn begin_draining(&self) -> bool {
        match self.phase.compare_exchange(
            Self::READY,
            Self::DRAINING,
            Ordering::AcqRel,
            Ordering::Acquire,
        ) {
            Ok(_) => true,
            Err(phase) => phase == Self::DRAINING,
        }
    }

    /// Mark a known fatal runtime condition; readiness and liveness both fail.
    pub fn mark_failed(&self) {
        let mut phase = self.phase.load(Ordering::Acquire);
        while phase != Self::STOPPED && phase != Self::FAILED {
            match self.phase.compare_exchange_weak(
                phase,
                Self::FAILED,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => return,
                Err(observed) => phase = observed,
            }
        }
    }

    /// Mark process shutdown complete.
    pub fn mark_stopped(&self) {
        self.phase.store(Self::STOPPED, Ordering::Release);
    }

    /// Whether local storage is open and the process is accepting work.
    #[must_use]
    pub fn is_ready(&self) -> bool {
        self.phase.load(Ordering::Acquire) == Self::READY
    }

    /// Whether the process can still serve health while running or draining.
    #[must_use]
    pub fn is_live(&self) -> bool {
        matches!(
            self.phase.load(Ordering::Acquire),
            Self::READY | Self::DRAINING
        )
    }

    const fn phase_name(phase: u8) -> &'static str {
        match phase {
            Self::STARTING => "starting",
            Self::READY => "ready",
            Self::DRAINING => "draining",
            Self::FAILED => "failed",
            Self::STOPPED => "stopped",
            _ => "unknown",
        }
    }
}

impl Default for Lifecycle {
    fn default() -> Self {
        Self::new()
    }
}

/// Invalid attempt to transition the process lifecycle to ready.
#[derive(Clone, Copy, Debug, Eq, PartialEq, thiserror::Error)]
#[error("cannot mark server ready from lifecycle phase {phase}")]
pub struct LifecycleError {
    phase: &'static str,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn readiness_tracks_storage_startup_and_graceful_drain() {
        let lifecycle = Lifecycle::new();
        assert!(!lifecycle.is_ready());
        assert!(!lifecycle.is_live());

        lifecycle.mark_ready().expect("storage opened");
        assert!(lifecycle.is_ready());
        assert!(lifecycle.is_live());

        assert!(lifecycle.begin_draining());
        assert!(lifecycle.begin_draining());
        assert!(!lifecycle.is_ready());
        assert!(lifecycle.is_live());

        lifecycle.mark_stopped();
        assert!(!lifecycle.is_live());
    }

    #[test]
    fn failed_process_cannot_become_ready() {
        let lifecycle = Lifecycle::new();
        lifecycle.mark_failed();

        assert!(!lifecycle.is_ready());
        assert!(!lifecycle.is_live());
        assert_eq!(
            lifecycle.mark_ready(),
            Err(LifecycleError { phase: "failed" })
        );
    }
}
