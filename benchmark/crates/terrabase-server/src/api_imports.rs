//! Resumable import execution on an admitted database.
//!
//! This module deliberately owns orchestration only. [`ImportWriteTarget`] is
//! the seam to the ordinary document write path: its implementation must use
//! the same authorization, write budget, durability, and idempotency rules as
//! a public batch write.

use super::{ApiState, attach_write_budget, document_scope};
use crate::{
    export_formats::{self, ImportRecord, TransferFormat},
    import_runtime::ImportInputReader,
    import_store::{ImportJob, ImportStore},
};
use std::sync::Arc;
use std::{
    io,
    path::Path,
    sync::atomic::{AtomicBool, Ordering},
};
use terrabase::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
};

/// Maximum number of records in one admitted engine transaction.
pub(crate) const IMPORT_BATCH_RECORDS: usize = 256;

/// Result of one ordinary admitted write transaction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ImportCommit {
    /// The target durably committed the complete batch.
    Committed { records: u64 },
    /// The target rejected the batch before committing any records.
    Rejected,
    /// The commit may have landed. The checkpoint must not advance.
    Unknown,
}

/// Errors returned by the real engine write adapter.
pub(crate) trait ImportWriteTarget: Send + Sync {
    fn commit_import_batch(
        &self,
        operation_id: &str,
        database_id: &str,
        records: &[ImportRecord],
    ) -> Result<ImportCommit, String>;
}

/// Production import target bound to the same Engine transaction seams as the
/// public document batch route. The import executor supplies deterministic
/// operation identities; the target owns validation, metering, and durable
/// commit of each batch.
pub(crate) struct EngineImportWriteTarget {
    state: Arc<ApiState>,
    engine: Arc<terrabase_tbep::member_host::MemberHost>,
    tenant_id: String,
    project_id: String,
}

impl EngineImportWriteTarget {
    pub(crate) fn new(
        state: Arc<ApiState>,
        engine: Arc<terrabase_tbep::member_host::MemberHost>,
        tenant_id: String,
        project_id: String,
    ) -> Self {
        Self { state, engine, tenant_id, project_id }
    }
}

impl ImportWriteTarget for EngineImportWriteTarget {
    fn commit_import_batch(
        &self,
        operation_id: &str,
        database_id: &str,
        records: &[ImportRecord],
    ) -> Result<ImportCommit, String> {
        if records.is_empty() {
            return Ok(ImportCommit::Committed { records: 0 });
        }
        let engine = self.engine.engine();
        if engine.database_id().to_text() != database_id {
            return Err("import database does not match the admitted engine".into());
        }
        let owner = engine.database_owner();
        if owner.tenant().to_string() != self.tenant_id
            || owner.project().to_string() != self.project_id
        {
            return Err("import owner does not match the admitted engine".into());
        }
        let scope = document_scope(engine).map_err(|_| "document scope unavailable".to_owned())?;
        let operation = OperationId::try_new(operation_id)
            .map_err(|_| "import batch operation identity is invalid".to_owned())?;
        let snapshot =
            engine.read_snapshot(&scope).map_err(|_| "snapshot unavailable".to_owned())?;
        let mut transaction = engine
            .begin(&scope, snapshot, operation)
            .map_err(|_| "import transaction unavailable".to_owned())?;
        let mut units = 0_u64;
        for record in records {
            let collection = CollectionName::try_new(&record.collection)
                .map_err(|_| "import collection identifier is invalid".to_owned())?;
            let id = DocumentId::try_new(&record.id)
                .map_err(|_| "import document identifier is invalid".to_owned())?;
            let value: serde_json::Value = serde_json::from_slice(&record.value)
                .map_err(|_| "import document value is invalid JSON".to_owned())?;
            let value = super::json_value(&value);
            units = units
                .saturating_add(terrabase::usage::write_units_for_bytes(record.value.len() as u64));
            transaction
                .put(Document::new(id, collection, value), ExpectedVersion::Any)
                .map_err(|_| "import document could not stage".to_owned())?;
        }
        let operation_id = transaction.operation_id().clone();
        attach_write_budget(
            &self.state,
            &mut transaction,
            &scope,
            &self.project_id,
            database_id,
            &operation_id,
            units,
        )
        .map_err(|error| error.message)?;

        // Imports use the ordinary synced local commit. Regional/global
        // admission is performed by the serving route before this synchronous
        // target is invoked; a commit outcome is never guessed here.
        match transaction.commit(LocalDurability::Synced) {
            terrabase::CommitOutcome::Committed(receipt) => {
                let _ = (&self.tenant_id, receipt);
                Ok(ImportCommit::Committed { records: records.len() as u64 })
            }
            terrabase::CommitOutcome::Rejected(_) => Ok(ImportCommit::Rejected),
            terrabase::CommitOutcome::Unknown(_) => Ok(ImportCommit::Unknown),
        }
    }
}

