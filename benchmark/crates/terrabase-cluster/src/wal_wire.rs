//! Database-scoped physical WAL batches for authenticated member QUIC frames.
//!
//! Decoding validates framing and scope syntax, not placement authority. The
//! receiver must match the scope and replica epoch to its accepted placement
//! before calling the follower. WAL checksum validation remains in the follower.

use crate::production::DatabaseRouteScope;
use crate::wal_follower::{
    FollowerAck, WalFrames, WalHeader, WalTail, MAX_WAL_BATCH_BYTES, WAL_HEADER_BYTES,
};

const MAGIC: &[u8; 8] = b"TBWAL\0\0\x01";
const PREFIX_BYTES: usize = 8 + 3 * 26 + 8 + 8 + 8 + WAL_HEADER_BYTES;
/// Maximum encoded batch size, including its fixed routing prefix.
pub const MAX_WAL_WIRE_BYTES: usize = PREFIX_BYTES + MAX_WAL_BATCH_BYTES;

/// Physical replication message bound to one database and writer epoch.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScopedWalBatch {
    /// Immutable tenant, project, and database identity.
    pub scope: DatabaseRouteScope,
    /// Accepted writer replica epoch; zero is never an admitted epoch.
    pub replica_epoch: u64,
    /// Exact SQLite header and frame bytes.
    pub frames: WalFrames,
}

/// A rejected physical WAL wire message.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WalWireError {
    /// Unknown version, truncation, trailing partial frame, or invalid header.
    InvalidFrame,
    /// Routing identifiers are not canonical ULIDs or the epoch is zero.
    InvalidScope,
    /// The payload exceeds the bounded physical batch size.
    Oversized,
    /// Frame offsets or commit boundaries are impossible.
    InvalidPosition,
}

impl ScopedWalBatch {
    /// Encodes one bounded batch without transforming the SQLite bytes.
    ///
    /// # Errors
    /// Returns a wire error for invalid scope, size, header, or positions.
    pub fn encode(&self) -> Result<Vec<u8>, WalWireError> {
        self.validate()?;
        let mut bytes = Vec::with_capacity(PREFIX_BYTES + self.frames.bytes.len());
        bytes.extend_from_slice(MAGIC);
        for id in [
            self.scope.tenant().to_text(),
            self.scope.project().to_text(),
            self.scope.database().to_text(),
        ] {
            bytes.extend_from_slice(id.as_bytes());
        }
        bytes.extend_from_slice(&self.replica_epoch.to_be_bytes());
        bytes.extend_from_slice(
            &u64::try_from(self.frames.from_frame)
                .map_err(|_| WalWireError::InvalidPosition)?
                .to_be_bytes(),
        );
        let committed = self
            .frames
            .committed_through
            .map(u64::try_from)
            .transpose()
            .map_err(|_| WalWireError::InvalidPosition)?
            .unwrap_or(0);
        bytes.extend_from_slice(&committed.to_be_bytes());
        bytes.extend_from_slice(&self.frames.header);
        bytes.extend_from_slice(&self.frames.bytes);
        Ok(bytes)
    }

    /// Decodes a complete QUIC frame with bounded allocation.
    ///
    /// # Errors
    /// Returns a wire error before allocating payload storage if framing,
    /// routing, header, size, or positions are invalid.
    pub fn decode(bytes: &[u8]) -> Result<Self, WalWireError> {
        if bytes.len() > MAX_WAL_WIRE_BYTES {
            return Err(WalWireError::Oversized);
        }
        if bytes.len() < PREFIX_BYTES || &bytes[..8] != MAGIC {
            return Err(WalWireError::InvalidFrame);
        }
        let id = |start| {
            std::str::from_utf8(&bytes[start..start + 26]).map_err(|_| WalWireError::InvalidScope)
        };
        let scope = DatabaseRouteScope::try_new(id(8)?, id(34)?, id(60)?)
            .map_err(|_| WalWireError::InvalidScope)?;
        let number = |start| {
            let mut value = [0; 8];
            value.copy_from_slice(&bytes[start..start + 8]);
            u64::from_be_bytes(value)
        };
        let replica_epoch = number(86);
        let from_frame = usize::try_from(number(94)).map_err(|_| WalWireError::InvalidPosition)?;
        let committed = number(102);
        let committed_through = if committed == 0 {
            None
        } else {
            Some(usize::try_from(committed).map_err(|_| WalWireError::InvalidPosition)?)
        };
        validate_parts(
            replica_epoch,
            from_frame,
            committed_through,
            &bytes[110..PREFIX_BYTES],
            &bytes[PREFIX_BYTES..],
        )?;
        Ok(Self {
            scope,
            replica_epoch,
            frames: WalFrames {
                header: bytes[110..PREFIX_BYTES].to_vec(),
                from_frame,
                bytes: bytes[PREFIX_BYTES..].to_vec(),
                committed_through,
            },
        })
    }

    pub(crate) fn validate(&self) -> Result<(), WalWireError> {
        validate_parts(
            self.replica_epoch,
            self.frames.from_frame,
            self.frames.committed_through,
            &self.frames.header,
            &self.frames.bytes,
        )
    }
}

fn validate_parts(
    epoch: u64,
    from: usize,
    committed: Option<usize>,
    header: &[u8],
    bytes: &[u8],
) -> Result<(), WalWireError> {
    if epoch == 0 {
        return Err(WalWireError::InvalidScope);
    }
    if bytes.len() > MAX_WAL_BATCH_BYTES {
        return Err(WalWireError::Oversized);
    }
    if header.len() != WAL_HEADER_BYTES {
        return Err(WalWireError::InvalidFrame);
    }
    let parsed = WalHeader::parse(header).map_err(|_| WalWireError::InvalidFrame)?;
    if bytes.is_empty() || !bytes.len().is_multiple_of(parsed.frame_size()) {
        return Err(WalWireError::InvalidFrame);
    }
    let through =
        from.checked_add(bytes.len() / parsed.frame_size()).ok_or(WalWireError::InvalidPosition)?;
    if committed.is_some_and(|value| value <= from || value > through) {
        return Err(WalWireError::InvalidPosition);
    }
    Ok(())
}

