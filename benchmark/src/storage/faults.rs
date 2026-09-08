//! Private fault-injection control surface of the storage adapter.
//!
//! The plan is compiled into the storage adapter so the commit path has one
//! implementation, but only `cfg(test)` code inside this crate can arm a
//! fault. Production callers never see this type: it is not re-exported from
//! the crate root, and an unarmed plan never fires.

/// One named point in the commit path where a test may inject a failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum FaultPoint {
    /// Fail the next write transaction inside the `SQLite` commit.
    WalAppend,
    /// Fail restoring the writer connection's synchronous mode after commit.
    PostCommitSync,
}

/// The armed plan. `None` means no fault fires, which is the only state
/// production code can observe.
#[derive(Default)]
pub(crate) struct FaultPlan {
    armed: Option<FaultPoint>,
}

impl FaultPlan {
    /// Creates an unarmed plan.
    pub(crate) const fn empty() -> Self {
        Self { armed: None }
    }

    /// Fires a one-shot fault when `point` is armed.
    pub(crate) fn fire(&mut self, point: FaultPoint) -> Result<(), FaultPoint> {
        if self.armed == Some(point) {
            self.armed = None;
            return Err(point);
        }
        Ok(())
    }

    /// Arms one one-shot fault. Test-only by visibility.
    #[cfg(test)]
    pub(crate) fn arm(&mut self, point: FaultPoint) {
        self.armed = Some(point);
    }
}
