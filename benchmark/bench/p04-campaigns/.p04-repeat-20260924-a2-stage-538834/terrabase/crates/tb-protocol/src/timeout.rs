use std::future::Future;
use std::time::Duration;

use thiserror::Error;

/// Largest v1 call, handshake, or stream-progress deadline.
pub const MAX_TRANSPORT_TIMEOUT: Duration = Duration::from_secs(60 * 60);

/// Finite timeout policy for handshake calls, bounded RPCs, and stream progress.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TransportTimeouts {
    handshake_deadline: Duration,
    bounded_call_deadline: Duration,
    stream_progress_timeout: Duration,
}

impl TransportTimeouts {
    /// Rejects zero or unreasonably large timeouts before starting transport work.
    ///
    /// # Errors
    ///
    /// Returns [`ProgressTimeout::Invalid`] when any duration is zero or above
    /// the v1 maximum.
    pub fn new(
        handshake_deadline: Duration,
        bounded_call_deadline: Duration,
        stream_progress_timeout: Duration,
    ) -> Result<Self, ProgressTimeout> {
        for timeout in [
            handshake_deadline,
            bounded_call_deadline,
            stream_progress_timeout,
        ] {
            validate_timeout(timeout)?;
        }
        Ok(Self {
            handshake_deadline,
            bounded_call_deadline,
            stream_progress_timeout,
        })
    }

    /// Deadline applied to handshake RPCs.
    #[must_use]
    pub const fn handshake_deadline(self) -> Duration {
        self.handshake_deadline
    }

    /// Deadline applied to bounded unary/control RPCs.
    #[must_use]
    pub const fn bounded_call_deadline(self) -> Duration {
        self.bounded_call_deadline
    }

    /// Maximum idle time between progress operations on a long-lived stream.
    #[must_use]
    pub const fn stream_progress_timeout(self) -> Duration {
        self.stream_progress_timeout
    }
}

/// Invalid or elapsed operation deadline.
#[derive(Clone, Copy, Debug, Eq, Error, PartialEq)]
pub enum ProgressTimeout {
    /// A deadline must be positive and no longer than the v1 ceiling.
    #[error("transport timeout must be positive and no longer than one hour")]
    Invalid,
    /// No stream progress arrived before the configured timeout.
    #[error("replication stream made no progress before its timeout")]
    Elapsed,
}

/// Adds an explicit finite deadline to a Tonic request.
///
/// # Errors
///
/// Returns [`ProgressTimeout::Invalid`] when the deadline is zero or above the
/// v1 maximum.
pub fn apply_request_deadline<T>(
    request: &mut tonic::Request<T>,
    deadline: Duration,
) -> Result<(), ProgressTimeout> {
    validate_timeout(deadline)?;
    request.set_timeout(deadline);
    Ok(())
}

/// Times one stream poll/send operation; timeout cancellation drops its future
/// and any RAII byte/stream reservations captured by that future.
///
/// # Errors
///
/// Returns [`ProgressTimeout::Invalid`] for a zero or oversized timeout, or
/// [`ProgressTimeout::Elapsed`] when the future makes no progress in time.
pub async fn with_progress_timeout<F>(
    timeout: Duration,
    future: F,
) -> Result<F::Output, ProgressTimeout>
where
    F: Future,
{
    validate_timeout(timeout)?;
    tokio::time::timeout(timeout, future)
        .await
        .map_err(|_| ProgressTimeout::Elapsed)
}

fn validate_timeout(timeout: Duration) -> Result<(), ProgressTimeout> {
    if timeout.is_zero() || timeout > MAX_TRANSPORT_TIMEOUT {
        return Err(ProgressTimeout::Invalid);
    }
    Ok(())
}
