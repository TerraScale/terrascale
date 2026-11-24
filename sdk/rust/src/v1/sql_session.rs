//! SQL over the WebSocket
//! `GET /v1/projects/{project}/databases/{database}/sql/ws`, subprotocol
//! `terrascale.sql.v2`: interactive fast or strong transactions on one
//! session, with causal bookmarks on every answer.

use std::collections::HashMap;
use std::fmt;
use std::future::Future;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, Weak};
use std::time::Duration;

use futures_util::stream::{SplitSink, SplitStream};
use futures_util::{SinkExt, StreamExt};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use tokio::sync::oneshot;
use tokio::time::{interval_at, timeout, Instant, MissedTickBehavior};
use tokio_tungstenite::tungstenite::Message;

use super::client::WebSocket;
use super::database::Database;
use super::errors::{Error, Result, SqlError};
use super::ids::check_database_selector;
use super::sql_exec::{
    results_match, sql_error_codes, validate_batch, with_named, SqlRecord, SqlStatement,
    SqlStatementResult, SqlValue,
};
use crate::consistency::{ConsistencyMode, BOOKMARK_HEADER, CONSISTENCY_HEADER};

/// The SQL WebSocket subprotocol: frames carry causal bookmarks and
/// `fast` / `strong` consistency.
pub const SQL_SUBPROTOCOL: &str = "terrascale.sql.v2";
/// Maximum bytes of one client frame.
pub const MAX_SQL_FRAME_BYTES: usize = 1 << 20;

const FRAME_DEADLINE: Duration = Duration::from_secs(30);
const PING_INTERVAL: Duration = Duration::from_secs(10);

type SocketSink = tokio::sync::Mutex<SplitSink<WebSocket, Message>>;

#[derive(Serialize)]
struct Frame<'a> {
    id: u64,
    #[serde(rename = "type")]
    kind: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    statements: Option<&'a [SqlStatement]>,
    #[serde(skip_serializing_if = "Option::is_none")]
    consistency: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    bookmark: Option<String>,
}

#[derive(Deserialize)]
struct Answer {
    #[serde(default)]
    id: Option<u64>,
    #[serde(default)]
    results: Vec<SqlStatementResult>,
    #[serde(default)]
    error: Option<SqlError>,
    transaction: bool,
    #[serde(default)]
    bookmark: Option<String>,
}

struct Waiter {
    answer: oneshot::Sender<Result<Answer>>,
    committing: bool,
    control: bool,
}

/// What the reader task shares with callers.
struct Wire {
    /// `None` once the socket is gone.
    pending: Mutex<Option<HashMap<u64, Waiter>>>,
    transaction: AtomicBool,
}

impl Wire {
    fn alive(&self) -> bool {
        lock(&self.pending).is_some()
    }
}

/// One open socket.
struct Link {
    sink: Arc<SocketSink>,
    wire: Arc<Wire>,
    reader: tokio::task::AbortHandle,
}

impl Drop for Link {
    fn drop(&mut self) {
        self.reader.abort();
    }
}

#[derive(Default)]
struct State {
    link: Option<Arc<Link>>,
    /// The socket dropped inside a transaction; only `rollback` clears it.
    lost: bool,
    closed: bool,
    uncertain: bool,
}

