//! Immutable source-cut copying into a fresh encrypted database identity.

use super::{DATABASE_FILE, SCHEMA_BINDING_KEY, validate_database_binding};
use crate::storage::{
    DatabaseOwner, EncryptedStorage, create_physical_file, open_physical_file, open_physical_sqlite,
};
use std::{
    io::{Read, Write},
    path::Path,
};

/// Copies a closed, authenticated snapshot without ever staging plaintext.
/// The source must remain immutable through the caller's backup snapshot lease.
pub(crate) fn copy_encrypted_snapshot(
    source: &Path,
    target: &Path,
    source_owner: &DatabaseOwner,
    keys: std::sync::Arc<dyn crate::encryption::DatabaseKeySource>,
) -> Result<EncryptedStorage, ()> {
    let source_file = source.join(DATABASE_FILE);
    // A production owner record alone is not proof that the source contains
    // encrypted storage. Require both the live VFS registration (which holds
    // the key source) and its authenticated companion before accepting any
    // owner metadata or opening SQLite.
    let _source_registration = super::encrypted_vfs::lookup(&source_file).ok_or(())?;
    let source_backing = super::encrypted_file::companion(&source_file);
    let backing_metadata = std::fs::symlink_metadata(&source_backing).map_err(|_| ())?;
    if backing_metadata.file_type().is_symlink() || !backing_metadata.is_file() {
        return Err(());
    }
    if !source.join("owner.tbv1").is_file() {
        return Err(());
    }
    crate::storage::load_or_create(
        source,
        Some(source_owner),
        crate::storage::OwnershipMode::Production,
    )
    .map_err(|_| ())?;
    let source_binding = super::database_binding(source).map_err(|_| ())?.ok_or(())?;
    // READONLY verifies source identity and the WAL cut without changing source
    // checkpoint state. The producer guarantees no concurrent writer exists.
    let connection = open_physical_sqlite(&source_file, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
        .map_err(|_| ())?;
    if validate_database_binding(&connection, &source_binding).map_err(|_| ())? {
        return Err(());
    }
    let integrity: String =
        connection.query_row("PRAGMA integrity_check", [], |row| row.get(0)).map_err(|_| ())?;
    if integrity != "ok" {
        return Err(());
    }
    let registration = EncryptedStorage::register(target, keys).map_err(|_| ())?;
    for name in [DATABASE_FILE.to_owned(), format!("{DATABASE_FILE}-wal")] {
        let mut source_file = match open_physical_file(&source.join(&name)) {
            Ok(file) => file,
            Err(error)
                if error.kind() == std::io::ErrorKind::NotFound && name.ends_with("-wal") =>
            {
                continue;
            }
            Err(_) => return Err(()),
        };
        let mut destination = create_physical_file(&target.join(&name)).map_err(|_| ())?;
        let mut buffer = vec![0; 64 * 1024];
        loop {
            let length = source_file.read(&mut buffer).map_err(|_| ())?;
            if length == 0 {
                break;
            }
            destination.write_all(&buffer[..length]).map_err(|_| ())?;
        }
        destination.sync_all().map_err(|_| ())?;
    }
    drop(connection);
    Ok(registration)
}

/// Rebinds only the SQLite ownership marker after all producer-specific branch
/// transforms have succeeded in the same transaction.
pub(crate) fn rebind_owner(
    transaction: &rusqlite::Transaction<'_>,
    source: &Path,
    target: &Path,
) -> Result<(), ()> {
    let old = super::database_binding(source).map_err(|_| ())?.ok_or(())?;
    let new = super::database_binding(target).map_err(|_| ())?.ok_or(())?;
    if validate_database_binding(transaction, &old).map_err(|_| ())? {
        return Err(());
    }
    let changed = transaction
        .execute(
            "UPDATE meta SET value=?1 WHERE key=?2 AND value=?3",
            rusqlite::params![new, SCHEMA_BINDING_KEY, old],
        )
        .map_err(|_| ())?;
    if changed != 1 {
        return Err(());
    }
    Ok(())
}

/// Rebinds schema metadata from an authenticated source database to its branch
/// database while preserving branch names and envelope bytes.
pub(crate) fn transform_branch_schema(
    transaction: &rusqlite::Transaction<'_>,
    source_owner: &DatabaseOwner,
    child_owner: &DatabaseOwner,
) -> Result<(), ()> {
    if source_owner.tenant() != child_owner.tenant()
        || source_owner.project() != child_owner.project()
        || source_owner.environment() != child_owner.environment()
        || source_owner.region() != child_owner.region()
        || source_owner.database() == child_owner.database()
    {
        return Err(());
    }
    let source = source_owner.database().to_string();
    let child = child_owner.database().to_string();
    let unrelated: i64 = transaction
        .query_row(
            "SELECT COUNT(*) FROM schema_metadata WHERE database_id <> ?1",
            [&source],
            |row| row.get(0),
        )
        .map_err(|_| ())?;
    if unrelated != 0 {
        return Err(());
    }
    transaction
        .execute(
            "UPDATE schema_metadata SET database_id=?1 WHERE database_id=?2",
            [&child, &source],
        )
        .map_err(|_| ())?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn copy_rejects_plaintext_source_with_production_owner() {
        let root = tempfile::tempdir().unwrap();
        let source = root.path().join("source");
        let target = root.path().join("target");
        std::fs::create_dir(&source).unwrap();
        let owner = DatabaseOwner::local_development();
        crate::storage::load_or_create(
            &source,
            Some(&owner),
            crate::storage::OwnershipMode::Production,
        )
        .unwrap();
        std::fs::write(source.join(DATABASE_FILE), b"plaintext sqlite").unwrap();

        let keys = crate::engine::test_storage_keys(&owner, 201);
        assert!(copy_encrypted_snapshot(&source, &target, &owner, keys).is_err());
    }

    #[test]
    fn transform_branch_schema_rebinds_source_rows_and_rejects_unrelated_rows() {
        let source = DatabaseOwner::local_development();
        let child = DatabaseOwner::new(
            source.environment().clone(),
            source.tenant(),
            source.project(),
            crate::identity::DatabaseUlid::generate(),
            source.region(),
        );
        let connection = rusqlite::Connection::open_in_memory().unwrap();
        connection
            .execute_batch("CREATE TABLE schema_metadata (database_id TEXT NOT NULL, branch TEXT NOT NULL, collection TEXT NOT NULL, envelope BLOB NOT NULL, PRIMARY KEY(database_id, branch, collection)) WITHOUT ROWID")
            .unwrap();
        connection
            .execute(
                "INSERT INTO schema_metadata VALUES (?1, 'feature', 'users', ?2)",
                rusqlite::params![source.database().to_string(), b"source-envelope"],
            )
            .unwrap();
        let transaction = connection.transaction().unwrap();
        transform_branch_schema(&transaction, &source, &child).unwrap();
        transaction.commit().unwrap();
        let (database, branch, envelope): (String, String, Vec<u8>) = connection
            .query_row("SELECT database_id, branch, envelope FROM schema_metadata", [], |row| {
                Ok((row.get(0)?, row.get(1)?, row.get(2)?))
            })
            .unwrap();
        assert_eq!(database, child.database().to_string());
        assert_eq!(branch, "feature");
        assert_eq!(envelope, b"source-envelope");

        connection
            .execute(
                "INSERT INTO schema_metadata VALUES (?1, 'main', 'other', ?2)",
                rusqlite::params![source.database().to_string(), b"source"],
            )
            .unwrap();
        connection
            .execute(
                "INSERT INTO schema_metadata VALUES (?1, 'main', 'foreign', ?2)",
                rusqlite::params![
                    crate::identity::DatabaseUlid::generate().to_string(),
                    b"foreign"
                ],
            )
            .unwrap();
        let transaction = connection.transaction().unwrap();
        assert!(transform_branch_schema(&transaction, &source, &child).is_err());
    }
}
