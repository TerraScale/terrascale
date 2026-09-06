use super::*;

const MAX_RECORDS: u64 = 1000;
const MAX_BYTES: usize = 4 * 1024 * 1024;
const MAX_TRANSACTIONS: u64 = 32;
const MAX_OPERATIONS: u32 = 100;
const MAX_LIFETIME_MS: u64 = 30_000;

#[derive(Serialize, Deserialize)]
struct Workspace {
    records: Vec<(String, String, Vec<u8>)>,
    indexes: Vec<(String, Vec<u8>)>,
    revision: u64,
    expires: u64,
    operations: u32,
    failed: bool,
}

fn capture(
    transaction: &Transaction<'_>,
    revision: u64,
    expires: u64,
) -> Result<Option<Workspace>> {
    let count: u64 = transaction
        .query_row("SELECT COUNT(*) FROM auth_records", [], |row| row.get(0))
        .map_err(storage)?;
    let bytes: u64 = transaction
        .query_row("SELECT COALESCE(SUM(length(body)),0) FROM auth_records", [], |row| row.get(0))
        .map_err(storage)?;
    let indexes: u64 = transaction
        .query_row("SELECT COUNT(*) FROM auth_indexes", [], |row| row.get(0))
        .map_err(storage)?;
    if count > MAX_RECORDS || bytes > MAX_BYTES as u64 || indexes > 128 {
        return Ok(None);
    }
    let workspace = Workspace {
        records: transaction
            .prepare("SELECT model,id,body FROM auth_records ORDER BY model,id")
            .map_err(storage)?
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))
            .map_err(storage)?
            .collect::<std::result::Result<_, _>>()
            .map_err(storage)?,
        indexes: transaction
            .prepare("SELECT model,fields FROM auth_indexes ORDER BY model,fields")
            .map_err(storage)?
            .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
            .map_err(storage)?
            .collect::<std::result::Result<_, _>>()
            .map_err(storage)?,
        revision,
        expires,
        operations: 0,
        failed: false,
    };
    if encode(&workspace)?.len() > MAX_BYTES {
        return Ok(None);
    }
    Ok(Some(workspace))
}

fn load(transaction: &Transaction<'_>, id: &str) -> Result<Option<Workspace>> {
    let bytes: Option<Vec<u8>> = transaction
        .query_row("SELECT workspace FROM auth_transactions WHERE id=?1", [id], |row| row.get(0))
        .optional()
        .map_err(storage)?;
    bytes.map(|bytes| decode(&bytes)).transpose()
}
fn save(transaction: &Transaction<'_>, id: &str, workspace: &Workspace) -> Result<()> {
    transaction.execute("INSERT INTO auth_transactions(id,workspace) VALUES(?1,?2) ON CONFLICT(id) DO UPDATE SET workspace=excluded.workspace",params![id,encode(workspace)?]).map_err(storage)?;
    Ok(())
}
fn install(transaction: &Transaction<'_>, workspace: &Workspace) -> Result<()> {
    transaction
        .execute_batch("DELETE FROM auth_records; DELETE FROM auth_indexes;")
        .map_err(storage)?;
    for (model, id, body) in &workspace.records {
        transaction
            .execute("INSERT INTO auth_records VALUES(?1,?2,?3)", params![model, id, body])
            .map_err(storage)?;
    }
    for (model, fields) in &workspace.indexes {
        transaction
            .execute("INSERT INTO auth_indexes VALUES(?1,?2)", params![model, fields])
            .map_err(storage)?;
    }
    Ok(())
}
fn revision(transaction: &Transaction<'_>) -> Result<u64> {
    Ok(meta(transaction, "revision")?.unwrap_or(0))
}
fn advance(transaction: &Transaction<'_>) -> Result<()> {
    set_meta(
        transaction,
        "revision",
        &revision(transaction)?.checked_add(1).ok_or_else(|| storage("auth revision exhausted"))?,
    )
}
fn mutates(action: &Action, outcome: &Outcome) -> bool {
    !matches!(action, Action::Find { .. })
        && matches!(outcome, Outcome::Applied | Outcome::Record { .. })
}

