use super::{SqliteStorage, accounting};
use crate::{
    engine::lifecycle_state::{LifecycleCommand, LifecycleError, LifecycleState},
    types::TenantId,
};
use rusqlite::{OptionalExtension, TransactionBehavior, params};

impl SqliteStorage {
    /// Applies a reversible operation and returns its immutable receipt.
    pub(super) fn update_lifecycle_operation(
        &self,
        tenant: &TenantId,
        command: LifecycleCommand,
    ) -> Result<crate::engine::lifecycle_state::LifecycleOperationOutcome, LifecycleError> {
        let state = self.update_lifecycle(tenant, command.clone())?;
        let operation_id = match &command {
            LifecycleCommand::Pause(id) | LifecycleCommand::Resume(id) => id,
            _ => return Err(LifecycleError::Invalid),
        };
        let writer = self.writer.lock().map_err(|_| LifecycleError::Unavailable)?;
        let receipt_row: (String, i64, i64) = writer
            .query_row(
                "SELECT action, paused, generation FROM lifecycle_operations WHERE tenant=?1 AND operation_id=?2",
                params![tenant.as_str(), operation_id],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
            )
            .optional()
            .map_err(|_| LifecycleError::Unavailable)?
            .ok_or(LifecycleError::Unavailable)?;
        let (action, paused, generation) = receipt_row;
        let generation = u64::try_from(generation).map_err(|_| LifecycleError::Invalid)?;
        let receipt = crate::engine::lifecycle_state::LifecycleOperationReceipt {
            operation_id: operation_id.clone(),
            action,
            paused: paused != 0,
            generation,
        };
        Ok(crate::engine::lifecycle_state::LifecycleOperationOutcome { state, receipt })
    }

