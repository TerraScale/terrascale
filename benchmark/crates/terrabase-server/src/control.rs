//! The member's control plane: the databases registry (`SHORT_GOAL`
//! `S-03`, D-52).
//!
//! The registry is itself an ordinary `TerraScale` database — one engine the
//! member hosts at its root — so the control plane inherits the storage
//! core's durability, idempotency, and snapshots instead of growing a
//! second persistence story. A database is one document in the
//! `databases` collection: the name is the identifier, and the body is the
//! placement (an explicit region list), the residency constraint validated
//! against it (D-52), the lifecycle state, and the lifecycle timestamps.
//! Deletion is soft for seven days: the record flips to `deleted` with a
//! timestamp, and the purge that eventually reclaims the name is a later
//! lane's job.

use std::path::Path;
use std::time::SystemTime;
use std::time::UNIX_EPOCH;

use terrabase::CommitOutcome;
use terrabase::Engine;
use terrabase::EngineConfig;
use terrabase::engine::EngineError;
use terrabase::regions::RegionCode;
use terrabase::types::CollectionName;
use terrabase::types::Document;
use terrabase::types::DocumentId;
use terrabase::types::DocumentValue;
use terrabase::types::ExpectedVersion;
use terrabase::types::LocalDurability;
use terrabase::types::OperationId;
use terrabase::types::TenantId;
use terrabase::types::TenantScope;

/// The system scope the registry lives in. The control plane is a database
/// like any other, under a reserved tenant no customer key can name.
const REGISTRY_TENANT: &str = "terrascale-system";
/// The collection holding one document per database.
const REGISTRY_COLLECTION: &str = "databases";
/// How long a soft-deleted database stays restorable before its name can
/// be reclaimed, unix milliseconds — the same arithmetic the registry's
/// timestamps use.
const SOFT_DELETE_WINDOW_MILLIS: i64 = 7 * 24 * 60 * 60 * 1000;

/// The typed failures of control-plane operations. Every variant names the
/// client's remedy, the same contract the HTTP envelope carries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ControlError {
    /// The database name is not a name this deployment can host.
    InvalidName(String),
    /// The placement is empty or names an unknown region.
    InvalidPlacement(String),
    /// The residency constraint is not a known constraint or region list.
    InvalidResidency(String),
    /// The placement violates the residency constraint (D-52).
    ResidencyViolation {
        /// The offending placement entry.
        region: RegionCode,
        /// The constraint as given.
        constraint: String,
    },
    /// A live database with this name already exists.
    AlreadyExists(String),
    /// The name exists but is soft-deleted inside the restore window.
    SoftDeleted(String),
    /// No database with this name exists.
    NotFound(String),
    /// The registry's own engine refused the operation.
    Registry(String),
}

/// One registry record: everything the routing contract (D-58) and the
/// placement rule (D-52) need to answer for a database.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DatabaseRecord {
    /// The database's name — its registry identifier and path segment.
    pub name: String,
    /// The explicit region list hosting the database.
    pub placement: Vec<RegionCode>,
    /// The residency constraint stored with the database, if any.
    pub residency: Option<String>,
    /// Whether the database is live or soft-deleted.
    pub deleted: bool,
    /// When the record was created, unix milliseconds.
    pub created_at: i64,
    /// When the record was soft-deleted, unix milliseconds, if it was.
    pub deleted_at: Option<i64>,
}

impl DatabaseRecord {
    /// The `X-TerraScale-Placement` spelling: `GRU,FRA`.
    #[must_use]
    pub fn placement_header(&self) -> String {
        self.placement.iter().map(RegionCode::as_str).collect::<Vec<_>>().join(",")
    }

    /// The `X-TerraScale-Residency` value: the constraint, or `none`.
    #[must_use]
    pub fn residency_header(&self) -> &str {
        self.residency.as_deref().unwrap_or("none")
    }
}

/// The member's registry engine.
pub struct ControlPlane {
    engine: Engine,
    scope: TenantScope,
}

