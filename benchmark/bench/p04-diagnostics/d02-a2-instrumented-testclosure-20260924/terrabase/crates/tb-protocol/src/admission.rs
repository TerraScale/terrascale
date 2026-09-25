use std::sync::Arc;
use std::sync::Mutex;
use std::sync::atomic::{AtomicUsize, Ordering};

use thiserror::Error;

use crate::{
    MAX_IN_FLIGHT_BATCHES_PER_PEER, MAX_IN_FLIGHT_BYTES_PER_PEER, MAX_OPEN_STREAMS_PER_PEER, v1,
};

/// Explicit per-peer transport admission ceilings.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AdmissionLimits {
    /// Bytes reserved across encoded, decoded, queued, blocked, and in-flight work.
    in_flight_bytes: usize,
    /// Number of admitted application batches.
    in_flight_batches: usize,
    /// Number of simultaneously open replication streams.
    open_streams: usize,
}

impl AdmissionLimits {
    /// Validates finite v1 per-peer budgets.
    ///
    /// # Errors
    ///
    /// Returns [`AdmissionError::InvalidLimits`] for a zero or above-v1 budget.
    pub fn new(
        max_in_flight_bytes: usize,
        max_in_flight_batches: usize,
        max_open_streams: usize,
    ) -> Result<Self, AdmissionError> {
        if max_in_flight_bytes == 0
            || max_in_flight_bytes > MAX_IN_FLIGHT_BYTES_PER_PEER
            || max_in_flight_batches == 0
            || max_in_flight_batches > MAX_IN_FLIGHT_BATCHES_PER_PEER
            || max_open_streams == 0
            || max_open_streams > MAX_OPEN_STREAMS_PER_PEER
        {
            return Err(AdmissionError::InvalidLimits);
        }
        Ok(Self {
            in_flight_bytes: max_in_flight_bytes,
            in_flight_batches: max_in_flight_batches,
            open_streams: max_open_streams,
        })
    }

    /// Maximum in-flight application bytes.
    #[must_use]
    pub const fn max_in_flight_bytes(self) -> usize {
        self.in_flight_bytes
    }

    /// Maximum admitted batches.
    #[must_use]
    pub const fn max_in_flight_batches(self) -> usize {
        self.in_flight_batches
    }

    /// Maximum open streams.
    #[must_use]
    pub const fn max_open_streams(self) -> usize {
        self.open_streams
    }
}

/// Per-peer byte, batch, and stream admission controller.
///
/// Permits are RAII guards. Dropping a guard on cancellation, rejection, or
/// normal completion releases its local reservation without changing any
/// durable cursor.
#[derive(Clone, Debug)]
pub struct AdmissionController {
    shared: Arc<AdmissionState>,
}

#[derive(Debug)]
struct AdmissionState {
    limits: AdmissionLimits,
    reserved_bytes: AtomicUsize,
    in_flight_batches: AtomicUsize,
    open_streams: AtomicUsize,
}

impl AdmissionController {
    /// Creates a fresh per-peer admission budget.
    #[must_use]
    pub fn new(limits: AdmissionLimits) -> Self {
        Self {
            shared: Arc::new(AdmissionState {
                limits,
                reserved_bytes: AtomicUsize::new(0),
                in_flight_batches: AtomicUsize::new(0),
                open_streams: AtomicUsize::new(0),
            }),
        }
    }

    /// Attempts to reserve bytes for work not associated with one batch.
    ///
    /// # Errors
    ///
    /// Returns [`AdmissionError::ZeroReservation`] for zero bytes or
    /// [`AdmissionError::ByteCreditExhausted`] when the budget is full.
    pub fn try_reserve_bytes(&self, bytes: usize) -> Result<BytePermit, AdmissionError> {
        if bytes == 0 {
            return Err(AdmissionError::ZeroReservation);
        }
        reserve_bounded(
            &self.shared.reserved_bytes,
            self.shared.limits.in_flight_bytes,
            bytes,
            AdmissionError::ByteCreditExhausted,
        )?;
        Ok(BytePermit {
            shared: Arc::clone(&self.shared),
            bytes,
        })
    }

