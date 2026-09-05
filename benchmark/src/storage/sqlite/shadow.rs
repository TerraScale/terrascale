//! Scoped, bounded shadow reads over one pinned SQLite transaction.

use super::{SqliteStorage, filtered_page_sql, pushed_filter, quote_identifier};
use crate::storage::{
    MAX_SHADOW_ROWS, MAX_SHADOW_VM_STEPS, ShadowReadComparison, ShadowReadError,
    ShadowReadMeasurement, ShadowReadRequest, StoragePort,
};
use crate::types::{DocumentId, DocumentValue, StorageSequence, TenantId};
use rusqlite::{
    Connection, StatementStatus,
    types::{Value, ValueRef},
};
use std::sync::{
    Arc,
    atomic::{AtomicBool, AtomicU32, Ordering},
};

const MAX_RESULT_BYTES: usize = 8 * 1024 * 1024;

impl SqliteStorage {
    pub(super) fn measure_shadow_page(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        request: &ShadowReadRequest<'_>,
    ) -> Result<ShadowReadComparison, ShadowReadError> {
        if !(1..=MAX_SHADOW_ROWS).contains(&request.limit)
            || !(1..=MAX_SHADOW_VM_STEPS).contains(&request.max_vm_steps)
            || request.candidate_field.len() > 1024
            || request.filter.conditions().iter().any(|condition| {
                condition.field.len() > 1024
                    || matches!(&condition.value, DocumentValue::String(value) if value.len() > MAX_RESULT_BYTES)
            })
        {
            return Err(ShadowReadError::InvalidRequest);
        }
        let parameter_bytes = request
            .filter
            .conditions()
            .iter()
            .try_fold(0_usize, |total, condition| {
                let bytes = match &condition.value {
                    DocumentValue::String(value) => value.len(),
                    _ => 0,
                };
                total.checked_add(bytes)
            })
            .ok_or(ShadowReadError::InvalidRequest)?;
        if parameter_bytes > MAX_RESULT_BYTES {
            return Err(ShadowReadError::InvalidRequest);
        }
        if !self.snapshot_boundary_available(sequence) {
            return Err(ShadowReadError::Unavailable);
        }
        let extracted = self.extracted.lock().map_err(|_| ShadowReadError::Unavailable)?;
        if !extracted.is_active(request.collection.as_str(), request.candidate_field) {
            return Err(ShadowReadError::InvalidRequest);
        }
        let condition = request
            .filter
            .conditions()
            .iter()
            .find(|condition| condition.field == request.candidate_field)
            .ok_or(ShadowReadError::InvalidRequest)?;
        let suffix = match condition.value {
            DocumentValue::String(_) => "t",
            DocumentValue::Integer(_) | DocumentValue::Number(_) => "n",
            _ => return Err(ShadowReadError::InvalidRequest),
        };
        let (conditions, values) = pushed_filter(&extracted, request.collection, request.filter);
        drop(extracted);
        if conditions.is_empty() {
            return Err(ShadowReadError::InvalidRequest);
        }
        let ordinary_sql = filtered_page_sql(&conditions);
        let index = quote_identifier(&format!("i:{}#{suffix}", request.candidate_field));
        let candidate_sql = ordinary_sql.replacen(
            "FROM documents AS current",
            &format!("FROM documents AS current INDEXED BY {index}"),
            1,
        );
        let mut parameters = vec![
            Value::Text(tenant.as_str().to_owned()),
            Value::Text(request.collection.as_str().to_owned()),
            Value::Integer(
                i64::try_from(sequence.get()).map_err(|_| ShadowReadError::InvalidRequest)?,
            ),
            request
                .after
                .map_or(Value::Null, |id: &DocumentId| Value::Text(id.as_str().to_owned())),
        ];
        parameters.extend(values);
        parameters.push(Value::Integer(i64::from(request.limit)));
        let reader = self.reader();
        let connection = reader.connection();
        let transaction =
            connection.unchecked_transaction().map_err(|_| ShadowReadError::Unavailable)?;
        let candidate_sql = if let Some(id) = request.hidden_candidate {
            let scalar = quote_identifier(&match suffix {
                "t" => super::text_column(request.candidate_field),
                _ => super::numeric_column(request.candidate_field),
            });
            super::invisible::shadow_sql(
                &transaction,
                tenant.as_str(),
                request.collection.as_str(),
                id,
                &scalar,
                &conditions,
            )
            .map_err(|()| ShadowReadError::InvalidRequest)?
        } else {
            candidate_sql
        };
        let ordinary = measure(&transaction, &ordinary_sql, &parameters, request.max_vm_steps)?;
        // ADR 0045's 1.5x work cap plus a small fixed floor. This is an
        // instruction bound, not a substitute for page/row verification.
        let candidate_budget = ordinary
            .progress_steps
            .saturating_mul(3)
            .div_ceil(2)
            .saturating_add(100)
            .min(request.max_vm_steps);
        let candidate = measure(&transaction, &candidate_sql, &parameters, candidate_budget)?;
        if ordinary.rows != candidate.rows || ordinary.result_digest != candidate.result_digest {
            return Err(ShadowReadError::ResultMismatch);
        }
        transaction.commit().map_err(|_| ShadowReadError::Unavailable)?;
        Ok(ShadowReadComparison { ordinary, candidate })
    }
}

