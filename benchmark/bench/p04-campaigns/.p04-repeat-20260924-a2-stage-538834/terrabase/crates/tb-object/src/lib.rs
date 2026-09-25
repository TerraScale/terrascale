//! Immutable remote objects with bounded transfers and verified block reads.
//!
//! Object-durable acknowledgment is intentionally unavailable until a caller
//! supplies and qualifies both conditional manifest publication and fenced
//! ownership. Immutable uploads are still useful as storage primitives: each
//! upload gets a fresh key, verifies its content digest, and can only be made
//! reachable by a separately qualified manifest authority.

#![forbid(unsafe_code)]

mod client;
mod error;
mod types;

pub use client::{ObjectClient, UploadSession};
pub use error::{ObjectCapability, ObjectError, ObjectGuarantee};
pub use types::{
    ContentDigest, ObjectKey, ObjectLimits, ObjectReference, ProviderCapabilities, TransferOwner,
    VerifiedBlock, VerifiedObject,
};