impl ControlPlane {
    /// Opens (creating when absent) the control-plane engine at
    /// `<root>/control-plane`.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::Registry`] when the engine cannot open the
    /// location.
    pub fn open(root: &Path) -> Result<Self, ControlError> {
        let engine = Engine::open(
            EngineConfig::durable(root.join("control-plane"))
                .map_err(|error| ControlError::Registry(format!("{error:?}")))?,
        )
        .map_err(|error| ControlError::Registry(format!("{error:?}")))?;
        let scope = engine.scope_for(
            &TenantId::try_new(REGISTRY_TENANT)
                .map_err(|error| ControlError::Registry(format!("{error:?}")))?,
        );
        Ok(Self { engine, scope })
    }

    /// Creates one database record. The placement is an explicit, non-empty
    /// region list; the residency constraint — `eu-only`, `br-only`, or a
    /// region list — is validated against it here and stored with the
    /// record (D-52).
    ///
    /// # Errors
    ///
    /// Returns the typed refusal for an invalid name, placement, or
    /// residency, a residency violation, or an existing live or
    /// soft-deleted name.
    pub fn create(
        &self,
        name: &str,
        placement: &[RegionCode],
        residency: Option<&str>,
    ) -> Result<DatabaseRecord, ControlError> {
        validate_name(name)?;
        validate_placement(placement)?;
        if let Some(constraint) = residency {
            validate_residency(constraint, placement)?;
        }
        if let Some(existing) = self.get(name)? {
            if existing.deleted {
                return Err(ControlError::SoftDeleted(name.to_owned()));
            }
            return Err(ControlError::AlreadyExists(name.to_owned()));
        }
        let record = DatabaseRecord {
            name: name.to_owned(),
            placement: placement.to_vec(),
            residency: residency.map(str::to_owned),
            deleted: false,
            created_at: unix_millis(),
            deleted_at: None,
        };
        self.commit_record(&record, name)?;
        Ok(record)
    }