struct Shared {
    db: Database,
    next_id: AtomicU64,
    state: Mutex<State>,
    opening: tokio::sync::Mutex<()>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

enum Ready {
    Link(Arc<Link>),
    /// `rollback` after a lost transaction: cleared locally, nothing sent.
    Cleared,
    Connect,
}

/// A SQL WebSocket session on one database, from [`Database::connect`].
///
/// Outside a transaction every call is its own transaction; [`Self::begin`]
/// opens one that lasts at most 5 seconds. Calls may overlap; answers are
/// matched by frame id. Nothing is resent. Clones share the session.
///
/// A lost committing answer returns [`Error::CommitUncertain`] and closes
/// the session; every later call returns that error. Without a committing
/// request, a lost open transaction returns `transaction_rolled_back`
/// (40000) until [`Self::rollback`] clears it.
///
/// ```no_run
/// # async fn demo(client: terrascale::v1::Client) -> terrascale::v1::Result<()> {
/// let session = client.database("main").connect().await?;
/// session
///     .transaction(|s| async move {
///         s.execute("INSERT INTO ledger VALUES (?, ?)", &[2.into(), 25.into()]).await?;
///         s.execute("UPDATE totals SET n = n + 1", &[]).await
///     })
///     .await?;
/// session.close().await;
/// # Ok(()) }
/// ```
///
/// Every frame sends the database's causal bookmark and every answer's
/// bookmark advances it, shared with REST SQL on the same [`Database`].
/// [`Self::begin_strong`] opens a strong 2-of-3 quorum transaction.
#[derive(Clone)]
pub struct SqlSession {
    shared: Arc<Shared>,
    consistency: Option<ConsistencyMode>,
    bookmark: Option<String>,
}

impl fmt::Debug for SqlSession {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SqlSession")
            .field("database", &self.shared.db.name())
            .field("in_transaction", &self.in_transaction())
            .field("consistency", &self.consistency)
            .finish_non_exhaustive()
    }
}

impl Database {
    /// Opens a SQL WebSocket session. A refused upgrade returns the same
    /// errors as REST (401, 403, 404, 421 and others).
    pub async fn connect(&self) -> Result<SqlSession> {
        check_database_selector(self.name())?;
        let session = SqlSession {
            shared: Arc::new(Shared {
                db: self.clone(),
                next_id: AtomicU64::new(0),
                state: Mutex::default(),
                opening: tokio::sync::Mutex::new(()),
            }),
            consistency: self.consistency(),
            bookmark: None,
        };
        session.link(false).await?;
        Ok(session)
    }
}

impl SqlSession {
    /// A handle on the same session whose autocommit calls and
    /// [`Self::begin`] request `mode`.
    #[must_use]
    pub fn with_consistency(&self, mode: ConsistencyMode) -> Self {
        Self {
            consistency: Some(mode),
            ..self.clone()
        }
    }

    /// A handle on the same session whose frames also carry `bookmark`,
    /// merged with the database's tracked one, so the server waits for it.
    /// An invalid bookmark fails each call locally.
    #[must_use]
    pub fn with_bookmark(&self, bookmark: impl Into<String>) -> Self {
        Self {
            bookmark: Some(bookmark.into()),
            ..self.clone()
        }
    }

    /// The consistency mode this handle requests, when one is set.
    #[must_use]
    pub fn consistency(&self) -> Option<ConsistencyMode> {
        self.consistency
    }

    /// The newest causal bookmark observed on this database.
    #[must_use]
    pub fn bookmark(&self) -> Option<String> {
        self.shared.db.bookmark()
    }

    /// Runs one statement with positional arguments.
    pub async fn execute(&self, sql: &str, args: &[SqlValue]) -> Result<SqlStatementResult> {
        self.execute_statement(SqlStatement::new(sql).args(args.iter().cloned()))
            .await
    }

    /// Runs one statement with named arguments.
    pub async fn execute_named<K: Into<String>, V: Into<SqlValue>>(
        &self,
        sql: &str,
        named: impl IntoIterator<Item = (K, V)>,
    ) -> Result<SqlStatementResult> {
        self.execute_statement(with_named(sql, named)).await
    }

    /// Runs one statement.
    pub async fn execute_statement(
        &self,
        statement: impl Into<SqlStatement>,
    ) -> Result<SqlStatementResult> {
        let mut results = self.batch(vec![statement.into()]).await?;
        Ok(results.remove(0))
    }

    /// Runs one statement and returns its rows.
    pub async fn query(&self, sql: &str, args: &[SqlValue]) -> Result<Vec<SqlRecord>> {
        Ok(self.execute(sql, args).await?.records())
    }

