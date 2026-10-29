//! Database-wide SSE changes: raw bytes, or typed events that resume from the last cursor.
use super::{
    documents::Database,
    errors::{from_project_response, Error, Result},
    ids::{check_database_selector, check_name, document_id, DocumentId},
    types::u64_string,
};
use bytes::Bytes;
use futures_util::{stream, Stream, StreamExt};
use serde::Deserialize;
use serde_json::value::RawValue;
use std::{pin::Pin, time::Duration};

/// Streaming bytes without buffering the potentially 30-minute feed.
pub type ChangeBytes = Pin<Box<dyn Stream<Item = Result<Bytes>> + Send>>;

/// Resume and collection filtering. Last-Event-ID takes precedence over cursor.
#[derive(Debug, Clone, Default)]
pub struct ChangesOptions {
    pub cursor: Option<String>,
    pub last_event_id: Option<String>,
    pub collections: Vec<String>,
}
impl Database {
    /// `GET D/changes`; no automatic reconnect or cursor advancement.
    /// A stream ends at token expiry or after 30 minutes. Dropping it cancels delivery.
    pub async fn change_bytes(&self, options: &ChangesOptions) -> Result<ChangeBytes> {
        check_database_selector(self.name())?;
        if options.collections.len() > 64 {
            return Err(Error::InvalidInput(
                "changes accepts at most 64 collections".into(),
            ));
        }
        for collection in &options.collections {
            check_name("collection", collection)?;
        }
        let mut query = Vec::new();
        if let Some(cursor) = &options.cursor {
            query.push(("cursor", cursor.clone()));
        }
        if !options.collections.is_empty() {
            query.push(("collections", options.collections.join(",")));
        }
        let response = self
            .client()
            .changes_response(self.name(), &query, options.last_event_id.as_deref())
            .await?;
        Ok(Box::pin(response.bytes_stream().map(|chunk| {
            chunk.map_err(|error| Error::Transport(error.to_string()))
        })))
    }

    /// Typed `GET D/changes`. After a clean end of stream (token expiry or the
    /// 30-minute lifetime) it reconnects with `Last-Event-ID` set to the last
    /// cursor. HTTP refusals, transport failures and malformed events end the
    /// stream after one error; [`Error::is_cursor_expired`] marks a cursor
    /// that fell out of retention. Delivery is at least once.
    pub async fn changes(&self, options: &ChangesOptions) -> Result<ChangeEvents> {
        let bytes = self.change_bytes(options).await?;
        let feed = Feed {
            db: self.clone(),
            options: options.clone(),
            limit: self.client().max_response(),
            bytes,
            buf: Vec::new(),
            event: Pending::default(),
            done: false,
        };
        Ok(Box::pin(stream::unfold(feed, |mut feed| async move {
            let item = feed.next().await?;
            feed.done = item.is_err();
            Some((item, feed))
        })))
    }
}

/// Typed change events; see [`Database::changes`].
pub type ChangeEvents = Pin<Box<dyn Stream<Item = Result<ChangeEvent>> + Send>>;

/// One changes-feed event.
#[derive(Debug, Clone)]
#[non_exhaustive]
pub enum ChangeEvent {
    /// A committed document change.
    Change(DocumentChange),
    /// A checkpoint: the position after everything delivered so far.
    Cursor(String),
}

impl ChangeEvent {
    /// The resume position after this event. Persist only `Some` values.
    #[must_use]
    pub fn cursor(&self) -> Option<&str> {
        match self {
            Self::Change(change) => change.cursor.as_deref(),
            Self::Cursor(cursor) => Some(cursor),
        }
    }
}

/// A document change. Deduplicate by origin, version, collection and id.
#[derive(Debug, Clone, Deserialize)]
#[non_exhaustive]
pub struct DocumentChange {
    /// Set only on the final change of a commit group.
    #[serde(skip)]
    pub cursor: Option<String>,
    pub collection: String,
    #[serde(deserialize_with = "document_id")]
    pub id: DocumentId,
    pub op: ChangeOp,
    #[serde(with = "u64_string")]
    pub version: u64,
    /// 16 hex characters naming the committing machine.
    pub origin: String,
    /// The current value on the serving machine; `None` once deleted.
    pub document: Option<Box<RawValue>>,
}

/// The kind of document change.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ChangeOp {
    Upsert,
    Delete,
}

struct Feed {
    db: Database,
    options: ChangesOptions,
    limit: usize,
    bytes: ChangeBytes,
    buf: Vec<u8>,
    event: Pending,
    done: bool,
}