    /// Lists every record, deleted records included (the dashboard's
    /// restore view lists them; callers filter).
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::Registry`] when the registry engine cannot
    /// answer.
    pub fn list(&self) -> Result<Vec<DatabaseRecord>, ControlError> {
        let snapshot = self
            .engine
            .read_snapshot(&self.scope)
            .map_err(|error| ControlError::Registry(format!("{error:?}")))?;
        let documents = self
            .engine
            .snapshot_documents_in_collection(
                &self.scope,
                &snapshot,
                &CollectionName::try_new(REGISTRY_COLLECTION)
                    .map_err(|error| ControlError::Registry(format!("{error:?}")))?,
            )
            .map_err(|error| ControlError::Registry(format!("{error:?}")))?;
        let mut records: Vec<DatabaseRecord> = documents.iter().filter_map(record_of).collect();
        records.sort_by(|a, b| a.name.cmp(&b.name));
        Ok(records)
    }

    /// Returns one database's record.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::Registry`] when the registry engine cannot
    /// answer.
    pub fn get(&self, name: &str) -> Result<Option<DatabaseRecord>, ControlError> {
        Ok(self.registry_document(name)?.as_ref().and_then(record_of))
    }

    /// Reads one registry record's stored document — the record plus the
    /// engine's version, which optimistic updates need.
    fn registry_document(&self, name: &str) -> Result<Option<Document>, ControlError> {
        let id =
            DocumentId::try_new(name).map_err(|_| ControlError::InvalidName(name.to_owned()))?;
        let snapshot = self
            .engine
            .read_snapshot(&self.scope)
            .map_err(|error| ControlError::Registry(format!("{error:?}")))?;
        let transaction = self
            .engine
            .begin(&self.scope, snapshot, read_operation()?)
            .map_err(|error| registry_error(&error))?;
        transaction.read(&id).map_err(|error| registry_error(&error))
    }

    /// Soft-deletes one database: the record flips to `deleted` with a
    /// timestamp, restorable for [`SOFT_DELETE_WINDOW`].
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::NotFound`] for an unknown (or already
    /// soft-deleted) database and [`ControlError::Registry`] when the
    /// engine refuses.
    pub fn soft_delete(&self, name: &str) -> Result<DatabaseRecord, ControlError> {
        let Some(mut record) = self.get(name)? else {
            return Err(ControlError::NotFound(name.to_owned()));
        };
        if record.deleted {
            return Err(ControlError::NotFound(name.to_owned()));
        }
        record.deleted = true;
        record.deleted_at = Some(unix_millis());
        self.commit_record(&record, name)?;
        Ok(record)
    }

    /// Restores one soft-deleted database inside the window.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::NotFound`] when the database is unknown or
    /// live, [`ControlError::SoftDeleted`] when the window has passed, and
    /// [`ControlError::Registry`] when the engine refuses.
    pub fn restore(&self, name: &str) -> Result<DatabaseRecord, ControlError> {
        let Some(mut record) = self.get(name)? else {
            return Err(ControlError::NotFound(name.to_owned()));
        };
        if !record.deleted {
            return Err(ControlError::NotFound(name.to_owned()));
        }
        let Some(deleted_at) = record.deleted_at else {
            return Err(ControlError::Registry("deleted record without a timestamp".into()));
        };
        if unix_millis() - deleted_at > SOFT_DELETE_WINDOW_MILLIS {
            return Err(ControlError::SoftDeleted(name.to_owned()));
        }
        record.deleted = false;
        record.deleted_at = None;
        self.commit_record(&record, name)?;
        Ok(record)
    }

    fn commit_record(&self, record: &DatabaseRecord, name: &str) -> Result<(), ControlError> {
        // The expected version is whatever the engine last assigned the
        // record's key: absent for a create, exact for an update.
        let expected = match self.registry_document(name)? {
            Some(document) => ExpectedVersion::Exact(document.metadata().version()),
            None => ExpectedVersion::New,
        };
        // The operation identity is version-qualified: the engine binds an
        // operation id durably to its write set, so a record's second
        // mutation (delete, restore) under the same id would read as a
        // retry with a different digest. The version the mutation targets
        // is unique per key, so the identity is too.
        let version = match &expected {
            ExpectedVersion::Exact(version) => *version,
            _ => 0,
        };
        let snapshot = self
            .engine
            .read_snapshot(&self.scope)
            .map_err(|error| ControlError::Registry(format!("{error:?}")))?;
        let mut transaction = self
            .engine
            .begin(
                &self.scope,
                snapshot,
                OperationId::try_new(format!("control-{}-v{version}", record.name).as_str())
                    .map_err(|error| ControlError::Registry(format!("{error:?}")))?,
            )
            .map_err(|error| registry_error(&error))?;
        let document = Document::new(
            DocumentId::try_new(&record.name)
                .map_err(|error| ControlError::Registry(format!("{error:?}")))?,
            CollectionName::try_new(REGISTRY_COLLECTION)
                .map_err(|error| ControlError::Registry(format!("{error:?}")))?,
            record_body(record),
        );
        transaction.put(document, expected).map_err(|error| registry_error(&error))?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(reason) => Err(ControlError::Registry(format!("{reason:?}"))),
            CommitOutcome::Unknown(reason) => Err(ControlError::Registry(format!("{reason:?}"))),
        }
    }
}

/// Reads one record back from its stored document.
fn record_of(document: &Document) -> Option<DatabaseRecord> {
    let DocumentValue::Object(fields) = document.value() else {
        return None;
    };
    let name = document.id().as_str().to_owned();
    let placement = match fields.get("placement")? {
        DocumentValue::Array(regions) => regions
            .iter()
            .map(|region| match region {
                DocumentValue::String(text) => RegionCode::from_text(text).ok(),
                _ => None,
            })
            .collect::<Option<Vec<_>>>()?,
        _ => return None,
    };
    let residency = match fields.get("residency")? {
        DocumentValue::String(text) => Some(text.clone()),
        DocumentValue::Null => None,
        _ => return None,
    };
    let deleted = matches!(fields.get("deleted")?, DocumentValue::Boolean(true));
    let created_at = match fields.get("created_at")? {
        DocumentValue::Integer(millis) => *millis,
        _ => return None,
    };
    let deleted_at = match fields.get("deleted_at")? {
        DocumentValue::Integer(millis) => Some(*millis),
        DocumentValue::Null => None,
        _ => return None,
    };
    Some(DatabaseRecord { name, placement, residency, deleted, created_at, deleted_at })
}

