//! Canonical ordering key for a document's `(collection, id)` identity.
//!
//! The payload is a version byte followed by two zero terminated, escaped
//! UTF-8 strings. A zero byte in a component is encoded as `00 ff`; the
//! component terminator is `00 00`. This is a memcomparable encoding: byte
//! order of encoded keys is the same as SQLite BINARY tuple order.

use crate::types::{CollectionName, DocumentId};

const VERSION: u8 = 1;

/// Error returned when a shard key is not an exact canonical key.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ShardKeyError {
    InvalidVersion,
    Malformed,
    InvalidUtf8,
    InvalidIdentifier,
    NonCanonical,
}

/// Encodes a document identity in SQLite BINARY tuple order.
#[must_use]
pub(crate) fn canonical_shard_key(collection: &CollectionName, id: &DocumentId) -> Vec<u8> {
    let mut key = Vec::with_capacity(1 + collection.as_str().len() + id.as_str().len() + 3);
    key.push(VERSION);
    encode_component(&mut key, collection.as_str().as_bytes());
    encode_component(&mut key, id.as_str().as_bytes());
    key
}

/// Decodes an exact canonical shard key into its document identity.
pub(crate) fn decode_canonical_shard_key(
    key: &[u8],
) -> Result<(CollectionName, DocumentId), ShardKeyError> {
    if key.first().copied() != Some(VERSION) {
        return Err(ShardKeyError::InvalidVersion);
    }
    let mut cursor = 1;
    let collection = decode_component(key, &mut cursor)?;
    let id = decode_component(key, &mut cursor)?;
    if cursor != key.len() {
        return Err(ShardKeyError::Malformed);
    }
    let collection = std::str::from_utf8(&collection).map_err(|_| ShardKeyError::InvalidUtf8)?;
    let id = std::str::from_utf8(&id).map_err(|_| ShardKeyError::InvalidUtf8)?;
    let collection =
        CollectionName::try_new(collection).map_err(|_| ShardKeyError::InvalidIdentifier)?;
    let id = DocumentId::try_new(id).map_err(|_| ShardKeyError::InvalidIdentifier)?;
    let decoded = canonical_shard_key(&collection, &id);
    if decoded != key {
        return Err(ShardKeyError::NonCanonical);
    }
    Ok((collection, id))
}

fn encode_component(output: &mut Vec<u8>, bytes: &[u8]) {
    for &byte in bytes {
        if byte == 0 {
            output.extend_from_slice(&[0, 0xff]);
        } else {
            output.push(byte);
        }
    }
    output.extend_from_slice(&[0, 0]);
}

fn decode_component(key: &[u8], cursor: &mut usize) -> Result<Vec<u8>, ShardKeyError> {
    let mut output = Vec::new();
    while *cursor < key.len() {
        let byte = key[*cursor];
        *cursor += 1;
        if byte != 0 {
            output.push(byte);
            continue;
        }
        let escape = key.get(*cursor).copied().ok_or(ShardKeyError::Malformed)?;
        *cursor += 1;
        match escape {
            0 => return Ok(output),
            0xff => output.push(0),
            _ => return Err(ShardKeyError::Malformed),
        }
    }
    Err(ShardKeyError::Malformed)
}

#[cfg(test)]
mod tests {
    use super::*;
    use rusqlite::Connection;

    fn pair(collection: &str, id: &str) -> (CollectionName, DocumentId) {
        (CollectionName::try_new(collection).unwrap(), DocumentId::try_new(id).unwrap())
    }

    #[test]
    fn round_trip_unicode_and_prefixes() {
        for (collection, id) in [("a", "b"), ("a", "ba"), ("ab", "b"), ("é", "😀")] {
            let (collection, id) = pair(collection, id);
            let encoded = canonical_shard_key(&collection, &id);
            assert_eq!(decode_canonical_shard_key(&encoded), Ok((collection, id)));
        }
    }

    #[test]
    fn malformed_and_trailing_keys_are_rejected() {
        let (collection, id) = pair("a", "b");
        let encoded = canonical_shard_key(&collection, &id);
        for malformed in [vec![], vec![2, 0, 0, 0, 0], vec![1, b'a', 0, 1], {
            let mut key = encoded.clone();
            key.push(0);
            key
        }] {
            assert!(decode_canonical_shard_key(&malformed).is_err(), "{malformed:?}");
        }
    }

    #[test]
    fn encoded_order_matches_sqlite_binary_tuple_order() {
        let values = [("a", "z"), ("a", "aa"), ("aa", "a"), ("é", "a"), ("a", "😀")];
        let mut connection = Connection::open_in_memory().unwrap();
        connection
            .execute(
                "CREATE TABLE keys (collection TEXT COLLATE BINARY, id TEXT COLLATE BINARY)",
                [],
            )
            .unwrap();
        for (collection, id) in values {
            connection
                .execute("INSERT INTO keys VALUES (?1, ?2)", rusqlite::params![collection, id])
                .unwrap();
        }
        let mut statement = connection.prepare("SELECT collection, id FROM keys ORDER BY collection COLLATE BINARY, id COLLATE BINARY").unwrap();
        let sql_order: Vec<(String, String)> = statement
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
            .unwrap()
            .map(Result::unwrap)
            .collect();
        let mut encoded_order: Vec<(String, String)> = values
            .iter()
            .map(|(c, i)| (*c, *i))
            .map(|(c, i)| {
                let (c, i) = pair(c, i);
                (canonical_shard_key(&c, &i), (c.to_string(), i.to_string()))
            })
            .collect::<Vec<_>>();
        encoded_order.sort_by(|left, right| left.0.cmp(&right.0));
        assert_eq!(encoded_order.into_iter().map(|(_, pair)| pair).collect::<Vec<_>>(), sql_order);
    }
}