    /// Attempts to admit a batch and reserve its encoded/decoded working set.
    ///
    /// # Errors
    ///
    /// Returns [`AdmissionError::BatchLimitReached`] or
    /// [`AdmissionError::ByteCreditExhausted`] when a per-peer ceiling is full.
    pub fn try_admit_batch(&self, bytes: usize) -> Result<BatchPermit, AdmissionError> {
        reserve_bounded(
            &self.shared.in_flight_batches,
            self.shared.limits.in_flight_batches,
            1,
            AdmissionError::BatchLimitReached,
        )?;
        match self.try_reserve_bytes(bytes) {
            Ok(byte_permit) => Ok(BatchPermit {
                shared: Arc::clone(&self.shared),
                byte_permit: Some(byte_permit),
            }),
            Err(error) => {
                self.shared.in_flight_batches.fetch_sub(1, Ordering::AcqRel);
                Err(error)
            }
        }
    }

    /// Attempts to reserve one concurrent stream slot.
    ///
    /// # Errors
    ///
    /// Returns [`AdmissionError::StreamLimitReached`] when all stream slots are open.
    pub fn try_open_stream(&self) -> Result<StreamPermit, AdmissionError> {
        reserve_bounded(
            &self.shared.open_streams,
            self.shared.limits.open_streams,
            1,
            AdmissionError::StreamLimitReached,
        )?;
        Ok(StreamPermit {
            shared: Arc::clone(&self.shared),
        })
    }

    /// Number of application bytes currently reserved by admitted work.
    #[must_use]
    pub fn reserved_bytes(&self) -> usize {
        self.shared.reserved_bytes.load(Ordering::Acquire)
    }

    /// Number of admitted batches.
    #[must_use]
    pub fn in_flight_batches(&self) -> usize {
        self.shared.in_flight_batches.load(Ordering::Acquire)
    }

    /// Number of open streams.
    #[must_use]
    pub fn open_streams(&self) -> usize {
        self.shared.open_streams.load(Ordering::Acquire)
    }
}

/// A byte reservation retained for as long as its associated buffer/work lives.
#[derive(Debug)]
pub struct BytePermit {
    shared: Arc<AdmissionState>,
    bytes: usize,
}

impl BytePermit {
    /// Number of reserved bytes.
    #[must_use]
    pub const fn bytes(&self) -> usize {
        self.bytes
    }
}

impl Drop for BytePermit {
    fn drop(&mut self) {
        self.shared
            .reserved_bytes
            .fetch_sub(self.bytes, Ordering::AcqRel);
    }
}

/// An in-flight batch guard that releases both its item and byte reservations.
#[derive(Debug)]
pub struct BatchPermit {
    shared: Arc<AdmissionState>,
    byte_permit: Option<BytePermit>,
}

impl BatchPermit {
    /// Number of bytes reserved for the batch.
    #[must_use]
    pub fn bytes(&self) -> usize {
        self.byte_permit.as_ref().map_or(0, BytePermit::bytes)
    }
}

impl Drop for BatchPermit {
    fn drop(&mut self) {
        self.byte_permit.take();
        self.shared.in_flight_batches.fetch_sub(1, Ordering::AcqRel);
    }
}

/// An open-stream guard released when the RPC ends or is cancelled.
#[derive(Debug)]
pub struct StreamPermit {
    shared: Arc<AdmissionState>,
}

impl Drop for StreamPermit {
    fn drop(&mut self) {
        self.shared.open_streams.fetch_sub(1, Ordering::AcqRel);
    }
}

/// Per-stream sender window backed by application-level byte credits.
///
/// The window is intentionally not tied to HTTP/2 flow control. Drop it when a
/// stream ends; a reconnect starts with the credit advertised by its new hello.
#[derive(Debug)]
pub struct PeerCreditWindow {
    state: Mutex<PeerCreditState>,
}

#[derive(Debug)]
struct PeerCreditState {
    available: usize,
    pending: usize,
    in_flight: usize,
    closed: bool,
}

impl PeerCreditWindow {
    /// Creates a stream credit window from the peer's explicit initial grant.
    ///
    /// # Errors
    ///
    /// Returns [`CreditError::InvalidWindow`] for a zero, inverted, or above-v1 window.
    pub fn new(initial_credit: usize, maximum: usize) -> Result<Self, CreditError> {
        if initial_credit == 0
            || maximum == 0
            || maximum > MAX_IN_FLIGHT_BYTES_PER_PEER
            || initial_credit > maximum
        {
            return Err(CreditError::InvalidWindow);
        }
        Ok(Self {
            state: Mutex::new(PeerCreditState {
                available: initial_credit,
                pending: 0,
                in_flight: 0,
                closed: false,
            }),
        })
    }