/// Builds one record's stored body.
fn record_body(record: &DatabaseRecord) -> DocumentValue {
    let mut fields = std::collections::BTreeMap::new();
    fields.insert(
        "placement".to_owned(),
        DocumentValue::Array(
            record
                .placement
                .iter()
                .map(|region| DocumentValue::String(region.as_str().to_owned()))
                .collect(),
        ),
    );
    fields.insert(
        "residency".to_owned(),
        match &record.residency {
            Some(constraint) => DocumentValue::String(constraint.clone()),
            None => DocumentValue::Null,
        },
    );
    fields.insert("deleted".to_owned(), DocumentValue::Boolean(record.deleted));
    fields.insert("created_at".to_owned(), DocumentValue::Integer(record.created_at));
    fields.insert(
        "deleted_at".to_owned(),
        match record.deleted_at {
            Some(millis) => DocumentValue::Integer(millis),
            None => DocumentValue::Null,
        },
    );
    DocumentValue::Object(fields)
}

/// Validates a database name: 1–63 characters, lowercase letters, digits,
/// and dashes, starting and ending with a letter or digit — the same class
/// the catalog's path components allow.
fn validate_name(name: &str) -> Result<(), ControlError> {
    let valid = (1..=63).contains(&name.len())
        && name.chars().all(|character| {
            character.is_ascii_lowercase() || character.is_ascii_digit() || character == '-'
        })
        && !name.starts_with('-')
        && !name.ends_with('-');
    if valid && DocumentId::try_new(name).is_ok() {
        Ok(())
    } else {
        Err(ControlError::InvalidName(name.to_owned()))
    }
}

/// Validates a placement: non-empty, every region a well-formed code.
fn validate_placement(placement: &[RegionCode]) -> Result<(), ControlError> {
    if placement.is_empty() {
        return Err(ControlError::InvalidPlacement("the region list is empty".into()));
    }
    Ok(())
}

/// Validates one residency constraint against a placement (D-52): the
/// constraint is `eu-only`, `br-only`, or a region list, and every
/// placement region must satisfy it.
// ponytail: the eu/br sets hardcode the launch catalog (GRU/SFO/FRA); an
// orchestrator-fed geo map replaces this when the 4th region lands.
fn validate_residency(constraint: &str, placement: &[RegionCode]) -> Result<(), ControlError> {
    let allowed: Vec<RegionCode> = match constraint {
        "eu-only" => vec![RegionCode::FRA],
        "br-only" => vec![RegionCode::GRU],
        list => list
            .split(',')
            .map(str::trim)
            .map(RegionCode::from_text)
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ControlError::InvalidResidency(constraint.to_owned()))?,
    };
    if allowed.is_empty() {
        return Err(ControlError::InvalidResidency(constraint.to_owned()));
    }
    for region in placement {
        if !allowed.contains(region) {
            return Err(ControlError::ResidencyViolation {
                region: *region,
                constraint: constraint.to_owned(),
            });
        }
    }
    Ok(())
}

/// Wall-clock unix milliseconds. The registry's timestamps are display and
/// window bookkeeping, not consensus facts.
fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |elapsed| i64::try_from(elapsed.as_millis()).unwrap_or(0))
}

fn registry_error(error: &EngineError) -> ControlError {
    ControlError::Registry(format!("{error:?}"))
}

/// One registry-scope read operation identity. Reads stage nothing, so one
/// shared identity cannot collide with a writer's reservation.
fn read_operation() -> Result<OperationId, ControlError> {
    OperationId::try_new("control-read")
        .map_err(|error| ControlError::Registry(format!("{error:?}")))
}

#[cfg(test)]
mod tests {
    use super::ControlError;
    use super::ControlPlane;
    use terrabase::regions::RegionCode;

    fn plane() -> (tempdir::TempDir, ControlPlane) {
        let dir = tempdir::TempDir::new().expect("tempdir");
        let plane = ControlPlane::open(dir.path()).expect("opens");
        (dir, plane)
    }