    /// Runs one statement with named arguments and returns its rows.
    pub async fn query_named<K: Into<String>, V: Into<SqlValue>>(
        &self,
        sql: &str,
        named: impl IntoIterator<Item = (K, V)>,
    ) -> Result<Vec<SqlRecord>> {
        Ok(self.execute_named(sql, named).await?.records())
    }

    /// Runs one statement and maps its rows to `T`; see
    /// [`SqlStatementResult::decode_rows`].
    pub async fn query_as<T: DeserializeOwned>(
        &self,
        sql: &str,
        args: &[SqlValue],
    ) -> Result<Vec<T>> {
        self.execute(sql, args).await?.decode_rows()
    }

    /// Runs one statement with named arguments and maps its rows to `T`.
    pub async fn query_as_named<T: DeserializeOwned, K: Into<String>, V: Into<SqlValue>>(
        &self,
        sql: &str,
        named: impl IntoIterator<Item = (K, V)>,
    ) -> Result<Vec<T>> {
        self.execute_named(sql, named).await?.decode_rows()
    }

    /// Runs 1..=256 statements: all or nothing outside a transaction, or
    /// inside the open one. After a failed statement in a transaction every
    /// call returns `transaction_aborted` until `rollback` or `commit`.
    pub async fn batch(&self, statements: Vec<SqlStatement>) -> Result<Vec<SqlStatementResult>> {
        validate_batch(&statements)?;
        let answer = self.send("execute", Some(&statements)).await?;
        if !results_match(&answer.results, statements.len()) {
            return Err(Error::Decode(
                "SQL results do not match the statements".into(),
            ));
        }
        Ok(answer.results)
    }

    /// Opens a transaction of at most 5 seconds, in this handle's
    /// consistency mode or the session's default.
    pub async fn begin(&self) -> Result<()> {
        self.send("begin", None).await.map(drop)
    }

    /// Opens a transaction of at most 5 seconds in `mode`.
    pub async fn begin_with(&self, mode: ConsistencyMode) -> Result<()> {
        self.with_consistency(mode).begin().await
    }

    /// Opens a strong 2-of-3 quorum transaction of at most 5 seconds, the
    /// WebSocket form of `BEGIN STRONG`. A concurrent conflicting strong
    /// transaction makes [`Self::commit`] fail with `serialization_conflict`.
    pub async fn begin_strong(&self) -> Result<()> {
        self.begin_with(ConsistencyMode::Strong).await
    }

    /// [`Self::transaction`] as a strong 2-of-3 quorum transaction.
    pub async fn transaction_strong<T, F, Fut>(&self, work: F) -> Result<T>
    where
        F: FnOnce(SqlSession) -> Fut,
        Fut: Future<Output = Result<T>>,
    {
        self.with_consistency(ConsistencyMode::Strong)
            .transaction(work)
            .await
    }

    /// Commits the open transaction; after a failed statement it rolls back
    /// and returns `transaction_rolled_back`.
    pub async fn commit(&self) -> Result<()> {
        self.send("commit", None).await.map(drop)
    }

    /// Rolls back the open transaction.
    pub async fn rollback(&self) -> Result<()> {
        self.send("rollback", None).await.map(drop)
    }

    /// Begins, runs `work` and commits. When `work` fails, rolls back if a
    /// transaction is still open and returns the error.
    pub async fn transaction<T, F, Fut>(&self, work: F) -> Result<T>
    where
        F: FnOnce(SqlSession) -> Fut,
        Fut: Future<Output = Result<T>>,
    {
        self.begin().await?;
        match work(self.clone()).await {
            Ok(value) => {
                self.commit().await?;
                Ok(value)
            }
            Err(error) => {
                if self.in_transaction() {
                    let _ = self.rollback().await;
                }
                Err(error)
            }
        }
    }

