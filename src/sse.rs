//! The opt-in reactive server-sent-events stream.
//!
//! One `GET /api/v1/reactive/views/{view}/{generation}/stream` connection
//! stays open and delivers the same `terrascale.sync.v1` MessagePack
//! envelopes the WebSocket transport carries, framed as
//! `id: {nextOffset}\ndata: {base64 envelope}\n\n` events. A terminal
//! `event: error` or a dropped connection ends the session; the caller
//! reattaches from its last checkpoint.

use base64::engine::general_purpose::STANDARD as BASE64;
use base64::Engine as _;
use futures_util::StreamExt;

use crate::error::{Error, ErrorKind, Result};
use crate::reactive::CancellationToken;

/// One parsed stream event surfaced to the live subscription.
#[derive(Debug)]
pub(crate) enum Frame {
    /// A base64-decoded binary envelope.
    Envelope(Vec<u8>),
    /// The server ended the stream: a terminal `event: error` data code, or
    /// `None` for a dropped connection.
    Ended { code: Option<String> },
}

/// Bound for the raw line buffer. The largest legal event is the base64
/// spelling of one 8 MiB envelope (about 11.2 MiB of text).
const MAX_BUFFER_BYTES: usize = 16 * 1024 * 1024;

pub(crate) struct Session {
    stream: std::pin::Pin<
        Box<
            dyn futures_util::Stream<Item = std::result::Result<bytes::Bytes, reqwest::Error>>
                + Send,
        >,
    >,
    buffer: Vec<u8>,
    event: Option<String>,
    data: String,
}

impl std::fmt::Debug for Session {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("Session")
            .field("buffered", &self.buffer.len())
            .finish_non_exhaustive()
    }
}

/// Open the stream. The request arrives fully decorated (capability header,
/// `accept: text/event-stream`, and the same authentication as the log
/// route); pre-stream failures answer the standard JSON error response.
pub(crate) async fn open(request: crate::transport::Request) -> Result<Session> {
    let client = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|error| Error::transport(format!("HTTP client initialization failed: {error}")))?;
    let mut builder = client.get(request.url.as_str());
    for (name, value) in &request.headers {
        builder = builder.header(name.as_str(), value.as_str());
    }
    let response = builder
        .send()
        .await
        .map_err(|error| Error::transport(format!("reactive stream request failed: {error}")))?;
    let status = response.status().as_u16();
    if !(200..300).contains(&status) {
        let headers: Vec<(String, String)> = response
            .headers()
            .iter()
            .map(|(name, value)| {
                (
                    name.as_str().to_owned(),
                    value.to_str().unwrap_or_default().to_owned(),
                )
            })
            .collect();
        let body = response.bytes().await.map_err(|error| {
            Error::transport(format!("reading the error response failed: {error}"))
        })?;
        return Err(crate::client::decode_error(
            &request.route_name,
            &crate::transport::Response {
                status,
                headers,
                body,
            },
        ));
    }
    Ok(Session {
        stream: Box::pin(response.bytes_stream()),
        buffer: Vec::new(),
        event: None,
        data: String::new(),
    })
}

impl Session {
    /// Read the next frame, ignoring heartbeat comment lines. A
    /// fully-buffered event is dispatched without waiting for more bytes.
    pub(crate) async fn next(&mut self, cancellation: &CancellationToken) -> Result<Frame> {
        loop {
            if cancellation.is_cancelled() {
                return Err(crate::reactive::cancelled_error());
            }
            while let Some(end) = self.buffer.iter().position(|byte| *byte == b'\n') {
                let line: Vec<u8> = self.buffer.drain(..=end).collect();
                if let Some(frame) = self.line(&line[..line.len() - 1])? {
                    return Ok(frame);
                }
            }
            let chunk = tokio::select! {
                _ = cancellation.cancelled() => {
                    return Err(crate::reactive::cancelled_error());
                }
                chunk = self.stream.next() => chunk,
            };
            match chunk {
                Some(Ok(bytes)) => {
                    if self.buffer.len() + bytes.len() > MAX_BUFFER_BYTES {
                        return Err(Error::new(
                            ErrorKind::InvalidResponse,
                            "malformed_frame",
                            "reactive stream event exceeds its frame bound",
                        ));
                    }
                    self.buffer.extend_from_slice(&bytes);
                }
                Some(Err(error)) => {
                    return Err(Error::transport(format!(
                        "reactive stream read failed: {error}"
                    )));
                }
                None => return Ok(Frame::Ended { code: None }),
            }
        }
    }

