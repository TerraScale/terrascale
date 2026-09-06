//! Immutable source-cut copying into a fresh encrypted database identity.

use std::{io::{Read, Write}, path::Path};
use super::{DATABASE_FILE, SCHEMA_BINDING_KEY, validate_database_binding};
use crate::storage::{DatabaseOwner, EncryptedStorage, create_physical_file, open_physical_file, open_physical_sqlite};

/// Copies a closed, authenticated snapshot without ever staging plaintext.
/// The source must remain immutable through the caller's backup snapshot lease.
pub(crate) fn copy_encrypted_snapshot(
    source: &Path,
    target: &Path,
    source_owner: &DatabaseOwner,
    keys: std::sync::Arc<dyn crate::encryption::DatabaseKeySource>,
) -> Result<EncryptedStorage, ()> {
    if !source.join("owner.tbv1").is_file() { return Err(()); }
    crate::storage::load_or_create(source, Some(source_owner), crate::storage::OwnershipMode::Production).map_err(|_| ())?;
    let source_binding = super::database_binding(source).map_err(|_| ())?.ok_or(())?;
    // READONLY verifies source identity and the WAL cut without changing source
    // checkpoint state. The producer guarantees no concurrent writer exists.
    let connection = open_physical_sqlite(&source.join(DATABASE_FILE), rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY).map_err(|_| ())?;
    if validate_database_binding(&connection, &source_binding).map_err(|_| ())? { return Err(()); }
    let integrity: String = connection.query_row("PRAGMA integrity_check", [], |row| row.get(0)).map_err(|_| ())?;
    if integrity != "ok" { return Err(()); }
    let registration = EncryptedStorage::register(target, keys).map_err(|_| ())?;
    for name in [DATABASE_FILE.to_owned(), format!("{DATABASE_FILE}-wal")] {
        let mut source_file = match open_physical_file(&source.join(&name)) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound && name.ends_with("-wal") => continue,
            Err(_) => return Err(()),
        };
        let mut destination = create_physical_file(&target.join(&name)).map_err(|_| ())?;
        let mut buffer = vec![0; 64 * 1024];
        loop {
            let length = source_file.read(&mut buffer).map_err(|_| ())?;
            if length == 0 { break; }
            destination.write_all(&buffer[..length]).map_err(|_| ())?;
        }
        destination.sync_all().map_err(|_| ())?;
    }
    drop(connection);
    Ok(registration)
}

/// Rebinds only the SQLite ownership marker after all producer-specific branch
/// transforms have succeeded in the same transaction.
pub(crate) fn rebind_owner(transaction: &rusqlite::Transaction<'_>, source: &Path, target: &Path) -> Result<(), ()> {
    let old = super::database_binding(source).map_err(|_| ())?.ok_or(())?;
    let new = super::database_binding(target).map_err(|_| ())?.ok_or(())?;
    if validate_database_binding(transaction, &old).map_err(|_| ())? { return Err(()); }
    let changed = transaction.execute("UPDATE meta SET value=?1 WHERE key=?2 AND value=?3", rusqlite::params![new, SCHEMA_BINDING_KEY, old]).map_err(|_| ())?;
    if changed != 1 { return Err(()); }
    Ok(())
}