    /// Reserves peer-granted application bytes before queueing a group.
    ///
    /// Drop the returned reservation if the send is cancelled before it is
    /// committed to the stream. After a successful enqueue, call
    /// [`PeerCreditPermit::mark_sent`]; only a later explicit `CreditUpdate`
    /// returns those bytes to the available window.
    ///
    /// # Errors
    ///
    /// Returns [`CreditError::InvalidGrant`], [`CreditError::Closed`], or
    /// [`CreditError::Exhausted`] when the reservation cannot be made.
    pub fn reserve(&self, bytes: usize) -> Result<PeerCreditPermit<'_>, CreditError> {
        if bytes == 0 {
            return Err(CreditError::InvalidGrant);
        }
        let mut state = self.lock_state();
        if state.closed {
            return Err(CreditError::Closed);
        }
        if state.available < bytes {
            return Err(CreditError::Exhausted);
        }
        state.available -= bytes;
        state.pending += bytes;
        Ok(PeerCreditPermit {
            window: self,
            bytes,
            sent: false,
        })
    }

    /// Applies a peer `CreditUpdate`, rejecting credit inflation beyond the
    /// negotiated per-stream maximum.
    ///
    /// # Errors
    ///
    /// Returns [`CreditError::InvalidGrant`], [`CreditError::Closed`], or
    /// [`CreditError::GrantExceedsWindow`] for an invalid update.
    pub fn apply_update(&self, update: &v1::CreditUpdate) -> Result<(), CreditError> {
        let bytes = usize::try_from(update.granted_bytes).map_err(|_| CreditError::InvalidGrant)?;
        if bytes == 0 {
            return Err(CreditError::InvalidGrant);
        }
        let mut state = self.lock_state();
        if state.closed {
            return Err(CreditError::Closed);
        }
        if bytes > state.in_flight {
            return Err(CreditError::GrantExceedsWindow);
        }
        state.in_flight -= bytes;
        state.available += bytes;
        Ok(())
    }

    /// Remaining peer-granted bytes.
    #[must_use]
    pub fn available(&self) -> usize {
        self.lock_state().available
    }

    /// Bytes currently committed to the stream and awaiting a peer credit update.
    #[must_use]
    pub fn in_flight(&self) -> usize {
        self.lock_state().in_flight
    }

    /// Prevents additional sends or grants after stream cancellation/shutdown.
    pub fn close(&self) {
        let mut state = self.lock_state();
        state.closed = true;
        state.available = 0;
        state.pending = 0;
        state.in_flight = 0;
    }

    fn lock_state(&self) -> std::sync::MutexGuard<'_, PeerCreditState> {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}

/// A local reservation from a peer's send-credit window.
#[derive(Debug)]
pub struct PeerCreditPermit<'a> {
    window: &'a PeerCreditWindow,
    bytes: usize,
    sent: bool,
}

impl PeerCreditPermit<'_> {
    /// Number of peer-granted bytes reserved for this group.
    #[must_use]
    pub const fn bytes(&self) -> usize {
        self.bytes
    }

    /// Commits the reservation after the group enters the outgoing stream.
    ///
    /// # Errors
    ///
    /// Returns [`CreditError::Closed`] for a stopped stream or
    /// [`CreditError::InvalidReservationState`] for a stale or duplicate permit.
    pub fn mark_sent(&mut self) -> Result<(), CreditError> {
        let mut state = self.window.lock_state();
        if state.closed {
            return Err(CreditError::Closed);
        }
        if self.sent || state.pending < self.bytes {
            return Err(CreditError::InvalidReservationState);
        }
        state.pending -= self.bytes;
        state.in_flight += self.bytes;
        self.sent = true;
        Ok(())
    }
}

impl Drop for PeerCreditPermit<'_> {
    fn drop(&mut self) {
        let mut state = self.window.lock_state();
        if !state.closed && !self.sent {
            state.pending -= self.bytes;
            state.available += self.bytes;
        }
    }
}