    /// Whether a transaction is open, as of the last answer, or was lost
    /// with a dropped socket and awaits [`Self::rollback`].
    #[must_use]
    pub fn in_transaction(&self) -> bool {
        let state = lock(&self.shared.state);
        state.lost
            || state
                .link
                .as_ref()
                .is_some_and(|link| link.wire.transaction.load(Ordering::Acquire))
    }

    /// Closes the socket normally; the server rolls back an open transaction.
    /// Calls in flight fail and the session stays closed.
    pub async fn close(&self) {
        let link = {
            let mut state = lock(&self.shared.state);
            state.closed = true;
            state.link.clone()
        };
        if let Some(link) = link {
            fail(&link.wire, &self.shared, "the session closed");
            let mut sink = link.sink.lock().await;
            let _ = tokio::time::timeout(Duration::from_secs(5), sink.close()).await;
            drop(lock(&link.wire.pending).take());
        }
    }

    async fn send(&self, kind: &str, statements: Option<&[SqlStatement]>) -> Result<Answer> {
        if let Some(bookmark) = &self.bookmark {
            super::database::check_bookmark(bookmark)?;
        }
        let id = self.shared.next_id.fetch_add(1, Ordering::Relaxed) + 1;
        let consistency = self
            .consistency
            .filter(|_| matches!(kind, "execute" | "begin"))
            .map(ConsistencyMode::as_str);
        let bookmark = self
            .shared
            .db
            .bookmark_tracker()
            .with(self.bookmark.as_deref());
        let frame = serde_json::to_string(&Frame {
            id,
            kind,
            statements,
            consistency,
            bookmark,
        })
        .map_err(|e| Error::InvalidInput(format!("encode SQL frame: {e}")))?;
        if frame.len() > MAX_SQL_FRAME_BYTES {
            return Err(Error::InvalidInput(format!(
                "a SQL frame is at most {MAX_SQL_FRAME_BYTES} bytes"
            )));
        }
        loop {
            let Some(link) = self.link(kind == "rollback").await? else {
                return Ok(Answer {
                    id: Some(id),
                    results: Vec::new(),
                    error: None,
                    transaction: false,
                    bookmark: None,
                });
            };
            let mut sink = link.sink.lock().await;
            let (waiter, answer) = oneshot::channel();
            {
                let state = lock(&self.shared.state);
                if state.uncertain {
                    return Err(Error::CommitUncertain);
                }
                let mut pending = lock(&link.wire.pending);
                let Some(pending) = pending.as_mut() else {
                    continue;
                };
                let committing = kind == "commit"
                    || kind == "execute"
                        && (!link.wire.transaction.load(Ordering::Acquire)
                            || pending.values().any(|waiter| waiter.control));
                pending.insert(
                    id,
                    Waiter {
                        answer: waiter,
                        committing,
                        control: matches!(kind, "begin" | "commit" | "rollback"),
                    },
                );
            }
            let flight = Flight {
                shared: self.shared.clone(),
                link: link.clone(),
                id,
            };
            let failure = match timeout(
                FRAME_DEADLINE,
                sink.send(Message::Text(frame.as_str().into())),
            )
            .await
            {
                Ok(Ok(())) => None,
                Ok(Err(error)) => Some(error.to_string()),
                Err(_) => Some("sending the SQL frame timed out".into()),
            };
            if let Some(reason) = failure {
                fail(&link.wire, &self.shared, &reason);
                return if lock(&self.shared.state).uncertain {
                    Err(Error::CommitUncertain)
                } else {
                    Err(Error::Transport(format!("SQL session: {reason}")))
                };
            }
            drop(sink);
            let answer = answer
                .await
                .unwrap_or_else(|_| Err(Error::Transport("SQL session closed".into())))?;
            drop(flight);
            return match answer.error {
                Some(error) => Err(Error::Sql {
                    status: None,
                    error,
                }),
                None => Ok(answer),
            };
        }
    }