const ACK_MAGIC: &[u8; 8] = b"TBWACK\0\x01";
const ACK_BYTES: usize = PREFIX_BYTES + 8;

/// A follower's durable receipt, scoped to its database and replica epoch.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScopedWalAck {
    scope: DatabaseRouteScope,
    replica_epoch: u64,
    header: Vec<u8>,
    receipt: FollowerAck,
}

impl ScopedWalAck {
    /// Binds a successful follower apply to the received batch.
    ///
    /// # Errors
    /// Rejects non-durable receipts or a receipt for a different batch.
    pub fn from_follower(
        batch: &ScopedWalBatch,
        receipt: FollowerAck,
    ) -> Result<Self, WalWireError> {
        batch.validate()?;
        let header =
            WalHeader::parse(&batch.frames.header).map_err(|_| WalWireError::InvalidFrame)?;
        let through = batch.frames.from_frame + batch.frames.bytes.len() / header.frame_size();
        if !receipt.fdatasynced
            || receipt.header != header
            || receipt.applied != through
            || receipt.committed_through.is_some_and(|commit| commit == 0 || commit > through)
            || batch
                .frames
                .committed_through
                .is_some_and(|commit| receipt.committed_through != Some(commit))
        {
            return Err(WalWireError::InvalidPosition);
        }
        Ok(Self {
            scope: batch.scope,
            replica_epoch: batch.replica_epoch,
            header: batch.frames.header.clone(),
            receipt,
        })
    }

    /// Encodes the successful durable receipt. Failed syncs have no wire receipt.
    ///
    /// # Errors
    /// Returns an error when a platform frame count cannot fit the wire integer.
    pub fn encode(&self) -> Result<Vec<u8>, WalWireError> {
        let mut bytes = Vec::with_capacity(ACK_BYTES);
        bytes.extend_from_slice(ACK_MAGIC);
        for id in [
            self.scope.tenant().to_text(),
            self.scope.project().to_text(),
            self.scope.database().to_text(),
        ] {
            bytes.extend_from_slice(id.as_bytes());
        }
        for value in [
            self.replica_epoch,
            u64::try_from(self.receipt.applied).map_err(|_| WalWireError::InvalidPosition)?,
            u64::try_from(self.receipt.committed_through.unwrap_or(0))
                .map_err(|_| WalWireError::InvalidPosition)?,
        ] {
            bytes.extend_from_slice(&value.to_be_bytes());
        }
        bytes.extend_from_slice(&self.header);
        bytes.extend_from_slice(&self.receipt.chain.0.to_be_bytes());
        bytes.extend_from_slice(&self.receipt.chain.1.to_be_bytes());
        Ok(bytes)
    }

    /// Decodes one exact durable receipt from an authenticated follower.
    ///
    /// # Errors
    /// Rejects unknown versions, invalid scope, positions, and malformed headers.
    pub fn decode(bytes: &[u8]) -> Result<Self, WalWireError> {
        if bytes.len() != ACK_BYTES || &bytes[..8] != ACK_MAGIC {
            return Err(WalWireError::InvalidFrame);
        }
        let id = |start| {
            std::str::from_utf8(&bytes[start..start + 26]).map_err(|_| WalWireError::InvalidScope)
        };
        let scope = DatabaseRouteScope::try_new(id(8)?, id(34)?, id(60)?)
            .map_err(|_| WalWireError::InvalidScope)?;
        let number = |start| {
            let mut value = [0; 8];
            value.copy_from_slice(&bytes[start..start + 8]);
            u64::from_be_bytes(value)
        };
        let replica_epoch = number(86);
        if replica_epoch == 0 {
            return Err(WalWireError::InvalidScope);
        }
        let applied = usize::try_from(number(94)).map_err(|_| WalWireError::InvalidPosition)?;
        let committed = usize::try_from(number(102)).map_err(|_| WalWireError::InvalidPosition)?;
        if applied == 0 || committed > applied {
            return Err(WalWireError::InvalidPosition);
        }
        let header = bytes[110..PREFIX_BYTES].to_vec();
        let parsed = WalHeader::parse(&header).map_err(|_| WalWireError::InvalidFrame)?;
        let word = |start| {
            let mut value = [0; 4];
            value.copy_from_slice(&bytes[start..start + 4]);
            u32::from_be_bytes(value)
        };
        Ok(Self {
            scope,
            replica_epoch,
            header,
            receipt: FollowerAck {
                header: parsed,
                applied,
                chain: (word(PREFIX_BYTES), word(PREFIX_BYTES + 4)),
                committed_through: (committed != 0).then_some(committed),
                fdatasynced: true,
            },
        })
    }

    /// Verifies scope and epoch before advancing the writer's durable checkpoint.
    /// The caller must authenticate the peer against the accepted follower placement.
    ///
    /// # Errors
    /// Rejects foreign scopes, stale epochs, or receipts that do not match the pending tail batch.
    pub fn acknowledge(
        self,
        scope: DatabaseRouteScope,
        replica_epoch: u64,
        tail: &mut WalTail,
    ) -> Result<(), WalWireError> {
        if self.scope != scope || self.replica_epoch != replica_epoch {
            return Err(WalWireError::InvalidScope);
        }
        tail.acknowledge(self.receipt).map_err(|_| WalWireError::InvalidPosition)
    }
}