    pub(super) fn update_lifecycle(
        &self,
        tenant: &TenantId,
        command: LifecycleCommand,
    ) -> Result<LifecycleState, LifecycleError> {
        let mut writer = self.writer.lock().map_err(|_| LifecycleError::Unavailable)?;
        let tx = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| LifecycleError::Unavailable)?;
        let bytes: Option<Vec<u8>> = tx
            .query_row(
                "SELECT body FROM database_lifecycle WHERE tenant=?1",
                [tenant.as_str()],
                |r| r.get(0),
            )
            .optional()
            .map_err(|_| LifecycleError::Unavailable)?;
        let mut state: LifecycleState = bytes.map(|b| decode(&b)).transpose()?.unwrap_or_default();
        let before = state.clone();
        let operation = match &command {
            LifecycleCommand::Pause(id) => Some((id.clone(), true)),
            LifecycleCommand::Resume(id) => Some((id.clone(), false)),
            _ => None,
        };
        if let Some((id, paused)) = operation.as_ref() {
            if id.is_empty() || id.len() > 128 {
                return Err(LifecycleError::Invalid);
            }
            let prior: Option<(String, i64, i64)> = tx.query_row(
                "SELECT action, paused, generation FROM lifecycle_operations WHERE tenant=?1 AND operation_id=?2",
                params![tenant.as_str(), id], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?))).optional()
                .map_err(|_| LifecycleError::Unavailable)?;
            if let Some((action, prior_paused, generation)) = prior {
                let expected = if *paused { "pause" } else { "resume" };
                if action != expected || prior_paused != i64::from(*paused) {
                    return Err(LifecycleError::Conflict);
                }
                let generation = u64::try_from(generation).map_err(|_| LifecycleError::Invalid)?;
                let _ = generation;
                return Ok(state);
            }
        }
        let stopped: Option<i64> = tx
            .query_row(
                "SELECT millis FROM storage_accounting_stops WHERE tenant=?1",
                [tenant.as_str()],
                |r| r.get(0),
            )
            .optional()
            .map_err(|_| LifecycleError::Unavailable)?;
        // Refuse inconsistent legacy/external fences instead of inventing intent.
        if stopped.map(|v| u64::try_from(v).ok())
            != state.deletion.as_ref().map(|d| Some(d.usage_stopped_at))
        {
            return Err(LifecycleError::Conflict);
        }
        match command {
            LifecycleCommand::Read => return Ok(state),
            LifecycleCommand::Prepare(operation_id) => {
                if operation_id.is_empty() || operation_id.len() > 255 {
                    return Err(LifecycleError::Invalid);
                }
                if let Some(prior) = &state.deletion_preparation {
                    return if prior.operation_id == operation_id {
                        Ok(state)
                    } else {
                        Err(LifecycleError::Conflict)
                    };
                }
                if state.deletion.is_some() {
                    return Err(LifecycleError::Conflict);
                }
                if state.deletion_protection {
                    return Err(LifecycleError::Protected);
                }
                let clock: i64 = tx
                    .query_row(
                        "SELECT millis FROM storage_accounting_clock WHERE singleton=1",
                        [],
                        |r| r.get(0),
                    )
                    .map_err(|_| LifecycleError::Unavailable)?;
                let now = u64::try_from(
                    std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .map_err(|_| LifecycleError::Invalid)?
                        .as_millis(),
                )
                .map_err(|_| LifecycleError::Invalid)?;
                let minimum_cutoff_millis =
                    now.max(u64::try_from(clock).map_err(|_| LifecycleError::Invalid)?);
                if minimum_cutoff_millis > i64::MAX as u64 {
                    return Err(LifecycleError::Invalid);
                }
                let (epoch, _) =
                    super::origin_epoch_on(&tx).map_err(|_| LifecycleError::Unavailable)?;
                tx.execute("INSERT INTO meta(key,value) VALUES('origin_authority_epoch',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [format!("{epoch}:1")]).map_err(|_| LifecycleError::Unavailable)?;
                state.deletion_preparation =
                    Some(crate::engine::lifecycle_state::DeletionPreparation {
                        operation_id,
                        minimum_cutoff_millis,
                    });
            }

            LifecycleCommand::Protection(enabled) => {
                if state.access_revoked() {
                    return Err(LifecycleError::Revoked);
                }
                state.deletion_protection = enabled;
            }
            LifecycleCommand::Activity(at) => {
                if at == 0 || at > i64::MAX as u64 {
                    return Err(LifecycleError::Invalid);
                }
                if state.access_revoked() {
                    return Err(LifecycleError::Revoked);
                }
                state.last_authorized_request_at =
                    Some(state.last_authorized_request_at.unwrap_or(0).max(at));
            }
            LifecycleCommand::Delete(intent) => {
                if intent.operation_id.is_empty()
                    || intent.operation_id.len() > 255
                    || intent.usage_stopped_at == 0
                    || intent.retain_until.is_some_and(|until| until < intent.usage_stopped_at)
                    || intent.retain_until.is_some_and(|until| until > i64::MAX as u64)
                {
                    return Err(LifecycleError::Invalid);
                }
                if let Some(prior) = &state.deletion {
                    return if prior == &intent {
                        Ok(state)
                    } else {
                        Err(LifecycleError::Conflict)
                    };
                }
                if state.deletion_protection {
                    return Err(LifecycleError::Protected);
                }
                if let Some(prepared) = &state.deletion_preparation {
                    if prepared.operation_id != intent.operation_id
                        || intent.usage_stopped_at < prepared.minimum_cutoff_millis
                    {
                        return Err(LifecycleError::Conflict);
                    }
                }
                accounting::stop(&tx, tenant.as_str(), intent.usage_stopped_at)
                    .map_err(|()| LifecycleError::Conflict)?;
                state.deletion = Some(intent);
            }
            LifecycleCommand::Pause(_) => {
                if state.access_revoked() {
                    return Err(LifecycleError::Revoked);
                }
                state.paused = true;
            }
            LifecycleCommand::Resume(_) => {
                if state.access_revoked() {
                    return Err(LifecycleError::Revoked);
                }
                state.paused = false;
            }
        }
        if state == before {
            return Ok(state);
        }
        state.generation = state.generation.checked_add(1).ok_or(LifecycleError::Invalid)?;
        let body = encode(&state);
        tx.execute("INSERT INTO database_lifecycle VALUES (?1,?2) ON CONFLICT(tenant) DO UPDATE SET body=excluded.body", params![tenant.as_str(), body]).map_err(|_| LifecycleError::Unavailable)?;
        if let Some((id, paused)) = operation {
            let generation =
                i64::try_from(state.generation).map_err(|_| LifecycleError::Invalid)?;
            tx.execute("INSERT INTO lifecycle_operations(tenant,operation_id,action,paused,generation) VALUES (?1,?2,?3,?4,?5)", params![tenant.as_str(), id, if paused { "pause" } else { "resume" }, i64::from(paused), generation]).map_err(|_| LifecycleError::Unavailable)?;
        }
        tx.commit().map_err(|_| LifecycleError::Unavailable)?;
        Ok(state)
    }
}

