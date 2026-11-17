use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;
use std::time::Duration;

use futures_util::{stream, Stream};
use reqwest::{Method, StatusCode};
use serde::de::{MapAccess, Visitor};
use serde::{Deserialize, Deserializer};
use tokio::time::Instant;

use super::client::{backoff, Req};
use super::table_shape_hints::{Hint, Hints};
use super::{Database, Error, Result, SqlRecord, SqlValue};

/// Synchronization state of an immutable shape view.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ShapeStatus {
    Loading,
    Live,
    Reconnecting,
}

/// Rows at an applied shape boundary, including the region's provisional writes.
#[derive(Debug, Clone)]
pub struct ShapeView {
    pub rows: Arc<[SqlRecord]>,
    pub status: ShapeStatus,
    pub offset: String,
    pub provisional: Arc<BTreeSet<String>>,
}

/// A filtered SQL table subscription. Construct with [Database::shape].
#[derive(Debug, Clone)]
pub struct Shape {
    db: Database,
    table: String,
    filter: Option<String>,
    columns: Option<Vec<String>>,
}

impl Database {
    /// Builds a shape without performing I/O. Requires project sql:read access.
    pub fn shape(&self, table: impl Into<String>) -> Shape {
        Shape {
            db: self.clone(),
            table: table.into(),
            filter: None,
            columns: None,
        }
    }
}

impl Shape {
    /// Includes rows matching a server-validated SQL condition.
    pub fn filter(mut self, condition: impl Into<String>) -> Self {
        self.filter = Some(condition.into());
        self
    }

    /// Projects distinct stored column names.
    pub fn columns(mut self, columns: &[&str]) -> Self {
        self.columns = Some(columns.iter().map(|name| (*name).into()).collect());
        self
    }

    /// Streams each snapshot page and subsequent live batch. Dropping cancels in-flight I/O.
    pub async fn subscribe(self) -> Result<impl Stream<Item = Result<ShapeView>> + Send> {
        super::ids::check_database_selector(self.db.name())?;
        if !identifier(&self.table)
            || self.table.len() > 63
            || self
                .filter
                .as_ref()
                .is_some_and(|value| value.trim().is_empty() || value.len() > 4096)
            || self.columns.as_ref().is_some_and(|names| {
                names.is_empty()
                    || names.iter().any(|name| !identifier(name))
                    || names.iter().collect::<BTreeSet<_>>().len() != names.len()
                    || names.join(",").len() > 4096
            })
        {
            return Err(Error::InvalidInput(
                "invalid shape table, filter or columns".into(),
            ));
        }
        Ok(stream::unfold(Some(State::new(self)), |state| async {
            let mut state = state?;
            match state.next().await {
                Ok(view) => Some((Ok(view), Some(state))),
                Err(error) => Some((Err(error), None)),
            }
        }))
    }
}

fn identifier(name: &str) -> bool {
    !name.is_empty()
        && name.bytes().enumerate().all(|(index, byte)| {
            byte == b'_' || byte.is_ascii_alphabetic() || index > 0 && byte.is_ascii_digit()
        })
}

#[derive(Deserialize)]
struct Page {
    handle: String,
    offset: String,
    next_offset: String,
    up_to_date: bool,
    changes: Vec<Change>,
    provisional: Option<Vec<Pending>>,
    local: Option<String>,
}

#[derive(Deserialize)]
struct Change {
    ops: Vec<Op>,
    #[serde(default)]
    confirmed: Vec<String>,
    #[serde(default)]
    revert: Vec<String>,
}

#[derive(Deserialize)]
struct Pending {
    id: String,
    ops: Vec<Op>,
}

#[derive(Deserialize)]
struct Op {
    op: String,
    key: String,
    row: Option<OrderedRow>,
    #[serde(rename = "move")]
    movement: Option<String>,
}

#[derive(Clone)]
struct OrderedRow(Vec<(String, SqlValue)>);

impl<'de> Deserialize<'de> for OrderedRow {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> std::result::Result<Self, D::Error> {
        struct RowVisitor;
        impl<'de> Visitor<'de> for RowVisitor {
            type Value = OrderedRow;
            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                formatter.write_str("an ordered SQL row object")
            }
            fn visit_map<M: MapAccess<'de>>(
                self,
                mut map: M,
            ) -> std::result::Result<Self::Value, M::Error> {
                let mut row = Vec::new();
                while let Some(entry) = map.next_entry()? {
                    row.push(entry);
                }
                Ok(OrderedRow(row))
            }
        }
        deserializer.deserialize_map(RowVisitor)
    }
}

type Rows = BTreeMap<String, OrderedRow>;

struct State {
    shape: Shape,
    rows: Rows,
    pending: Vec<Pending>,
    handle: String,
    offset: String,
    cursor: Option<String>,
    local: String,
    status: ShapeStatus,
    attempt: u32,
    delay: Option<Duration>,
    hints_advertised: bool,
    hints: Option<Hints>,
    hint_retry_at: Option<Instant>,
    hint_attempt: u32,
}

