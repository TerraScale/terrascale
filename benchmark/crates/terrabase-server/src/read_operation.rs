//! Unique reservations for read transactions, which never commit their IDs.
use std::sync::atomic::{AtomicU64, Ordering};

pub(crate) fn next() -> Result<terrabase::types::OperationId, ()> {
    static NEXT_READ: AtomicU64 = AtomicU64::new(0);
    let sequence = NEXT_READ
        .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |value| value.checked_add(1))
        .map_err(|_| ())?;
    terrabase::types::OperationId::try_new(&format!("server-read-{sequence}")).map_err(|_| ())
}