    fn ready(&self, rollback: bool) -> Result<Ready> {
        let mut state = lock(&self.shared.state);
        if state.uncertain {
            return Err(Error::CommitUncertain);
        }
        if state.closed {
            return Err(Error::InvalidInput("the SQL session is closed".into()));
        }
        if state.lost {
            if rollback {
                state.lost = false;
                return Ok(Ready::Cleared);
            }
            return Err(Error::Sql {
                status: None,
                error: SqlError {
                    code: sql_error_codes::TRANSACTION_ROLLED_BACK.into(),
                    message: "the connection dropped and the server rolled back the open transaction; call rollback".into(),
                    statement: 0,
                    sqlstate: Some("40000".into()),
                },
            });
        }
        Ok(match &state.link {
            Some(link) if link.wire.alive() => Ready::Link(link.clone()),
            _ => Ready::Connect,
        })
    }

    /// The open socket, opening one if needed; `None` when a lost
    /// transaction was cleared by `rollback`.
    async fn link(&self, rollback: bool) -> Result<Option<Arc<Link>>> {
        match self.ready(rollback)? {
            Ready::Link(link) => return Ok(Some(link)),
            Ready::Cleared => return Ok(None),
            Ready::Connect => {}
        }
        let _opening = self.shared.opening.lock().await;
        match self.ready(rollback)? {
            Ready::Link(link) => return Ok(Some(link)),
            Ready::Cleared => return Ok(None),
            Ready::Connect => {}
        }
        let db = &self.shared.db;
        let mut headers = Vec::new();
        if let Some(bookmark) = db.bookmark_tracker().with(self.bookmark.as_deref()) {
            headers.push((BOOKMARK_HEADER, bookmark));
        }
        if let Some(mode) = db.consistency() {
            headers.push((CONSISTENCY_HEADER, mode.as_str().to_owned()));
        }
        let socket = db
            .client()
            .open_socket(
                vec!["v1", "databases", db.name(), "sql", "ws"],
                &[],
                &headers,
                SQL_SUBPROTOCOL,
                db.client().max_response(),
            )
            .await?;
        let (sink, stream) = socket.split();
        let sink = Arc::new(tokio::sync::Mutex::new(sink));
        let wire = Arc::new(Wire {
            pending: Mutex::new(Some(HashMap::new())),
            transaction: AtomicBool::new(false),
        });
        let reader = tokio::spawn(read(
            stream,
            sink.clone(),
            wire.clone(),
            Arc::downgrade(&self.shared),
        ));
        let link = Arc::new(Link {
            sink,
            wire,
            reader: reader.abort_handle(),
        });
        let mut state = lock(&self.shared.state);
        if state.uncertain {
            return Err(Error::CommitUncertain);
        }
        if state.closed {
            return Err(Error::InvalidInput("the SQL session is closed".into()));
        }
        state.link = Some(link.clone());
        Ok(Some(link))
    }
}

