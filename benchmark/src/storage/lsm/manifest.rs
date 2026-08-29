//! The segment manifest of the LSM engine (P1).
//!
//! The manifest names the immutable segments that make up the durable
//! database, the next segment identity, and the WAL checkpoint — the
//! sequence through which every entry lives in a named segment. Publication
//! is the crash-safe pointer pattern the W3-1 root store proved: write a
//! complete temporary file, fsync it, atomically rename it over the live
//! manifest, then fsync the directory. A torn publication can only ever
//! leave the previous epoch's file in place.
//!
//! Recovery treats the manifest as an anchor, never as a source of truth
//! beyond verification: a manifest that does not verify is discarded and the
//! segment set is rebuilt from the verifying segment files on disk, with
//! the WAL replaying everything above the rebuilt state (replay is
//! idempotent by internal key).

use std::{
    fs,
    io::Write,
    path::{Path, PathBuf},
};

use super::super::codec;
use super::segment::SegmentMeta;

const MANIFEST_MAGIC: &[u8; 8] = b"TBLSMMF1";

/// The published manifest state.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub(super) struct SegmentManifest {
    /// Monotonic publication epoch.
    pub(super) epoch: u64,
    /// The next segment identity to allocate.
    pub(super) next_segment_id: u64,
    /// The sequence through which every entry lives in a named segment.
    pub(super) wal_checkpoint: u64,
    /// The named segments, in no particular order.
    pub(super) segments: Vec<SegmentMeta>,
}

impl SegmentManifest {
    /// Allocates the next segment identity.
    pub(super) fn allocate_segment_id(&mut self) -> u64 {
        let id = self.next_segment_id;
        self.next_segment_id += 1;
        id
    }

    /// Encodes the manifest body.
    fn encode(&self) -> Vec<u8> {
        let mut body = Vec::new();
        body.extend_from_slice(MANIFEST_MAGIC);
        codec::push_u64(&mut body, self.epoch);
        codec::push_u64(&mut body, self.next_segment_id);
        codec::push_u64(&mut body, self.wal_checkpoint);
        #[allow(clippy::cast_possible_truncation)]
        let count = self.segments.len() as u32;
        codec::push_u32(&mut body, count);
        for segment in &self.segments {
            codec::push_u64(&mut body, segment.id);
            codec::push_u16(&mut body, segment.level);
            codec::push_u16(&mut body, 0);
            codec::push_u64(&mut body, segment.entry_count);
            codec::push_u64(&mut body, segment.byte_len);
            #[allow(clippy::cast_possible_truncation)]
            let min_len = segment.min_key.len() as u32;
            codec::push_u32(&mut body, min_len);
            body.extend_from_slice(&segment.min_key);
            #[allow(clippy::cast_possible_truncation)]
            let max_len = segment.max_key.len() as u32;
            codec::push_u32(&mut body, max_len);
            body.extend_from_slice(&segment.max_key);
        }
        let mut out = body.clone();
        codec::push_u64(&mut out, codec::fnv1a(&body));
        out
    }

    /// Decodes and verifies one manifest body.
    fn decode(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 8 + 8 + 8 + 8 + 8 || &bytes[..8] != MANIFEST_MAGIC {
            return None;
        }
        let body_end = bytes.len() - 8;
        let expected = codec::fnv1a(&bytes[..body_end]);
        let stored = u64::from_le_bytes(bytes[body_end..].try_into().expect("eight bytes"));
        if expected != stored {
            return None;
        }
        let mut reader = codec::Reader::new(&bytes[8..body_end]);
        let epoch = reader.read_u64()?;
        let next_segment_id = reader.read_u64()?;
        let wal_checkpoint = reader.read_u64()?;
        let count = reader.read_u32()? as usize;
        let mut segments = Vec::with_capacity(count.min(1024));
        for _ in 0..count {
            let id = reader.read_u64()?;
            let level = reader.read_u16()?;
            let _reserved = reader.read_u16()?;
            let entry_count = reader.read_u64()?;
            let byte_len = reader.read_u64()?;
            let min_len = reader.read_u32()? as usize;
            let min_key = reader.take(min_len)?.to_owned();
            let max_len = reader.read_u32()? as usize;
            let max_key = reader.take(max_len)?.to_owned();
            segments.push(SegmentMeta { id, level, entry_count, byte_len, min_key, max_key });
        }
        if reader.remaining() != 0 {
            return None;
        }
        Some(Self { epoch, next_segment_id, wal_checkpoint, segments })
    }

    /// Publishes the manifest atomically at `dir/MANIFEST`.
    pub(super) fn publish(&self, dir: &Path) -> std::io::Result<()> {
        let encoded = self.encode();
        let tmp: PathBuf = dir.join("MANIFEST.tmp");
        let live: PathBuf = dir.join("MANIFEST");
        {
            let mut file = fs::File::create(&tmp)?;
            file.write_all(&encoded)?;
            file.sync_all()?;
        }
        fs::rename(&tmp, &live)?;
        if let Ok(dir_handle) = fs::File::open(dir) {
            let _ = dir_handle.sync_all();
        }
        Ok(())
    }

    /// Reads and verifies the manifest at `dir/MANIFEST`, if one exists and
    /// verifies.
    pub(super) fn read(dir: &Path) -> Option<Self> {
        let bytes = fs::read(dir.join("MANIFEST")).ok()?;
        Self::decode(&bytes)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn meta(id: u64, level: u16, min: &[u8], max: &[u8]) -> SegmentMeta {
        SegmentMeta {
            id,
            level,
            entry_count: id * 10,
            byte_len: id * 100,
            min_key: min.to_vec(),
            max_key: max.to_vec(),
        }
    }

    fn dir(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("tbv2-lsm-mf-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn publish_and_read_round_trip() {
        let dir = dir("rt");
        let manifest = SegmentManifest {
            epoch: 3,
            next_segment_id: 9,
            wal_checkpoint: 4242,
            segments: vec![meta(1, 0, b"a", b"m"), meta(7, 1, b"n", b"z")],
        };
        manifest.publish(&dir).unwrap();
        let read = SegmentManifest::read(&dir).unwrap();
        assert_eq!(read, manifest);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn corruption_reads_as_none() {
        let dir = dir("corrupt");
        let manifest =
            SegmentManifest { epoch: 1, next_segment_id: 2, wal_checkpoint: 5, segments: vec![] };
        manifest.publish(&dir).unwrap();
        let path = dir.join("MANIFEST");
        let mut bytes = std::fs::read(&path).unwrap();
        let mid = bytes.len() / 2;
        bytes[mid] ^= 0xFF;
        std::fs::write(&path, &bytes).unwrap();
        assert!(SegmentManifest::read(&dir).is_none());
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn segment_ids_allocate_monotonically() {
        let mut manifest = SegmentManifest::default();
        assert_eq!(manifest.allocate_segment_id(), 0);
        assert_eq!(manifest.allocate_segment_id(), 1);
        assert_eq!(manifest.next_segment_id, 2);
    }
}
