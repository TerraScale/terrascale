use std::collections::BTreeMap;
use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use serde::Deserialize;
use tokio_tungstenite::tungstenite::Message;

use super::client::{Client, WebSocket};
use super::table_shape::{valid_handle, valid_local};
use super::{Error, Result};

pub(super) const PROTOCOL: &str = "terrascale.shape.v1";

pub(super) enum Hint {
    Data,
    Local,
}

pub(super) struct Hints {
    socket: WebSocket,
}

#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum Frame {
    Ready,
    NewData {
        handle: String,
        offset: String,
        next_offset: String,
        #[serde(rename = "manifest")]
        _manifest: BTreeMap<String, serde::de::IgnoredAny>,
    },
    Provisional {
        handle: String,
        offset: String,
        local: String,
    },
    Error {
        code: String,
        retry_after: Option<f64>,
    },
}

impl Hints {
    pub(super) async fn open(
        client: &Client,
        database: &str,
        table: &str,
        query: &[(&str, String)],
    ) -> Result<Self> {
        tokio::time::timeout(Duration::from_secs(10), async {
            let socket = client
                .open_socket_query(
                    vec!["v1", "databases", database, "tables", table, "shape", "ws"],
                    query,
                    PROTOCOL,
                    64 * 1024,
                )
                .await?;
            let mut hints = Self { socket };
            if !matches!(hints.frame().await?, Frame::Ready) {
                return Err(Error::Decode("expected shape hint ready frame".into()));
            }
            Ok(hints)
        })
        .await
        .map_err(|_| Error::Transport("shape hint connection timed out".into()))?
    }

    pub(super) async fn wait(&mut self, handle: &str, offset: &str, local: &str) -> Result<Hint> {
        let applied = boundary(offset)?;
        loop {
            match self.frame().await? {
                Frame::NewData {
                    handle: next_handle,
                    offset,
                    next_offset,
                    ..
                } => {
                    let at = boundary(&offset)?;
                    let next = boundary(&next_offset)?;
                    if !valid_handle(&next_handle) || next <= at {
                        return Err(Error::Decode("invalid shape hint boundary".into()));
                    }
                    if next_handle != handle {
                        return Err(error("must_refetch", None));
                    }
                    if next > applied {
                        return Ok(Hint::Data);
                    }
                }
                Frame::Provisional {
                    handle: next_handle,
                    offset,
                    local: next_local,
                } => {
                    let at = boundary(&offset)?;
                    if !valid_handle(&next_handle) || !valid_local(&next_local) {
                        return Err(Error::Decode("invalid provisional hint boundary".into()));
                    }
                    if next_handle != handle {
                        return Err(error("must_refetch", None));
                    }
                    if at == applied && next_local != local {
                        return Ok(Hint::Local);
                    }
                }
                _ => return Err(Error::Decode("unexpected shape hint frame".into())),
            }
        }
    }

    async fn frame(&mut self) -> Result<Frame> {
        loop {
            match self.socket.next().await {
                Some(Ok(Message::Text(text))) => {
                    let frame = serde_json::from_str(&text)
                        .map_err(|_| Error::Decode("invalid shape hint frame".into()))?;
                    return match frame {
                        Frame::Error { code, retry_after } => {
                            let retry_after = retry_after
                                .map(Duration::try_from_secs_f64)
                                .transpose()
                                .map_err(|_| Error::Decode("invalid hint Retry-After".into()))?;
                            Err(error(&code, retry_after))
                        }
                        frame => Ok(frame),
                    };
                }
                Some(Ok(Message::Ping(_))) => {
                    self.socket
                        .flush()
                        .await
                        .map_err(|_| Error::Transport("shape hint pong failed".into()))?;
                }
                Some(Ok(Message::Pong(_))) => {}
                Some(Ok(Message::Close(_))) | None => {
                    return Err(Error::Transport("shape hints closed".into()))
                }
                Some(Err(_)) => {
                    return Err(Error::Transport("shape hint connection failed".into()))
                }
                _ => return Err(Error::Decode("invalid shape hint message".into())),
            }
        }
    }
}

fn boundary(value: &str) -> Result<i64> {
    value
        .parse::<i64>()
        .ok()
        .filter(|number| *number >= 0 && number.to_string() == value)
        .ok_or_else(|| Error::Decode("invalid shape hint offset".into()))
}

fn error(code: &str, retry_after: Option<Duration>) -> Error {
    let status = match code {
        "invalid_key" | "key_revoked" | "key_expired" => 401,
        "forbidden_scope" | "not_granted" | "column_not_granted" | "grant_invalid" => 403,
        "invalid_request" | "unsafe_where" | "unsupported_table" => 400,
        "unknown_database" | "unknown_table" => 404,
        "must_refetch" => 409,
        "shape_busy" => 429,
        _ => 503,
    };
    let body = serde_json::json!({"error": {"code": code}}).to_string();
    super::errors::from_project_response(status, retry_after, None, body.as_bytes(), false)
}

pub(super) fn terminal(error: &Error) -> bool {
    matches!(error.status(), Some(401 | 403))
        || matches!(
            error.code(),
            Some(
                "invalid_request"
                    | "unsafe_where"
                    | "unsupported_table"
                    | "unknown_database"
                    | "unknown_table"
                    | "invalid_key"
                    | "key_revoked"
                    | "key_expired"
                    | "forbidden_scope"
                    | "not_granted"
                    | "column_not_granted"
                    | "grant_invalid"
            )
        )
}