    /// Consume one `\n`-terminated line (carriage return trimmed). Returns
    /// the completed frame when the line closes an event.
    fn line(&mut self, raw: &[u8]) -> Result<Option<Frame>> {
        let line = raw.strip_suffix(b"\r").unwrap_or(raw);
        if line.is_empty() {
            // An empty line dispatches the accumulated event.
            let event = self.event.take();
            let data = std::mem::take(&mut self.data);
            if data.is_empty() {
                return Ok(None);
            }
            if event.as_deref() == Some("error") {
                return Ok(Some(Frame::Ended { code: Some(data) }));
            }
            let bytes = BASE64.decode(data.as_bytes()).map_err(|error| {
                Error::new(
                    ErrorKind::InvalidResponse,
                    "malformed_frame",
                    format!("reactive stream event carried invalid base64 data: {error}"),
                )
            })?;
            return Ok(Some(Frame::Envelope(bytes)));
        }
        if line[0] == b':' {
            // Heartbeat comment line.
            return Ok(None);
        }
        let (field, value) = match line.iter().position(|byte| *byte == b':') {
            Some(separator) => {
                let value = &line[separator + 1..];
                let value = value.strip_prefix(b" ").unwrap_or(value);
                (&line[..separator], value)
            }
            None => (line, &line[..0]),
        };
        if field == b"event" {
            self.event = Some(String::from_utf8_lossy(value).into_owned());
        } else if field == b"data" {
            // The reference clients concatenate multiple data lines without
            // a separator, which also tolerates base64 split across lines.
            // The accumulated text is bounded by the base64 spelling of one
            // maximum-size envelope.
            if self.data.len() + value.len() > MAX_BUFFER_BYTES {
                return Err(Error::new(
                    ErrorKind::InvalidResponse,
                    "malformed_frame",
                    "reactive stream event exceeds its frame bound",
                ));
            }
            self.data.push_str(&String::from_utf8_lossy(value));
        }
        // `id` and unknown fields are accepted and ignored: reconnects are
        // driven by the caller's checkpoint, not by the browser event id.
        Ok(None)
    }
}

#[cfg(test)]
mod tests {
    use super::{Frame, Session};

    fn session() -> Session {
        Session {
            stream: Box::pin(futures_util::stream::empty()),
            buffer: Vec::new(),
            event: None,
            data: String::new(),
        }
    }

    #[test]
    fn comment_id_and_unknown_fields_are_ignored() {
        let mut session = session();
        assert!(session.line(b": ping").expect("comment").is_none());
        assert!(session.line(b"id: 41").expect("id").is_none());
        assert!(session.line(b"retry: 100").expect("unknown").is_none());
        // None of those fields carry data, so the dispatch line is a no-op.
        assert!(session.line(b"").expect("dispatch").is_none());
    }

    #[test]
    fn error_events_end_the_session_with_their_code() {
        let mut session = session();
        assert!(session.line(b"event: error").expect("event").is_none());
        assert!(session
            .line(b"data: reactive_unavailable")
            .expect("data")
            .is_none());
        match session.line(b"").expect("dispatch") {
            Some(Frame::Ended { code }) => {
                assert_eq!(code.as_deref(), Some("reactive_unavailable"));
            }
            other => panic!("expected an ended frame, got {other:?}"),
        }
    }

    #[test]
    fn data_lines_join_and_base64_decodes_to_envelope_bytes() {
        let mut session = session();
        assert!(session.line(b"data: AQID").expect("first data").is_none());
        assert!(session.line(b"data: BA==").expect("second data").is_none());
        match session.line(b"").expect("dispatch") {
            Some(Frame::Envelope(bytes)) => assert_eq!(bytes, vec![1, 2, 3, 4]),
            other => panic!("expected an envelope frame, got {other:?}"),
        }
    }

    #[test]
    fn invalid_base64_fails_closed() {
        let mut session = session();
        assert!(session.line(b"data: !!!").expect("data").is_none());
        let error = session.line(b"").expect_err("invalid base64");
        assert_eq!(error.code, "malformed_frame");
    }
}
