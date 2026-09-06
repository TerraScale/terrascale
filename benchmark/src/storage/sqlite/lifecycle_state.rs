use super::{SqliteStorage, accounting};
use crate::{
    engine::lifecycle_state::{LifecycleCommand, LifecycleError, LifecycleState},
    types::TenantId,
};
use rusqlite::{OptionalExtension, TransactionBehavior, params};

impl SqliteStorage {
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
            LifecycleCommand::Protection(enabled) => {
                if state.deletion.is_some() {
                    return Err(LifecycleError::Revoked);
                }
                state.deletion_protection = enabled;
            }
            LifecycleCommand::Activity(at) => {
                if at == 0 || at > i64::MAX as u64 {
                    return Err(LifecycleError::Invalid);
                }
                if state.deletion.is_some() {
                    return Err(LifecycleError::Revoked);
                }
                state.last_authorized_request_at =
                    Some(state.last_authorized_request_at.unwrap_or(0).max(at));
            }
            LifecycleCommand::Delete(intent) => {
                if intent.operation_id.is_empty()
                    || intent.operation_id.len() > 255
                    || intent.usage_stopped_at == 0
                    || intent.retain_until < intent.usage_stopped_at
                    || intent.retain_until > i64::MAX as u64
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
                accounting::stop(&tx, tenant.as_str(), intent.usage_stopped_at)
                    .map_err(|()| LifecycleError::Conflict)?;
                state.deletion = Some(intent);
            }
        }
        if state == before {
            return Ok(state);
        }
        state.generation = state.generation.checked_add(1).ok_or(LifecycleError::Invalid)?;
        let body = encode(&state);
        tx.execute("INSERT INTO database_lifecycle VALUES (?1,?2) ON CONFLICT(tenant) DO UPDATE SET body=excluded.body", params![tenant.as_str(), body]).map_err(|_| LifecycleError::Unavailable)?;
        tx.commit().map_err(|_| LifecycleError::Unavailable)?;
        Ok(state)
    }
}

fn encode(state: &LifecycleState) -> Vec<u8> {
    let mut body = Vec::with_capacity(300);
    body.extend_from_slice(&state.generation.to_be_bytes());
    body.extend_from_slice(&state.last_authorized_request_at.unwrap_or(0).to_be_bytes());
    body.push(u8::from(state.deletion_protection));
    if let Some(intent) = &state.deletion {
        body.extend_from_slice(&intent.usage_stopped_at.to_be_bytes());
        body.extend_from_slice(&intent.retain_until.to_be_bytes());
        body.extend_from_slice(intent.operation_id.as_bytes());
    }
    body
}

fn decode(body: &[u8]) -> Result<LifecycleState, LifecycleError> {
    let invalid = || LifecycleError::Invalid;
    let number = |at: usize| -> Result<u64, LifecycleError> {
        Ok(u64::from_be_bytes(
            body.get(at..at + 8).ok_or_else(invalid)?.try_into().map_err(|_| invalid())?,
        ))
    };
    if body.len() < 17 || body.len() > 288 || body[16] > 1 {
        return Err(invalid());
    }
    let at = number(8)?;
    let deletion = if body.len() == 17 {
        None
    } else {
        let operation_id =
            std::str::from_utf8(body.get(33..).ok_or_else(invalid)?).map_err(|_| invalid())?;
        let usage_stopped_at = number(17)?;
        let retain_until = number(25)?;
        if operation_id.is_empty()
            || usage_stopped_at == 0
            || retain_until < usage_stopped_at
            || retain_until > i64::MAX as u64
        {
            return Err(invalid());
        }
        Some(crate::engine::lifecycle_state::DeletionIntent {
            operation_id: operation_id.into(),
            usage_stopped_at,
            retain_until,
        })
    };
    if at > i64::MAX as u64 {
        return Err(invalid());
    }
    Ok(LifecycleState {
        generation: number(0)?,
        last_authorized_request_at: (at != 0).then_some(at),
        deletion_protection: body[16] != 0,
        deletion,
    })
}