struct ProgressGuard<'a>(&'a Connection);
impl Drop for ProgressGuard<'_> {
    fn drop(&mut self) {
        let _ = self.0.progress_handler(0, None::<fn() -> bool>);
    }
}

fn measure(
    connection: &Connection,
    sql: &str,
    parameters: &[Value],
    budget: u32,
) -> Result<ShadowReadMeasurement, ShadowReadError> {
    // Prepare the bounded generated SQL before starting the execution meter;
    // SQLite may invoke progress callbacks while compiling a statement too.
    let mut statement = connection.prepare(sql).map_err(|_| ShadowReadError::Unavailable)?;
    let exhausted = Arc::new(AtomicBool::new(false));
    let progress_exhausted = Arc::clone(&exhausted);
    let steps = Arc::new(AtomicU32::new(0));
    let progress_steps = Arc::clone(&steps);
    connection
        .progress_handler(
            1,
            Some(move || {
                let count = progress_steps.fetch_add(1, Ordering::Relaxed) + 1;
                let stop = count > budget;
                if stop {
                    progress_exhausted.store(true, Ordering::Relaxed);
                }
                stop
            }),
        )
        .map_err(|_| ShadowReadError::Unavailable)?;
    let _progress = ProgressGuard(connection);
    let sql_error = |_| {
        if exhausted.load(Ordering::Relaxed) {
            ShadowReadError::BudgetExceeded
        } else {
            ShadowReadError::Unavailable
        }
    };
    let mut rows = statement.query(rusqlite::params_from_iter(parameters)).map_err(sql_error)?;
    let mut digest = blake3::Hasher::new();
    let mut count = 0;
    let mut bytes = 0_usize;
    loop {
        let row = rows.next().map_err(|_| {
            if exhausted.load(Ordering::Relaxed) {
                ShadowReadError::BudgetExceeded
            } else {
                ShadowReadError::Unavailable
            }
        })?;
        let Some(row) = row else { break };
        count += 1;
        for column in 0..4 {
            let value = row.get_ref(column).map_err(|_| ShadowReadError::Unavailable)?;
            let is_text = matches!(value, ValueRef::Text(_));
            match value {
                ValueRef::Null => {
                    digest.update(&[0]);
                }
                ValueRef::Integer(value) => {
                    digest.update(&[1]);
                    digest.update(&value.to_le_bytes());
                }
                ValueRef::Text(value) | ValueRef::Blob(value) => {
                    bytes =
                        bytes.checked_add(value.len()).ok_or(ShadowReadError::BudgetExceeded)?;
                    if bytes > MAX_RESULT_BYTES {
                        return Err(ShadowReadError::BudgetExceeded);
                    }
                    digest.update(&[if is_text { 2 } else { 3 }]);
                    digest.update(&(value.len() as u64).to_le_bytes());
                    digest.update(value);
                }
                ValueRef::Real(_) => return Err(ShadowReadError::Unavailable),
            }
        }
    }
    drop(rows);
    Ok(ShadowReadMeasurement {
        progress_steps: steps.load(Ordering::Relaxed),
        vm_steps: u32::try_from(statement.get_status(StatementStatus::VmStep))
            .map_err(|_| ShadowReadError::Unavailable)?,
        full_scan_steps: u32::try_from(statement.get_status(StatementStatus::FullscanStep))
            .map_err(|_| ShadowReadError::Unavailable)?,
        rows: count,
        result_digest: *digest.finalize().as_bytes(),
    })
}
