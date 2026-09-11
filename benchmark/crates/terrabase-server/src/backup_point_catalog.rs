//! Authenticated logical recovery-point discovery for the native backup fleet.

use std::collections::BTreeMap;

use base64::Engine as _;
use serde::{Deserialize, Serialize};

use crate::native_backup_runtime::{NativeBackupRuntime, NativeBackupRuntimeError};

/// Default number of points returned by one listing call.
pub const DEFAULT_LIMIT: usize = 50;
/// Maximum number of points returned by one listing call.
pub const MAX_LIMIT: usize = 100;
const MAX_CURSOR_BYTES: usize = 512;

/// The management representation of one authenticated logical recovery point.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize, utoipa::ToSchema)]
pub struct BackupPoint {
    /// Exact lowercase hexadecimal recovery-point identity.
    #[schema(pattern = "^[0-9a-f]{64}$")]
    pub point_id: String,
    /// Canonical source database ULID.
    pub source_database_id: String,
    /// Capture wall-clock timestamp.
    #[schema(format = "uint64", minimum = 0)]
    pub captured_at_unix_millis: u64,
    /// Immutable retention deadline.
    #[schema(format = "uint64", minimum = 0)]
    pub retain_until_unix_millis: u64,
    /// Logical content checksum.
    #[schema(pattern = "^[0-9a-f]+$")]
    pub logical_checksum: String,
    /// Logical document count encoded as a decimal string.
    #[schema(pattern = "^[0-9]+$")]
    pub logical_documents: String,
}

/// One bounded keyset page of retained logical points.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize, utoipa::ToSchema)]
pub struct BackupPointPage {
    /// Points in newest-first keyset order.
    pub points: Vec<BackupPoint>,
    /// Opaque cursor for the next page, if more points remain.
    pub next_cursor: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
struct Cursor {
    source_database_id: String,
    database_incarnation: String,
    captured_at_unix_millis: u64,
    point_id: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
/// Errors returned while inspecting or paginating authenticated backup points.
pub enum BackupPointCatalogError {
    /// The source is not a canonical database ULID.
    InvalidDatabase,
    /// The requested page size is outside the bounded range.
    InvalidLimit,
    /// The cursor is malformed or bound to another source/incarnation.
    InvalidCursor,
    /// Two authenticated artifacts claim one point identity with different facts.
    ConflictingPoint,
    /// The native backup fleet could not inspect its authenticated metadata.
    Runtime(NativeBackupRuntimeError),
}

impl std::fmt::Display for BackupPointCatalogError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::InvalidDatabase => f.write_str("invalid source database identity"),
            Self::InvalidLimit => f.write_str("backup point limit is out of range"),
            Self::InvalidCursor => f.write_str("invalid backup point cursor"),
            Self::ConflictingPoint => f.write_str("conflicting backup point identity"),
            Self::Runtime(error) => error.fmt(f),
        }
    }
}

impl std::error::Error for BackupPointCatalogError {}

impl From<NativeBackupRuntimeError> for BackupPointCatalogError {
    fn from(error: NativeBackupRuntimeError) -> Self {
        Self::Runtime(error)
    }
}

fn incarnation(database: &str) -> Result<u128, BackupPointCatalogError> {
    let id = terrabase::identity::DatabaseUlid::from_text(database)
        .map_err(|_| BackupPointCatalogError::InvalidDatabase)?;
    if id.to_text() != database {
        return Err(BackupPointCatalogError::InvalidDatabase);
    }
    Ok(u128::from_be_bytes(id.as_bytes()))
}