impl State {
    fn new(shape: Shape) -> Self {
        Self {
            shape,
            rows: BTreeMap::new(),
            pending: Vec::new(),
            handle: String::new(),
            offset: "-1".into(),
            cursor: None,
            local: "0".into(),
            status: ShapeStatus::Loading,
            attempt: 0,
            delay: None,
            hints_advertised: false,
            hints: None,
            hint_retry_at: None,
            hint_attempt: 0,
        }
    }

    fn view(&self) -> Result<ShapeView> {
        let mut rows = self.rows.clone();
        let mut provisional = BTreeSet::new();
        for transaction in &self.pending {
            apply(&mut rows, &transaction.ops, &mut provisional)?;
        }
        Ok(ShapeView {
            rows: rows
                .into_values()
                .map(|row| SqlRecord::from_shape(row.0))
                .collect::<Vec<_>>()
                .into(),
            status: self.status,
            offset: self.offset.clone(),
            provisional: Arc::new(provisional),
        })
    }

    fn query(&self) -> Vec<(&'static str, String)> {
        let mut query = vec![("offset", self.offset.clone())];
        if !self.handle.is_empty() {
            query.push(("handle", self.handle.clone()));
        }
        if let Some(filter) = &self.shape.filter {
            query.push(("where", filter.clone()));
        }
        if let Some(columns) = &self.shape.columns {
            query.push(("columns", columns.join(",")));
        }
        query
    }

    fn reset(&mut self) {
        self.rows.clear();
        self.pending.clear();
        self.handle.clear();
        self.offset = "-1".into();
        self.cursor = None;
        self.local = "0".into();
        self.status = ShapeStatus::Loading;
        self.hints = None;
        self.hint_retry_at = None;
        self.attempt = 0;
        self.hint_attempt = 0;
    }

    async fn hint(&mut self) -> Result<Hint> {
        let mut hints = match self.hints.take() {
            Some(hints) => hints,
            None => {
                Hints::open(
                    self.shape.db.client(),
                    self.shape.db.name(),
                    &self.shape.table,
                    &self.query(),
                )
                .await?
            }
        };
        let hint = hints.wait(&self.handle, &self.offset, &self.local).await?;
        self.hints = Some(hints);
        Ok(hint)
    }

