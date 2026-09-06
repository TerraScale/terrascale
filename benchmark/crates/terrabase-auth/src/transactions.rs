use super::*;

const MAX_BYTES: usize = 4 * 1024 * 1024;
const MAX_TRANSACTIONS: u64 = 32;
const MAX_OPERATIONS: u32 = 100;
const MAX_LIFETIME_MS: u64 = 30_000;

// An optimistic read set is represented conservatively by the global revision.
// No committed user records are copied. Replay touches only staged writes.
#[derive(Serialize, Deserialize)]
struct Workspace {
    commands: Vec<(Action, Outcome)>,
    revision: u64,
    expires: u64,
    operations: u32,
    failed: bool,
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

// Rejections before dispatch must also prevent a partial transaction commit.
pub(super) fn poison_operation(transaction: &Transaction<'_>, action: &Action) -> Result<()> {
    if let Action::TransactionOperation { transaction_id, .. } = action {
        if let Some(mut workspace) = load(transaction, transaction_id)? {
            workspace.failed = true;
            workspace.commands.clear();
            save(transaction, transaction_id, &workspace)?;
        }
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
    if matches!(action, Action::Find { .. } | Action::Count { .. }) {
        return false;
    }
    matches!(outcome, Outcome::Applied | Outcome::Record { .. })
        || matches!(outcome, Outcome::Count { count } if *count > 0)
}
fn replay(transaction: &Transaction<'_>, workspace: &Workspace) -> Result<()> {
    for (command, expected) in &workspace.commands {
        if &execute_action(transaction, command)? != expected {
            return Err(storage("auth transaction replay diverged at unchanged revision"));
        }
    }
    Ok(())
}

pub(super) fn execute_dispatched(
    transaction: &Transaction<'_>,
    action: &Action,
    now: u64,
) -> Result<Outcome> {
    match action {
        Action::BeginTransaction { transaction_id } => {
            if transaction_id.is_empty() || transaction_id.len() > 100 {
                return Ok(invalid("invalid transaction identity"));
            }
            // GC runs only as a committed, deterministic operation.
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
                    let workspace: Workspace = decode(&bytes)?;
                    if workspace.expires <= now {
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
            let count: i64 = transaction
                .query_row("SELECT COUNT(*) FROM auth_transactions", [], |row| row.get(0))
                .map_err(storage)?;
            if u64::try_from(count).map_err(storage)? >= MAX_TRANSACTIONS {
                return Ok(invalid("concurrent transaction bound reached"));
            }
            let expires = now
                .checked_add(MAX_LIFETIME_MS)
                .ok_or_else(|| storage("auth transaction time exhausted"))?;
            let generation = meta::<u64>(transaction, "transaction_generation")?
                .unwrap_or(0)
                .checked_add(1)
                .ok_or_else(|| storage("auth transaction generation exhausted"))?;
            let workspace = Workspace {
                commands: Vec::new(),
                revision: revision(transaction)?,
                expires,
                operations: 0,
                failed: false,
            };
            set_meta(transaction, "transaction_generation", &generation)?;
            let canonical = format!("{}#{}", transaction_id, generation);
            save(transaction, &canonical, &workspace)?;
            Ok(Outcome::Transaction { transaction_id: canonical, expires_at_ms: expires })
        }
        Action::TransactionOperation { transaction_id, action } => {
            let Some(mut workspace) = load(transaction, transaction_id)? else {
                return Ok(Outcome::TransactionConflict);
            };
            if workspace.failed
                || workspace.expires <= now
                || workspace.operations >= MAX_OPERATIONS
                || workspace.revision != revision(transaction)?
            {
                workspace.failed = true;
                workspace.commands.clear();
                save(transaction, transaction_id, &workspace)?;
                return Ok(Outcome::TransactionConflict);
            }
            transaction.execute_batch("SAVEPOINT auth_workspace;").map_err(storage)?;
            replay(transaction, &workspace)?;
            let outcome = execute_action(transaction, action)?;
            transaction
                .execute_batch("ROLLBACK TO auth_workspace; RELEASE auth_workspace;")
                .map_err(storage)?;
            workspace.operations += 1;
            if mutates(action, &outcome) {
                workspace.commands.push((*action.clone(), outcome.clone()));
            }
            if matches!(
                outcome,
                Outcome::Invalid { .. }
                    | Outcome::UniqueConflict
                    | Outcome::IdempotencyConflict
                    | Outcome::TransactionConflict
            ) {
                workspace.failed = true;
                workspace.commands.clear();
            }
            if encode(&workspace)?.len() > MAX_BYTES {
                workspace.commands.clear();
                workspace.failed = true;
                save(transaction, transaction_id, &workspace)?;
                return Ok(invalid("transaction staged byte bound exceeded"));
            }
            save(transaction, transaction_id, &workspace)?;
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
            replay(transaction, &workspace)?;
            if !workspace.commands.is_empty() {
                advance(transaction)?;
            }
            Ok(Outcome::Applied)
        }
        Action::RollbackTransaction { transaction_id } => {
            let Some(_workspace) = load(transaction, transaction_id)? else {
                return Ok(Outcome::TransactionConflict);
            };
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