#[derive(Default)]
struct Pending {
    kind: String,
    id: String,
    data: String,
    size: usize,
}

impl Feed {
    async fn next(&mut self) -> Option<Result<ChangeEvent>> {
        if self.done {
            return None;
        }
        loop {
            if let Some(end) = self.buf.iter().position(|&b| b == b'\n') {
                let line: Vec<u8> = self.buf.drain(..=end).collect();
                let line = line.strip_suffix(b"\n").unwrap_or(&line);
                let line = line.strip_suffix(b"\r").unwrap_or(line);
                match self.line(line) {
                    Ok(Some(event)) => return Some(Ok(event)),
                    Ok(None) => continue,
                    Err(error) => return Some(Err(error)),
                }
            }
            if self.event.size + self.buf.len() > self.limit {
                return Some(Err(invalid("changes event exceeds response limit")));
            }
            match self.bytes.next().await {
                Some(Ok(chunk)) => self.buf.extend_from_slice(&chunk),
                Some(Err(error)) => return Some(Err(error)),
                None => {
                    if !self.buf.is_empty() || self.event.size != 0 {
                        return Some(Err(invalid("truncated changes event")));
                    }
                    if self.options.last_event_id.is_none() {
                        return Some(Err(invalid("changes ended without a resume cursor")));
                    }
                    tokio::time::sleep(Duration::from_millis(100)).await;
                    match self.db.change_bytes(&self.options).await {
                        Ok(bytes) => self.bytes = bytes,
                        Err(error) => return Some(Err(error)),
                    }
                }
            }
        }
    }

    fn line(&mut self, line: &[u8]) -> Result<Option<ChangeEvent>> {
        if line.is_empty() {
            let event = std::mem::take(&mut self.event);
            return self.dispatch(event);
        }
        self.event.size += line.len() + 1;
        let line = std::str::from_utf8(line).map_err(|_| invalid("changes event is not UTF-8"))?;
        if line.starts_with(':') {
            return Ok(None);
        }
        let (field, value) = line.split_once(':').unwrap_or((line, ""));
        let value = value.strip_prefix(' ').unwrap_or(value);
        match field {
            "event" => value.clone_into(&mut self.event.kind),
            "id" if value.contains('\0') => return Err(invalid("changes cursor contains NUL")),
            "id" => value.clone_into(&mut self.event.id),
            "data" => {
                if !self.event.data.is_empty() {
                    self.event.data.push('\n');
                }
                self.event.data.push_str(value);
            }
            _ => {}
        }
        Ok(None)
    }

    fn dispatch(&mut self, event: Pending) -> Result<Option<ChangeEvent>> {
        let cursor = (!event.id.is_empty()).then_some(event.id);
        let event = match event.kind.as_str() {
            "" if event.data.is_empty() => return Ok(None),
            _ if event.data.is_empty() => return Err(invalid("changes event has no data")),
            "error" => {
                return Err(from_project_response(
                    200,
                    None,
                    None,
                    None,
                    event.data.as_bytes(),
                ))
            }
            "cursor" => {
                #[derive(Deserialize)]
                struct Checkpoint {
                    cursor: String,
                }
                let checkpoint: Checkpoint = decode(&event.data, "cursor")?;
                if cursor.as_deref() != Some(checkpoint.cursor.as_str()) {
                    return Err(invalid("cursor event disagrees with its ID"));
                }
                ChangeEvent::Cursor(checkpoint.cursor)
            }
            "change" => {
                let mut change: DocumentChange = decode(&event.data, "change")?;
                check_name("collection", &change.collection)?;
                if change.origin.len() != 16 || u64::from_str_radix(&change.origin, 16).is_err() {
                    return Err(invalid("invalid change origin"));
                }
                if change
                    .document
                    .as_ref()
                    .is_some_and(|doc| !doc.get().starts_with('{'))
                {
                    return Err(invalid("change document must be an object or null"));
                }
                change.cursor = cursor;
                ChangeEvent::Change(change)
            }
            other => return Err(invalid(&format!("unknown changes event {other:?}"))),
        };
        if let Some(cursor) = event.cursor() {
            self.options.last_event_id = Some(cursor.to_owned());
        }
        Ok(Some(event))
    }
}

fn decode<T: serde::de::DeserializeOwned>(data: &str, kind: &str) -> Result<T> {
    serde_json::from_str(data).map_err(|error| invalid(&format!("decode {kind} event: {error}")))
}

fn invalid(message: &str) -> Error {
    Error::Decode(message.to_owned())
}
