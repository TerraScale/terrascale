//! Causal bookmarks and the `fast` / `strong` consistency modes shared by SQL
//! over HTTP, the SQL WebSocket and the PostgreSQL wire.
//!
//! Every region of a database is an equal active-active peer. A
//! [`ConsistencyMode::Fast`] write commits in the region that received it and
//! replicates to its peers; a [`ConsistencyMode::Strong`] write commits on a
//! 2-of-3 regional quorum. Each SQL answer carries a `TerraScale-Bookmark`;
//! sending it back on later requests lets any region serve your own writes.

use std::collections::BTreeMap;
use std::fmt;
use std::str::FromStr;
use std::sync::{Arc, RwLock};

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine as _;
use serde::{Deserialize, Serialize};

/// The request and response header carrying a causal bookmark.
pub const BOOKMARK_HEADER: &str = "TerraScale-Bookmark";
/// The request and response header carrying the consistency mode.
pub const CONSISTENCY_HEADER: &str = "TerraScale-Consistency";
/// The longest encoded bookmark TerraScale accepts.
pub const MAX_BOOKMARK_BYTES: usize = 1024;
/// The PostgreSQL setting holding the session's causal bookmark.
pub const POSTGRES_BOOKMARK_SETTING: &str = "terrascale.bookmark";
/// The PostgreSQL setting holding the session's default consistency mode.
pub const POSTGRES_CONSISTENCY_SETTING: &str = "terrascale.consistency";

const BOOKMARK_FORMAT_VERSION: u8 = 1;
const MAX_BOOKMARK_REGION_BYTES: usize = 64;

/// How a SQL transaction commits across a database's peer regions.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ConsistencyMode {
    /// Commits in the receiving region and replicates asynchronously.
    #[default]
    Fast,
    /// Commits on a 2-of-3 regional quorum; concurrent conflicting strong
    /// transactions fail with `serialization_conflict` and may be retried.
    Strong,
}

impl ConsistencyMode {
    /// The wire value: `fast` or `strong`.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Fast => "fast",
            Self::Strong => "strong",
        }
    }

    /// Whether this is [`ConsistencyMode::Strong`].
    #[must_use]
    pub fn is_strong(self) -> bool {
        self == Self::Strong
    }
}

impl FromStr for ConsistencyMode {
    type Err = ();

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        let value = value.trim();
        if value.eq_ignore_ascii_case("fast") {
            Ok(Self::Fast)
        } else if value.eq_ignore_ascii_case("strong") {
            Ok(Self::Strong)
        } else {
            Err(())
        }
    }
}

impl fmt::Display for ConsistencyMode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Whether `bookmark` is a well-formed TerraScale causal bookmark. The empty
/// string is the empty bookmark and is valid.
#[must_use]
pub fn is_valid_bookmark(bookmark: &str) -> bool {
    bookmark.is_empty() || decode_bookmark(bookmark).is_some()
}

/// Combines two bookmarks into one that is at least as new as both, per
/// region. When either side is not a well-formed bookmark, `newer` wins.
#[must_use]
pub fn merge_bookmarks(older: &str, newer: &str) -> String {
    if older.is_empty() || older == newer {
        return newer.to_owned();
    }
    if newer.is_empty() {
        return older.to_owned();
    }
    match (decode_bookmark(older), decode_bookmark(newer)) {
        (Some(mut merged), Some(incoming)) => {
            for (region, sequence) in incoming {
                let slot = merged.entry(region).or_insert(0);
                *slot = (*slot).max(sequence);
            }
            let encoded = encode_bookmark(&merged);
            if encoded.len() <= MAX_BOOKMARK_BYTES {
                encoded
            } else {
                newer.to_owned()
            }
        }
        _ => newer.to_owned(),
    }
}

fn decode_bookmark(encoded: &str) -> Option<BTreeMap<String, u64>> {
    if encoded.len() > MAX_BOOKMARK_BYTES {
        return None;
    }
    let bytes = URL_SAFE_NO_PAD.decode(encoded.trim_end_matches('=')).ok()?;
    let (&version, mut cursor) = bytes.split_first()?;
    if version != BOOKMARK_FORMAT_VERSION {
        return None;
    }
    let count = read_varint(&mut cursor)?;
    if count > cursor.len() as u64 {
        return None;
    }
    let mut entries = BTreeMap::new();
    let mut previous: Option<String> = None;
    for _ in 0..count {
        let length = usize::try_from(read_varint(&mut cursor)?).ok()?;
        if length == 0 || length > MAX_BOOKMARK_REGION_BYTES || length > cursor.len() {
            return None;
        }
        let (region, rest) = cursor.split_at(length);
        let region = std::str::from_utf8(region).ok()?.to_owned();
        cursor = rest;
        let sequence = read_varint(&mut cursor)?;
        if sequence == 0 || sequence > i64::MAX as u64 {
            return None;
        }
        if previous
            .as_deref()
            .is_some_and(|last| last >= region.as_str())
        {
            return None;
        }
        previous = Some(region.clone());
        entries.insert(region, sequence);
    }
    cursor.is_empty().then_some(entries)
}