/// Delivers answers by id until the socket ends, then fails every call in
/// flight and records a transaction lost with it.
async fn read(
    mut stream: SplitStream<WebSocket>,
    sink: Arc<SocketSink>,
    wire: Arc<Wire>,
    shared: Weak<Shared>,
) {
    let keepalive = keepalive(&sink);
    tokio::pin!(keepalive);
    let reason = loop {
        let message = tokio::select! {
            message = timeout(FRAME_DEADLINE, stream.next()) => match message {
                Ok(message) => message,
                Err(_) => break "reading a SQL WebSocket frame timed out".into(),
            },
            reason = &mut keepalive => break reason,
        };
        match message {
            Some(Ok(Message::Text(text))) => match serde_json::from_str::<Answer>(&text) {
                Ok(answer) => {
                    let uncertain = answer.error.as_ref().is_some_and(SqlError::is_uncertain);
                    let waiter = if let Some(shared) = shared.upgrade() {
                        if let Some(bookmark) = answer.bookmark.as_deref() {
                            shared.db.bookmark_tracker().observe(bookmark);
                        }
                        let mut state = lock(&shared.state);
                        state.uncertain |= uncertain;
                        wire.transaction
                            .store(answer.transaction, Ordering::Release);
                        answer
                            .id
                            .and_then(|id| lock(&wire.pending).as_mut()?.remove(&id))
                    } else {
                        None
                    };
                    if uncertain {
                        if let Some(waiter) = waiter {
                            drop(waiter.answer.send(Err(Error::CommitUncertain)));
                        }
                        break "the server reported an uncertain write".into();
                    }
                    match (waiter, &answer.error) {
                        (Some(waiter), _) => drop(waiter.answer.send(Ok(answer))),
                        (None, Some(error)) => {
                            break format!("TerraBase refused the session: {error}")
                        }
                        (None, None) => {}
                    }
                }
                Err(error) => break format!("invalid SQL frame: {error}"),
            },
            Some(Ok(Message::Close(frame))) => {
                break frame.map_or_else(
                    || "TerraBase closed the socket".into(),
                    |frame| {
                        format!(
                            "TerraBase closed the socket ({}: {})",
                            u16::from(frame.code),
                            frame.reason
                        )
                    },
                )
            }
            Some(Ok(Message::Binary(_))) => break "TerraBase sent a binary frame".into(),
            Some(Ok(_)) => {}
            Some(Err(error)) => break error.to_string(),
            None => break "the socket ended".into(),
        }
    };
    if let Some(shared) = shared.upgrade() {
        fail(&wire, &shared, &reason);
    }
}

async fn keepalive(sink: &SocketSink) -> String {
    let mut ticks = interval_at(Instant::now() + PING_INTERVAL, PING_INTERVAL);
    ticks.set_missed_tick_behavior(MissedTickBehavior::Delay);
    loop {
        ticks.tick().await;
        match timeout(FRAME_DEADLINE, async {
            sink.lock()
                .await
                .send(Message::Ping(Default::default()))
                .await
        })
        .await
        {
            Ok(Ok(())) => {}
            Ok(Err(error)) => return error.to_string(),
            Err(_) => return "sending a SQL WebSocket ping timed out".into(),
        }
    }
}

fn fail(wire: &Wire, shared: &Shared, reason: &str) {
    let mut state = lock(&shared.state);
    let pending = lock(&wire.pending).take();
    let committing = pending
        .as_ref()
        .is_some_and(|pending| pending.values().any(|waiter| waiter.committing));
    if state
        .link
        .as_ref()
        .is_some_and(|link| std::ptr::eq(Arc::as_ptr(&link.wire), wire))
    {
        state.uncertain |= committing;
        state.lost = !state.uncertain && wire.transaction.load(Ordering::Acquire);
        state.link = None;
    }
    for (_, waiter) in pending.into_iter().flatten() {
        let error = if state.uncertain {
            Error::CommitUncertain
        } else if state.lost {
            Error::Sql {
                status: None,
                error: SqlError {
                    code: sql_error_codes::TRANSACTION_ROLLED_BACK.into(),
                    message: format!(
                        "the transaction rolled back when the SQL session closed: {reason}"
                    ),
                    statement: 0,
                    sqlstate: Some("40000".into()),
                },
            }
        } else {
            Error::Transport(format!("SQL session closed: {reason}"))
        };
        drop(waiter.answer.send(Err(error)));
    }
}

struct Flight {
    shared: Arc<Shared>,
    link: Arc<Link>,
    id: u64,
}

impl Drop for Flight {
    fn drop(&mut self) {
        let pending = lock(&self.link.wire.pending)
            .as_ref()
            .is_some_and(|pending| pending.contains_key(&self.id));
        if pending {
            fail(
                &self.link.wire,
                &self.shared,
                "the caller stopped waiting for its answer",
            );
            self.link.reader.abort();
        }
    }
}