/// Peer-credit window error.
#[derive(Clone, Copy, Debug, Eq, Error, PartialEq)]
pub enum CreditError {
    /// The initial or maximum credit is zero, inverted, or above the v1 cap.
    #[error("invalid application byte credit window")]
    InvalidWindow,
    /// A zero-byte update or reservation is invalid.
    #[error("zero-byte credit operation is invalid")]
    InvalidGrant,
    /// The peer's current grant has been consumed.
    #[error("peer application byte credit is exhausted")]
    Exhausted,
    /// A credit update would exceed the negotiated maximum.
    #[error("peer credit update exceeds the negotiated window")]
    GrantExceedsWindow,
    /// The stream credit window has been closed.
    #[error("peer application byte credit window is closed")]
    Closed,
    /// A reservation was committed more than once or no longer exists.
    #[error("invalid peer credit reservation state")]
    InvalidReservationState,
}

/// A bounded-resource admission failure.
#[derive(Clone, Copy, Debug, Eq, Error, PartialEq)]
pub enum AdmissionError {
    /// A zero or above-v1 resource limit was configured.
    #[error("invalid per-peer transport admission limits")]
    InvalidLimits,
    /// A zero-byte work reservation is not meaningful.
    #[error("zero-byte reservations are not admitted")]
    ZeroReservation,
    /// The requested byte reservation exceeds the available byte budget.
    #[error("per-peer application byte credit is exhausted")]
    ByteCreditExhausted,
    /// The batch count ceiling has been reached.
    #[error("per-peer in-flight batch limit is reached")]
    BatchLimitReached,
    /// The open stream ceiling has been reached.
    #[error("per-peer open stream limit is reached")]
    StreamLimitReached,
}

fn reserve_bounded(
    counter: &AtomicUsize,
    limit: usize,
    amount: usize,
    exhausted: AdmissionError,
) -> Result<(), AdmissionError> {
    let mut current = counter.load(Ordering::Acquire);
    loop {
        let Some(next) = current.checked_add(amount) else {
            return Err(exhausted);
        };
        if next > limit {
            return Err(exhausted);
        }
        match counter.compare_exchange_weak(current, next, Ordering::AcqRel, Ordering::Acquire) {
            Ok(_) => return Ok(()),
            Err(observed) => current = observed,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn byte_batch_and_stream_permits_release_when_dropped() {
        let admission =
            AdmissionController::new(AdmissionLimits::new(1_024, 2, 1).expect("valid limits"));
        let stream = admission.try_open_stream().expect("open stream");
        let batch = admission.try_admit_batch(512).expect("admit batch");
        assert_eq!(admission.open_streams(), 1);
        assert_eq!(admission.in_flight_batches(), 1);
        assert_eq!(admission.reserved_bytes(), 512);
        assert!(matches!(
            admission.try_open_stream(),
            Err(AdmissionError::StreamLimitReached)
        ));
        assert!(matches!(
            admission.try_admit_batch(600),
            Err(AdmissionError::ByteCreditExhausted)
        ));

        drop(batch);
        drop(stream);
        assert_eq!(admission.open_streams(), 0);
        assert_eq!(admission.in_flight_batches(), 0);
        assert_eq!(admission.reserved_bytes(), 0);
    }

    #[test]
    fn peer_credits_return_only_from_updates_or_unsent_cancel() {
        let window = PeerCreditWindow::new(500, 1_000).expect("valid credit window");
        {
            let reservation = window.reserve(400).expect("reserve credits");
            assert_eq!(window.available(), 100);
            drop(reservation);
        }
        assert_eq!(window.available(), 500);

        let mut sent = window.reserve(400).expect("reserve send");
        sent.mark_sent().expect("commit send");
        drop(sent);
        assert_eq!(window.available(), 100);
        assert_eq!(window.in_flight(), 400);
        assert!(matches!(
            window.apply_update(&v1::CreditUpdate { granted_bytes: 401 }),
            Err(CreditError::GrantExceedsWindow)
        ));
        window
            .apply_update(&v1::CreditUpdate { granted_bytes: 300 })
            .expect("return part of the send credit");
        window
            .apply_update(&v1::CreditUpdate { granted_bytes: 100 })
            .expect("return remaining send credit");
        assert_eq!(window.available(), 500);
        assert_eq!(window.in_flight(), 0);

        window.close();
        assert!(matches!(window.reserve(1), Err(CreditError::Closed)));
    }
}