fn encode_bookmark(entries: &BTreeMap<String, u64>) -> String {
    if entries.is_empty() {
        return String::new();
    }
    let mut bytes = vec![BOOKMARK_FORMAT_VERSION];
    write_varint(&mut bytes, entries.len() as u64);
    for (region, sequence) in entries {
        write_varint(&mut bytes, region.len() as u64);
        bytes.extend_from_slice(region.as_bytes());
        write_varint(&mut bytes, *sequence);
    }
    URL_SAFE_NO_PAD.encode(bytes)
}

fn read_varint(cursor: &mut &[u8]) -> Option<u64> {
    let mut value = 0u64;
    for (index, &byte) in cursor.iter().enumerate().take(10) {
        value |= u64::from(byte & 0x7F).checked_shl(7 * index as u32)?;
        if byte & 0x80 == 0 {
            *cursor = &cursor[index + 1..];
            return Some(value);
        }
    }
    None
}

fn write_varint(bytes: &mut Vec<u8>, mut value: u64) {
    while value >= 0x80 {
        bytes.push((value as u8) | 0x80);
        value >>= 7;
    }
    bytes.push(value as u8);
}

/// Thread-safe holder of the newest causal bookmark observed on one database.
/// Clones share the same bookmark.
#[derive(Debug, Clone, Default)]
pub struct BookmarkTracker {
    current: Arc<RwLock<Option<String>>>,
}

impl BookmarkTracker {
    /// An empty tracker.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The newest bookmark observed, if any.
    #[must_use]
    pub fn get(&self) -> Option<String> {
        self.current
            .read()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clone()
    }

    /// Merges `bookmark` into the tracked one; empty and malformed values are
    /// ignored.
    pub fn observe(&self, bookmark: &str) {
        let bookmark = bookmark.trim();
        if bookmark.is_empty() || !is_valid_bookmark(bookmark) {
            return;
        }
        let mut current = self
            .current
            .write()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let merged = match current.as_deref() {
            Some(existing) => merge_bookmarks(existing, bookmark),
            None => bookmark.to_owned(),
        };
        *current = Some(merged);
    }

    /// Replaces the tracked bookmark; `None` or an empty value clears it.
    pub fn set(&self, bookmark: Option<&str>) {
        let value = bookmark
            .map(str::trim)
            .filter(|bookmark| !bookmark.is_empty())
            .map(str::to_owned);
        *self
            .current
            .write()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = value;
    }

    /// Forgets the tracked bookmark.
    pub fn clear(&self) {
        self.set(None);
    }

    /// The tracked bookmark merged with `extra`, which a caller supplied for
    /// one request.
    #[must_use]
    pub fn with(&self, extra: Option<&str>) -> Option<String> {
        let current = self.get();
        match (
            current,
            extra.map(str::trim).filter(|value| !value.is_empty()),
        ) {
            (Some(current), Some(extra)) => Some(merge_bookmarks(&current, extra)),
            (Some(current), None) => Some(current),
            (None, extra) => extra.map(str::to_owned),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bookmark(entries: &[(&str, u64)]) -> String {
        encode_bookmark(
            &entries
                .iter()
                .map(|(region, sequence)| ((*region).to_owned(), *sequence))
                .collect(),
        )
    }

    #[test]
    fn round_trips_and_merges_per_region() {
        let older = bookmark(&[("fra", 9), ("gru", 300)]);
        let newer = bookmark(&[("gru", 200), ("sfo", 1)]);
        assert!(is_valid_bookmark(&older));
        assert_eq!(
            merge_bookmarks(&older, &newer),
            bookmark(&[("fra", 9), ("gru", 300), ("sfo", 1)])
        );
    }

    #[test]
    fn malformed_bookmarks_fall_back_to_the_newer_value() {
        assert!(!is_valid_bookmark("not a bookmark"));
        assert_eq!(merge_bookmarks("@@", "AQA"), "AQA");
        let tracker = BookmarkTracker::new();
        tracker.observe("@@");
        assert_eq!(tracker.get(), None);
    }

    #[test]
    fn parses_consistency_modes() {
        assert_eq!("STRONG".parse(), Ok(ConsistencyMode::Strong));
        assert_eq!(" fast ".parse(), Ok(ConsistencyMode::Fast));
        assert_eq!("quorum".parse::<ConsistencyMode>(), Err(()));
    }
}
