//! The attachment content plane (GAP-F01).
//!
//! The attachments family of the typed seam records attachment *metadata* —
//! names, content types, sizes, digests, document ownership. This module
//! adds the engine-side half that metadata alone cannot provide: byte
//! storage for attachment content, bounded segment-by-segment readers, V1's
//! range and disposal semantics, and explicit size limits. It is the plane
//! the PB-3007/PB-3045 backup and migration halves read bytes from, and the
//! plane GAP-F5's attachment snapshots will read history from.
//!
//! Content is digest-addressed and deduplicated — V1's model, where one
//! stored stream serves every attachment whose bytes hash to the same
//! digest. Bytes live in the reserved family collection as one manifest
//! record plus bounded base64 segment records, staged through the caller's
//! Engine transaction, so content inherits tenant isolation, atomicity, and
//! durability from the family seam without any new storage format. Readers
//! decode at most one segment at a time and never materialize whole
//! content; over-limit content is refused by a typed error instead.
//!
//! The plane is attachment content, not a generic byte escape hatch: it is
//! reachable only through [`FamilyStore`](super::FamilyStore), is
//! tenant-scoped, is bounded by the explicit limits below, and exposes no
//! page, frame, or codec buffer. The V1-observable semantics pinned here and
//! their pinned-commit citations are recorded in
//! `docs/families/attachment-content-plane.md`.

pub(crate) mod codec;
mod content;

#[cfg(test)]
mod tests;

pub use content::{
    AttachmentContentDelete, AttachmentContentError, AttachmentContentRead,
    AttachmentContentReader, AttachmentContentRecorded, AttachmentContentWrite, AttachmentRange,
    AttachmentSegment, MAX_CONTENT_BYTES, MAX_SEGMENT_BYTES, MAX_SEGMENT_COUNT,
};