    async fn next(&mut self) -> Result<ShapeView> {
        loop {
            if let Some(delay) = self.delay.take() {
                tokio::time::sleep(delay).await;
            }
            let hint = if self.status == ShapeStatus::Live
                && self.hints_advertised
                && self.hint_retry_at.is_none_or(|at| Instant::now() >= at)
            {
                match self.hint().await {
                    Ok(hint) => {
                        self.hint_attempt = 0;
                        Some(hint)
                    }
                    Err(error) if error.code() == Some("must_refetch") => {
                        self.reset();
                        return self.view();
                    }
                    Err(error) if super::table_shape_hints::terminal(&error) => return Err(error),
                    Err(error) => {
                        self.hints = None;
                        self.hint_retry_at = Some(
                            Instant::now()
                                .checked_add(reconnect_delay(&error, self.hint_attempt))
                                .ok_or_else(|| Error::Decode("invalid hint Retry-After".into()))?,
                        );
                        self.hint_attempt = self.hint_attempt.saturating_add(1);
                        self.status = ShapeStatus::Reconnecting;
                        return self.view();
                    }
                }
            } else {
                None
            };
            let mut req = Req::new(
                Method::GET,
                vec![
                    "v1",
                    "databases",
                    self.shape.db.name(),
                    "tables",
                    &self.shape.table,
                    "shape",
                ],
            );
            req.query = self.query();
            let live = self.status == ShapeStatus::Live && !matches!(hint, Some(Hint::Data));
            if live {
                req.query.push(("live", "true".into()));
                req.query.push(("local", self.local.clone()));
                if let Some(cursor) = &self.cursor {
                    req.query.push(("cursor", cursor.clone()));
                }
            }
            let response = match self.shape.db.client().read_shape(req).await {
                Ok(response) => response,
                Err(error) if error.code() == Some("must_refetch") => {
                    self.reset();
                    return self.view();
                }
                Err(error) if transient(&error) => {
                    self.delay = Some(reconnect_delay(&error, self.attempt));
                    self.hints = None;
                    self.attempt = self.attempt.saturating_add(1);
                    self.status = ShapeStatus::Reconnecting;
                    return self.view();
                }
                Err(error) => return Err(error),
            };
            if response.status == StatusCode::TEMPORARY_REDIRECT {
                if !self.handle.is_empty() {
                    return Err(Error::Decode("unexpected shape redirect".into()));
                }
                let location = response.header("Location").unwrap_or_default();
                let base = self.shape.db.client().base_url();
                let target = base
                    .join(location)
                    .map_err(|_| Error::Decode("invalid shape location".into()))?;
                let query = target.query_pairs().collect::<BTreeMap<_, _>>();
                self.handle = query
                    .get("handle")
                    .map(|value| value.to_string())
                    .unwrap_or_default();
                self.offset = query
                    .get("offset")
                    .map(|value| value.to_string())
                    .unwrap_or_default();
                if !valid_handle(&self.handle) || !snapshot(&self.offset) {
                    return Err(Error::Decode("invalid shape bootstrap boundary".into()));
                }
                continue;
            }
            if let Some(protocol) = response.header("TB-Shape-Hints") {
                self.hints_advertised = protocol == super::table_shape_hints::PROTOCOL;
            }
            let page: Page = response.decode()?;
            if !valid_handle(&page.handle)
                || !self.handle.is_empty() && page.handle != self.handle
                || page.offset != self.offset
                || !(snapshot(&page.next_offset) || decimal(&page.next_offset))
                || page.up_to_date && !decimal(&page.next_offset)
            {
                return Err(Error::Decode("invalid shape page boundary".into()));
            }
            if live
                && page.next_offset == self.offset
                && page.local.as_ref().is_none_or(|local| local == &self.local)
            {
                self.delay = Some(Duration::from_millis(
                    1000 + u64::from(uuid::Uuid::new_v4().as_bytes()[0]),
                ));
            }
            let mut rows = self.rows.clone();
            for change in &page.changes {
                apply(&mut rows, &change.ops, &mut BTreeSet::new())?;
            }
            for change in &page.changes {
                self.pending.retain(|entry| {
                    !change.confirmed.contains(&entry.id) && !change.revert.contains(&entry.id)
                });
            }
            if let Some(pending) = page.provisional {
                let local = page
                    .local
                    .filter(|local| valid_local(local))
                    .ok_or_else(|| Error::Decode("invalid shape local boundary".into()))?;
                self.pending = pending;
                self.local = local;
            }
            self.rows = rows;
            self.handle = page.handle;
            self.offset = page.next_offset;
            self.cursor = response
                .header("TB-Shape-Cursor")
                .map(str::to_owned)
                .or(self.cursor.take());
            self.status = if page.up_to_date {
                ShapeStatus::Live
            } else {
                ShapeStatus::Loading
            };
            self.attempt = 0;
            return self.view();
        }
    }
}

fn reconnect_delay(error: &Error, attempt: u32) -> Duration {
    if let Error::Response {
        retry_after: Some(delay),
        ..
    } = error
    {
        return *delay;
    }
    backoff(
        &super::RetryPolicy {
            base_delay: Duration::from_millis(250),
            max_delay: Duration::from_secs(30),
            ..Default::default()
        },
        attempt,
    )
}

fn transient(error: &Error) -> bool {
    match error {
        Error::Transport(_) => true,
        Error::Response { error, .. } if matches!(error.as_ref(), Error::Transport(_)) => true,
        _ => {
            matches!(error.status(), Some(408 | 429 | 500 | 502 | 503 | 504))
                && !matches!(
                    error.code(),
                    Some(
                        "invalid_key"
                            | "key_revoked"
                            | "key_expired"
                            | "forbidden_scope"
                            | "rule_denied"
                            | "column_not_readable"
                            | "rules_invalid"
                    )
                )
        }
    }
}

pub(super) fn valid_handle(value: &str) -> bool {
    value.len() == 19
        && value.starts_with("h1-")
        && value[3..]
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

pub(super) fn valid_local(value: &str) -> bool {
    value == "0"
        || value.len() == 16
            && value
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn decimal(value: &str) -> bool {
    value
        .parse::<i64>()
        .is_ok_and(|number| number >= 0 && number.to_string() == value)
}

fn snapshot(value: &str) -> bool {
    let parts = value.split('.').collect::<Vec<_>>();
    parts.len() == 3 && parts[0] == "snap" && decimal(parts[1]) && decimal(parts[2])
}

fn apply(rows: &mut Rows, ops: &[Op], provisional: &mut BTreeSet<String>) -> Result<()> {
    for op in ops {
        let delete = match op.movement.as_deref() {
            Some("out") => true,
            Some("in") => false,
            None if op.op == "delete" => true,
            None if op.op == "insert" || op.op == "update" => false,
            _ => return Err(Error::Decode("invalid shape operation".into())),
        };
        if delete {
            rows.remove(&op.key);
            provisional.remove(&op.key);
        } else {
            let row = op
                .row
                .as_ref()
                .ok_or_else(|| Error::Decode("missing shape row".into()))?;
            rows.insert(op.key.clone(), row.clone());
            provisional.insert(op.key.clone());
        }
    }
    Ok(())
}