fn encode(state: &LifecycleState) -> Vec<u8> {
    let mut body = Vec::with_capacity(560);
    body.extend_from_slice(&state.generation.to_be_bytes());
    body.extend_from_slice(&state.last_authorized_request_at.unwrap_or(0).to_be_bytes());
    body.push(u8::from(state.deletion_protection));
    let prepared = state.deletion_preparation.as_ref();
    body.extend_from_slice(&prepared.map_or(0, |p| p.minimum_cutoff_millis).to_be_bytes());
    let deletion = state.deletion.as_ref();
    body.extend_from_slice(&deletion.map_or(0, |d| d.usage_stopped_at).to_be_bytes());
    body.extend_from_slice(&deletion.and_then(|d| d.retain_until).unwrap_or(0).to_be_bytes());
    for operation in
        [prepared.map(|p| p.operation_id.as_str()), deletion.map(|d| d.operation_id.as_str())]
    {
        let bytes = operation.unwrap_or("").as_bytes();
        body.push(u8::try_from(bytes.len()).expect("validated operation identity"));
        body.extend_from_slice(bytes);
    }
    body.push(u8::from(state.paused));
    body
}

fn decode(body: &[u8]) -> Result<LifecycleState, LifecycleError> {
    let invalid = || LifecycleError::Invalid;
    let number = |at: usize| -> Result<u64, LifecycleError> {
        Ok(u64::from_be_bytes(
            body.get(at..at + 8).ok_or_else(invalid)?.try_into().map_err(|_| invalid())?,
        ))
    };
    if body.len() < 44 || body.len() > 554 || body[16] > 1 {
        return Err(invalid());
    }
    let at = number(8)?;
    let minimum_cutoff_millis = number(17)?;
    let usage_stopped_at = number(25)?;
    let retain_until = number(33)?;
    let mut cursor = 41;
    let mut operation = || -> Result<String, LifecycleError> {
        let size = usize::from(*body.get(cursor).ok_or_else(invalid)?);
        cursor += 1;
        let value = std::str::from_utf8(body.get(cursor..cursor + size).ok_or_else(invalid)?)
            .map_err(|_| invalid())?
            .to_owned();
        cursor += size;
        Ok(value)
    };
    let preparation_id = operation()?;
    let deletion_id = operation()?;
    let Some([paused]) = body.get(cursor..) else {
        return Err(invalid());
    };
    if *paused > 1
        || at > i64::MAX as u64
        || minimum_cutoff_millis > i64::MAX as u64
        || retain_until > i64::MAX as u64
        || (retain_until != 0 && usage_stopped_at > retain_until)
        || preparation_id.is_empty() != (minimum_cutoff_millis == 0)
        || deletion_id.is_empty() != (usage_stopped_at == 0)
    {
        return Err(invalid());
    }
    let deletion_preparation = (!preparation_id.is_empty()).then_some(
        crate::engine::lifecycle_state::DeletionPreparation {
            operation_id: preparation_id,
            minimum_cutoff_millis,
        },
    );
    let deletion =
        (!deletion_id.is_empty()).then_some(crate::engine::lifecycle_state::DeletionIntent {
            operation_id: deletion_id,
            usage_stopped_at,
            retain_until: (retain_until != 0).then_some(retain_until),
        });
    Ok(LifecycleState {
        generation: number(0)?,
        last_authorized_request_at: (at != 0).then_some(at),
        deletion_protection: body[16] != 0,
        paused: *paused != 0,
        deletion_preparation,
        deletion,
    })
}

pub(super) fn permits_origin_activation(connection: &rusqlite::Connection) -> Result<bool, ()> {
    let mut query = connection.prepare("SELECT body FROM database_lifecycle").map_err(|_| ())?;
    let mut rows = query.query([]).map_err(|_| ())?;
    while let Some(row) = rows.next().map_err(|_| ())? {
        let body: Vec<u8> = row.get(0).map_err(|_| ())?;
        if decode(&body).map_err(|_| ())?.access_revoked() {
            return Ok(false);
        }
    }
    Ok(true)
}