fn cursor_decode(
    encoded: &str,
    database: &str,
    database_incarnation: u128,
) -> Result<Cursor, BackupPointCatalogError> {
    if encoded.is_empty() || encoded.len() > MAX_CURSOR_BYTES {
        return Err(BackupPointCatalogError::InvalidCursor);
    }
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(encoded)
        .map_err(|_| BackupPointCatalogError::InvalidCursor)?;
    if base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(&bytes) != encoded {
        return Err(BackupPointCatalogError::InvalidCursor);
    }
    let cursor: Cursor =
        serde_json::from_slice(&bytes).map_err(|_| BackupPointCatalogError::InvalidCursor)?;
    if serde_json::to_vec(&cursor).map_err(|_| BackupPointCatalogError::InvalidCursor)? != bytes {
        return Err(BackupPointCatalogError::InvalidCursor);
    }
    if cursor.source_database_id != database
        || cursor.database_incarnation != database_incarnation.to_string()
        || cursor.point_id.len() != 64
        || !cursor.point_id.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
    {
        return Err(BackupPointCatalogError::InvalidCursor);
    }
    Ok(cursor)
}

#[allow(clippy::needless_pass_by_value, reason = "cursor is only borrowed for encoding")]
fn cursor_encode(cursor: Cursor) -> Result<String, BackupPointCatalogError> {
    let bytes = serde_json::to_vec(&cursor).map_err(|_| BackupPointCatalogError::InvalidCursor)?;
    let encoded = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(bytes);
    if encoded.len() > MAX_CURSOR_BYTES {
        return Err(BackupPointCatalogError::InvalidCursor);
    }
    Ok(encoded)
}