pub(super) fn execute_dispatched(
    transaction: &Transaction<'_>,
    action: &Action,
    now: u64,
) -> Result<Outcome> {
    match action {
        Action::BeginTransaction { transaction_id } => {
            if transaction_id.is_empty() || transaction_id.len() > 128 {
                return Ok(invalid("invalid transaction identity"));
            }
            // Garbage collection runs only as a deterministic committed command.
            let expired = {
                let mut statement = transaction
                    .prepare("SELECT id,workspace FROM auth_transactions")
                    .map_err(storage)?;
                let rows = statement
                    .query_map([], |row| Ok((row.get::<_, String>(0)?, row.get::<_, Vec<u8>>(1)?)))
                    .map_err(storage)?;
                let mut expired = Vec::new();
                for row in rows {
                    let (id, bytes) = row.map_err(storage)?;
                    if decode::<Workspace>(&bytes)?.expires <= now {
                        expired.push(id);
                    }
                }
                expired
            };
            for id in expired {
                transaction
                    .execute("DELETE FROM auth_transactions WHERE id=?1", [id])
                    .map_err(storage)?;
            }
            if load(transaction, transaction_id)?.is_some() {
                return Ok(Outcome::TransactionConflict);
            }
            let count: u64 = transaction
                .query_row("SELECT COUNT(*) FROM auth_transactions", [], |row| row.get(0))
                .map_err(storage)?;
            if count >= MAX_TRANSACTIONS {
                return Ok(invalid("concurrent transaction bound reached"));
            }
            let expires = now
                .checked_add(MAX_LIFETIME_MS)
                .ok_or_else(|| storage("auth transaction time exhausted"))?;
            let Some(workspace) = capture(transaction, revision(transaction)?, expires)? else {
                return Ok(invalid("transaction snapshot exceeds record or byte bound"));
            };
            save(transaction, transaction_id, &workspace)?;
            Ok(Outcome::Transaction {
                transaction_id: transaction_id.clone(),
                expires_at_ms: expires,
            })
        }
        Action::TransactionOperation { transaction_id, action } => {
            let Some(mut workspace) = load(transaction, transaction_id)? else {
                return Ok(Outcome::TransactionConflict);
            };
            if workspace.failed
                || workspace.expires <= now
                || workspace.operations >= MAX_OPERATIONS
            {
                return Ok(Outcome::TransactionConflict);
            }
            transaction.execute_batch("SAVEPOINT auth_workspace;").map_err(storage)?;
            install(transaction, &workspace)?;
            let outcome = execute_action(transaction, action)?;
            let next = capture(transaction, workspace.revision, workspace.expires)?;
            transaction
                .execute_batch("ROLLBACK TO auth_workspace; RELEASE auth_workspace;")
                .map_err(storage)?;
            let operations = workspace.operations + 1;
            if let Some(next) = next {
                workspace = next;
            } else {
                workspace.failed = true;
            }
            workspace.operations = operations;
            // Catching a failed inner mutation in JavaScript cannot make a partial commit.
            if matches!(
                outcome,
                Outcome::Invalid { .. }
                    | Outcome::UniqueConflict
                    | Outcome::IdempotencyConflict
                    | Outcome::TransactionConflict
            ) {
                workspace.failed = true;
            }
            save(transaction, transaction_id, &workspace)?;
            if workspace.failed
                && !matches!(outcome, Outcome::Invalid { .. } | Outcome::UniqueConflict)
            {
                return Ok(Outcome::TransactionConflict);
            }
            Ok(outcome)
        }
        Action::CommitTransaction { transaction_id } => {
            let Some(workspace) = load(transaction, transaction_id)? else {
                return Ok(Outcome::TransactionConflict);
            };
            transaction
                .execute("DELETE FROM auth_transactions WHERE id=?1", [transaction_id])
                .map_err(storage)?;
            if workspace.failed
                || workspace.expires <= now
                || workspace.revision != revision(transaction)?
            {
                return Ok(Outcome::TransactionConflict);
            }
            install(transaction, &workspace)?;
            advance(transaction)?;
            Ok(Outcome::Applied)
        }
        Action::RollbackTransaction { transaction_id } => {
            transaction
                .execute("DELETE FROM auth_transactions WHERE id=?1", [transaction_id])
                .map_err(storage)?;
            Ok(Outcome::Applied)
        }
        _ => {
            let outcome = execute_action(transaction, action)?;
            if mutates(action, &outcome) {
                advance(transaction)?;
            }
            Ok(outcome)
        }
    }
}