#[derive(Debug)]
pub(crate) enum ImportExecutionError {
    Input(io::Error),
    Codec(export_formats::CodecError),
    Store(String),
    Rejected(String),
    Unknown(String),
    Cancelled,
}

impl From<io::Error> for ImportExecutionError {
    fn from(error: io::Error) -> Self {
        Self::Input(error)
    }
}

/// Executes a committed upload from its durable cursor. Every batch has a
/// deterministic operation identity, and its cursor is persisted only after
/// the write target reports a proven commit. A failed or cancelled operation
/// remains resumable from its last checkpoint.
pub(crate) fn execute_import<C, T>(
    store: &ImportStore,
    job: &ImportJob,
    reader: ImportInputReader<C>,
    target: &T,
    cancellation: &AtomicBool,
) -> Result<ImportJob, ImportExecutionError>
where
    C: crate::import_runtime::ImportCipher,
    T: ImportWriteTarget,
{
    if !job.input_committed {
        return Err(ImportExecutionError::Store("import input is not committed".into()));
    }
    let digest = job
        .input_digest
        .as_deref()
        .ok_or_else(|| ImportExecutionError::Store("import digest is missing".into()))?;
    let database_id = job.database_id.as_str();
    let mut cursor = job.cursor;
    let mut committed_records = job.committed_records;
    let mut ordinal = committed_records / IMPORT_BATCH_RECORDS as u64;
    let mut batch = Vec::with_capacity(IMPORT_BATCH_RECORDS);
    let mut batch_cursor = cursor;
    let mut batch_error = None;
    let mut result = Ok(());
    let format = parse_format(&job.format)?;

    let mut line = Vec::new();
    let mut source_offset = 0u64;
    let mut decode_error = None;
    let decode_result = reader.for_each_chunk(digest, job.input_length.unwrap_or(0), |chunk| {
        for byte in chunk {
            source_offset = source_offset
                .checked_add(1)
                .ok_or_else(|| io::Error::other("import offset overflow"))?;
            if *byte != b'\n' {
                if line.len() >= 16 * 1024 * 1024 {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidData,
                        "import record too large",
                    ));
                }
                line.push(*byte);
                continue;
            }
            let record = export_formats::decode(&line, format, 16 * 1024 * 1024)
                .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "invalid import record"))?
                .into_iter()
                .next();
            line.clear();
            let Some(record) = record else {
                continue;
            };
            if cancellation.load(Ordering::Acquire) {
                return Err(io::Error::new(io::ErrorKind::Interrupted, "import cancelled"));
            }
            if source_offset <= cursor {
                continue;
            }
            batch.push(record);
            batch_cursor = source_offset;
            if batch.len() < IMPORT_BATCH_RECORDS {
                continue;
            }
            let next = match commit_batch(
                store,
                target,
                job,
                digest,
                database_id,
                ordinal,
                source_offset,
                &mut batch,
                committed_records,
            ) {
                Ok(next) => next,
                Err(error) => {
                    batch_error = Some(error);
                    return Err(io::Error::new(io::ErrorKind::Interrupted, "import batch failed"));
                }
            };
            cursor = next.0;
            committed_records = next.1;
            ordinal = ordinal
                .checked_add(1)
                .ok_or_else(|| io::Error::other("import batch ordinal overflow"))?;
        }
        Ok(())
    });
    if decode_result.is_ok() && !line.iter().all(u8::is_ascii_whitespace) {
        decode_error = Some(io::Error::new(
            io::ErrorKind::InvalidData,
            "import input lacks newline terminator",
        ));
    }
    let decode_result = decode_result.and_then(|_| decode_error.map_or(Ok(()), Err));
    if let Some(error) = batch_error {
        result = Err(error);
    } else if let Err(error) = decode_result {
        result =
            if cancellation.load(Ordering::Acquire) || error.kind() == io::ErrorKind::Interrupted {
                Err(ImportExecutionError::Cancelled)
            } else {
                Err(ImportExecutionError::Input(error))
            };
    }
    if result.is_ok() && !batch.is_empty() {
        let offset = batch_cursor.max(job.cursor);
        match commit_batch(
            store,
            target,
            job,
            digest,
            database_id,
            ordinal,
            offset,
            &mut batch,
            committed_records,
        ) {
            Ok((next_cursor, next_records)) => {
                cursor = next_cursor;
                committed_records = next_records;
            }
            Err(error) => result = Err(error),
        }
    }
    if let Err(error) = result {
        let text = match &error {
            ImportExecutionError::Cancelled => "import cancelled".into(),
            ImportExecutionError::Input(_) | ImportExecutionError::Codec(_) => {
                "invalid import input".into()
            }
            ImportExecutionError::Store(_) => "import metadata unavailable".into(),
            ImportExecutionError::Rejected(_) => "import batch rejected".into(),
            ImportExecutionError::Unknown(_) => "import commit outcome unknown".into(),
        };
        let _ = store.fail(&job.operation_id, &text);
        return Err(error);
    }
    store
        .complete(&job.operation_id)
        .map_err(|error| ImportExecutionError::Store(error.to_string()))?;
    store
        .get(&job.operation_id)
        .map_err(|error| ImportExecutionError::Store(error.to_string()))?
        .ok_or_else(|| ImportExecutionError::Store("import operation disappeared".into()))
}