/// Lists authenticated root manifests and complete routed envelopes.
///
/// # Errors
/// Refuses invalid limits, cursors, or conflicting retained points, and returns runtime failures.
pub async fn list_backup_points(
    runtime: &NativeBackupRuntime,
    database: &str,
    limit: Option<usize>,
    cursor: Option<&str>,
    now_unix_millis: u64,
) -> Result<BackupPointPage, BackupPointCatalogError> {
    let limit = limit.unwrap_or(DEFAULT_LIMIT);
    if !(1..=MAX_LIMIT).contains(&limit) {
        return Err(BackupPointCatalogError::InvalidLimit);
    }
    let database_incarnation = incarnation(database)?;
    let after =
        cursor.map(|value| cursor_decode(value, database, database_incarnation)).transpose()?;
    let manifests = runtime.retained_points_async(database).await?;
    let envelopes = runtime.retained_envelopes_async(database).await?;
    let mut points = BTreeMap::<String, BackupPoint>::new();
    for manifest in manifests {
        if manifest.database_id() != database
            || manifest.capture_kind() != terrabase::backup::BackupCaptureKind::WholeDatabase
        {
            return Err(BackupPointCatalogError::ConflictingPoint);
        }
        let id = manifest.identity();
        if id.len() != 64 || !id.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase()) {
            return Err(BackupPointCatalogError::ConflictingPoint);
        }
        let point = BackupPoint {
            point_id: id.to_owned(),
            source_database_id: database.to_owned(),
            captured_at_unix_millis: manifest.captured_unix_millis(),
            retain_until_unix_millis: manifest.retain_until_unix_millis(),
            logical_checksum: manifest.logical_checksum().to_owned(),
            logical_documents: manifest.logical_documents().to_string(),
        };
        if points.insert(id.to_owned(), point.clone()).is_some() {
            return Err(BackupPointCatalogError::ConflictingPoint);
        }
    }
    for envelope in envelopes {
        if envelope.database_id() != database
            || envelope.database_incarnation() != database_incarnation
        {
            return Err(BackupPointCatalogError::ConflictingPoint);
        }
        if envelope.participants().iter().any(|participant| {
            participant.database_id != database
                || participant.database_incarnation != database_incarnation
                || participant.manifest.database_id() != database
                || participant.manifest.capture_kind()
                    != terrabase::backup::BackupCaptureKind::RoutedShardParticipant
        }) {
            return Err(BackupPointCatalogError::ConflictingPoint);
        }
        let id = envelope.digest();
        if id.len() != 64 || !id.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase()) {
            return Err(BackupPointCatalogError::ConflictingPoint);
        }
        let point = BackupPoint {
            point_id: id.to_owned(),
            source_database_id: database.to_owned(),
            captured_at_unix_millis: envelope.captured_unix_millis(),
            retain_until_unix_millis: envelope.retain_until_unix_millis(),
            logical_checksum: envelope.logical_checksum(),
            logical_documents: envelope.logical_documents().to_string(),
        };
        if points.insert(id.to_owned(), point.clone()).is_some() {
            return Err(BackupPointCatalogError::ConflictingPoint);
        }
    }
    let mut ordered: Vec<_> = points
        .into_values()
        .filter(|point| point.retain_until_unix_millis > now_unix_millis)
        .collect();
    ordered.sort_by(|a, b| {
        (b.captured_at_unix_millis, b.point_id.as_str())
            .cmp(&(a.captured_at_unix_millis, a.point_id.as_str()))
    });
    if let Some(after) = after {
        ordered.retain(|point| {
            (point.captured_at_unix_millis, point.point_id.as_str())
                < (after.captured_at_unix_millis, after.point_id.as_str())
        });
    }
    let has_more = ordered.len() > limit;
    ordered.truncate(limit);
    let next_cursor = if has_more {
        let last = ordered.last().ok_or(BackupPointCatalogError::InvalidCursor)?;
        Some(cursor_encode(Cursor {
            source_database_id: database.to_owned(),
            database_incarnation: database_incarnation.to_string(),
            captured_at_unix_millis: last.captured_at_unix_millis,
            point_id: last.point_id.clone(),
        })?)
    } else {
        None
    };
    Ok(BackupPointPage { points: ordered, next_cursor })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cursor_is_bound_to_source_and_incarnation() {
        let cursor = cursor_encode(Cursor {
            source_database_id: "01ARZ3NDEKTSV4RRFFQ69G5FAX".into(),
            database_incarnation: "42".into(),
            captured_at_unix_millis: u64::MAX,
            point_id: "ab".repeat(32),
        })
        .expect("cursor");
        assert!(cursor.len() <= MAX_CURSOR_BYTES);
        assert!(cursor_decode(&cursor, "01ARZ3NDEKTSV4RRFFQ69G5FAX", 42).is_ok());
        assert_eq!(
            cursor_decode(&cursor, "01ARZ3NDEKTSV4RRFFQ69G5FAY", 42),
            Err(BackupPointCatalogError::InvalidCursor)
        );
        assert_eq!(
            cursor_decode(&cursor, "01ARZ3NDEKTSV4RRFFQ69G5FAX", 43),
            Err(BackupPointCatalogError::InvalidCursor)
        );
    }

    #[test]
    fn cursor_rejects_noncanonical_malformed_and_oversized_values() {
        let valid = cursor_encode(Cursor {
            source_database_id: "source".into(),
            database_incarnation: "1".into(),
            captured_at_unix_millis: 1,
            point_id: "cd".repeat(32),
        })
        .expect("cursor");
        assert_eq!(
            cursor_decode(&format!("{valid}="), "source", 1),
            Err(BackupPointCatalogError::InvalidCursor)
        );
        assert_eq!(
            cursor_decode("not-base64", "source", 1),
            Err(BackupPointCatalogError::InvalidCursor)
        );
        assert_eq!(
            cursor_decode(&"a".repeat(MAX_CURSOR_BYTES + 1), "source", 1),
            Err(BackupPointCatalogError::InvalidCursor)
        );
    }

    #[test]
    fn documents_are_decimal_strings_without_u64_loss() {
        let point = BackupPoint {
            point_id: "ef".repeat(32),
            source_database_id: "01ARZ3NDEKTSV4RRFFQ69G5FAX".into(),
            captured_at_unix_millis: u64::MAX,
            retain_until_unix_millis: u64::MAX,
            logical_checksum: "00".repeat(32),
            logical_documents: u64::MAX.to_string(),
        };
        let encoded = serde_json::to_string(&point).expect("point JSON");
        assert!(encoded.contains(&format!("\"logical_documents\":\"{}\"", u64::MAX)));
        assert_eq!(serde_json::from_str::<BackupPoint>(&encoded).expect("point"), point);
    }
}