    #[test]
    fn create_list_get_round_trip_with_placement_and_residency() {
        let (_dir, plane) = plane();
        let record = plane.create("prod-db", &[RegionCode::FRA], Some("eu-only")).expect("creates");
        assert_eq!(record.placement_header(), "FRA");
        assert_eq!(record.residency_header(), "eu-only");
        let listed = plane.list().expect("lists");
        assert_eq!(listed.len(), 1);
        assert_eq!(listed[0].name, "prod-db");
        let fetched = plane.get("prod-db").expect("gets").expect("present");
        assert_eq!(fetched, record);
    }

    #[test]
    fn residency_rejects_a_placement_outside_the_constraint() {
        let (_dir, plane) = plane();
        // eu-only admits only EU regions, so both GRU and SFO violate it;
        // the first offender is reported.
        let refused = plane
            .create("us-db", &[RegionCode::GRU, RegionCode::SFO], Some("eu-only"))
            .expect_err("neither GRU nor SFO is in eu-only");
        assert!(
            matches!(refused, ControlError::ResidencyViolation { region, .. } if region == RegionCode::GRU),
            "{refused:?}"
        );
        // br-only admits GRU alone.
        assert!(plane.create("br-db", &[RegionCode::GRU], Some("br-only")).is_ok());
        // A region-list constraint admits exactly its members.
        assert!(
            plane.create("list-db", &[RegionCode::GRU, RegionCode::FRA], Some("GRU,FRA")).is_ok()
        );
        // A nonsense constraint is invalid, never silently empty.
        assert!(matches!(
            plane.create("bad-db", &[RegionCode::GRU], Some("nope")),
            Err(ControlError::InvalidResidency(_))
        ));
    }

    #[test]
    fn names_and_placements_are_validated() {
        let (_dir, plane) = plane();
        for name in ["", "-x", "x-", "Upper", "has space", &"a".repeat(64)] {
            assert!(matches!(
                plane.create(name, &[RegionCode::GRU], None),
                Err(ControlError::InvalidName(_))
            ));
        }
        assert!(matches!(plane.create("empty", &[], None), Err(ControlError::InvalidPlacement(_))));
    }

    #[test]
    fn create_conflict_soft_delete_and_restore() {
        let (_dir, plane) = plane();
        plane.create("conflict", &[RegionCode::GRU], None).expect("creates");
        assert!(matches!(
            plane.create("conflict", &[RegionCode::GRU], None),
            Err(ControlError::AlreadyExists(_))
        ));
        let deleted = plane.soft_delete("conflict").expect("soft-deletes");
        assert!(deleted.deleted, "the record flips to deleted");
        assert!(matches!(
            plane.create("conflict", &[RegionCode::GRU], None),
            Err(ControlError::SoftDeleted(_))
        ));
        let restored = plane.restore("conflict").expect("restores");
        assert!(!restored.deleted);
        assert!(!plane.get("conflict").expect("gets").expect("present").deleted);
    }

    #[test]
    fn records_survive_reopen() {
        let (dir, plane) = plane();
        plane.create("durable", &[RegionCode::FRA], None).expect("creates");
        drop(plane);
        let reopened = ControlPlane::open(dir.path()).expect("reopens");
        assert!(reopened.get("durable").expect("gets").is_some());
    }

    // A tiny tempdir: the crate keeps dev-dependencies minimal and the
    // registry tests need exactly one directory that outlives the plane.
    mod tempdir {
        use std::path::Path;
        use std::path::PathBuf;

        pub(super) struct TempDir(PathBuf);

        impl TempDir {
            pub(super) fn new() -> Result<Self, std::io::Error> {
                let path = std::env::temp_dir().join(format!(
                    "terrabase-control-{}-{}",
                    std::process::id(),
                    unix_nanos()
                ));
                std::fs::create_dir_all(&path)?;
                Ok(Self(path))
            }

            pub(super) fn path(&self) -> &Path {
                &self.0
            }
        }

        impl Drop for TempDir {
            fn drop(&mut self) {
                std::fs::remove_dir_all(&self.0).ok();
            }
        }

        fn unix_nanos() -> u128 {
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        }
    }
}