fn commit_batch<T: ImportWriteTarget>(
    store: &ImportStore,
    target: &T,
    job: &ImportJob,
    digest: &str,
    database_id: &str,
    ordinal: u64,
    cursor: u64,
    batch: &mut Vec<ImportRecord>,
    committed_records: u64,
) -> Result<(u64, u64), ImportExecutionError> {
    let operation = export_formats::batch_operation_id(&job.operation_id, digest, ordinal);
    let count = batch.len() as u64;
    let outcome = target
        .commit_import_batch(&operation, database_id, batch)
        .map_err(ImportExecutionError::Rejected)?;
    match outcome {
        ImportCommit::Committed { records } if records == count => {
            let total = committed_records.checked_add(records).ok_or_else(|| {
                ImportExecutionError::Store("import record count overflow".into())
            })?;
            store
                .checkpoint(&job.operation_id, cursor, total)
                .map_err(|error| ImportExecutionError::Store(error.to_string()))?;
            batch.clear();
            Ok((cursor, total))
        }
        ImportCommit::Committed { .. } => Err(ImportExecutionError::Rejected(
            "import target committed an unexpected record count".into(),
        )),
        ImportCommit::Rejected => {
            Err(ImportExecutionError::Rejected("import batch rejected".into()))
        }
        ImportCommit::Unknown => Err(ImportExecutionError::Unknown(operation)),
    }
}

fn parse_format(value: &str) -> Result<TransferFormat, ImportExecutionError> {
    serde_json::from_str(&format!("\"{value}\""))
        .map_err(|_| ImportExecutionError::Codec(export_formats::CodecError::Malformed))
}

/// Opens and executes a durable upload after the caller has authenticated the
/// operation and selected the database's import encryption lease.
pub(crate) fn execute_import_file<C, T>(
    store: &ImportStore,
    job: &ImportJob,
    path: impl AsRef<Path>,
    reader: ImportInputReader<C>,
    target: &T,
    cancellation: &AtomicBool,
) -> Result<ImportJob, ImportExecutionError>
where
    C: crate::import_runtime::ImportCipher,
    T: ImportWriteTarget,
{
    let _ = path;
    execute_import(store, job, reader, target, cancellation)
}
