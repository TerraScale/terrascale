//! The opt-in reactive WebSocket opening exchange.

use futures_util::{SinkExt, StreamExt};
use tokio_tungstenite::tungstenite::{client::IntoClientRequest, http::header, Message};

use crate::error::{Error, ErrorKind, Result};
use crate::reactive::{decode_reactive_msgpack_json, Checkpoint, MSGPACK_MAX_MESSAGE_BYTES};

const SYNC_PROTOCOL: &str = "terrascale.sync.v1";

/// Open a canonical view socket, send the protocol subscribe frame, and
/// validate the first server frame. The socket is owned by this call and is
/// closed after the opening exchange; the regular live state machine remains
/// responsible for subsequent bounded polling/reconnects.
pub(crate) struct OpenRequest<'a> {
    pub base_url: &'a str,
    pub view: &'a str,
    pub generation: u64,
    pub capability: &'a str,
    pub subscription: &'a str,
    pub query: &'a str,
    pub parameters: &'a [u8],
    pub checkpoint: Option<&'a Checkpoint>,
    pub bearer: Option<&'a str>,
}

#[derive(Debug)]
pub(crate) struct Session {
    socket: tokio_tungstenite::WebSocketStream<
        tokio_tungstenite::MaybeTlsStream<tokio::net::TcpStream>,
    >,
}

impl Session {
    pub(crate) async fn close(&mut self) {
        let _ = self.socket.close(None).await;
    }

    pub(crate) async fn next(
        &mut self,
        cancellation: &crate::reactive::CancellationToken,
    ) -> Result<Vec<u8>> {
        loop {
            let frame = tokio::select! {
                _ = cancellation.cancelled() => {
                    let _ = self.socket.close(None).await;
                    return Err(crate::reactive::cancelled_error());
                }
                frame = self.socket.next() => frame,
            }
            .ok_or_else(|| Error::transport("WebSocket closed"))?
            .map_err(|error| Error::transport(format!("WebSocket receive failed: {error}")))?;
            match frame {
                Message::Binary(bytes) if bytes.len() <= MSGPACK_MAX_MESSAGE_BYTES => {
                    return Ok(bytes.to_vec());
                }
                Message::Binary(_) => {
                    return Err(Error::invalid_response(
                        "reactive WebSocket frame exceeds 8 MiB",
                    ));
                }
                Message::Ping(payload) => {
                    self.socket
                        .send(Message::Pong(payload))
                        .await
                        .map_err(|error| {
                            Error::transport(format!("WebSocket pong failed: {error}"))
                        })?;
                }
                Message::Pong(_) => {}
                Message::Close(frame) => {
                    let _ = self.socket.close(frame);
                    return Err(Error::transport("WebSocket closed"));
                }
                Message::Text(_) => {
                    return Err(Error::new(
                        ErrorKind::InvalidResponse,
                        "malformed_frame",
                        "reactive WebSocket frames must be binary MessagePack",
                    ));
                }
                Message::Frame(_) => {
                    return Err(Error::new(
                        ErrorKind::InvalidResponse,
                        "malformed_frame",
                        "reactive WebSocket frame is not binary MessagePack",
                    ));
                }
            }
        }
    }
}

pub(crate) async fn open(request: OpenRequest<'_>) -> Result<Session> {
    let OpenRequest {
        base_url,
        view,
        generation,
        capability,
        subscription,
        query,
        parameters,
        checkpoint,
        bearer,
    } = request;
    if capability.is_empty()
        || !capability
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || b"!#$%&'*+-.^_`|~".contains(&byte))
    {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_websocket_capability",
            "The view capability is not a valid WebSocket protocol token.",
        ));
    }
    let mut url = base_url.trim_end_matches('/').to_owned();
    if url.starts_with("https://") {
        url.replace_range(..8, "wss://");
    } else if url.starts_with("http://") {
        url.replace_range(..7, "ws://");
    } else {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_websocket_endpoint",
            "The WebSocket endpoint must use HTTP or HTTPS origin syntax.",
        ));
    }
    url.push_str("/reactive/views/");
    url.push_str(&percent_encode(view));
    url.push('/');
    url.push_str(&generation.to_string());
    url.push_str("/websocket");

    let mut request = url.into_client_request().map_err(|error| {
        Error::new(
            ErrorKind::Validation,
            "invalid_websocket_endpoint",
            format!("invalid WebSocket request: {error}"),
        )
    })?;
    let protocols = format!("{SYNC_PROTOCOL}, terrascale.cap.{capability}");
    request.headers_mut().insert(
        header::SEC_WEBSOCKET_PROTOCOL,
        protocols.parse().map_err(|_| {
            Error::new(
                ErrorKind::Validation,
                "invalid_websocket_capability",
                "The WebSocket subprotocol list is invalid.",
            )
        })?,
    );
    if let Some(token) = bearer {
        let value = format!("Bearer {token}").parse().map_err(|_| {
            Error::new(
                ErrorKind::Validation,
                "invalid_authorization",
                "The bearer credential is not a valid HTTP header value.",
            )
        })?;
        request.headers_mut().insert(header::AUTHORIZATION, value);
    }

    let (mut socket, response) = tokio_tungstenite::connect_async(request)
        .await
        .map_err(|error| Error::transport(format!("WebSocket connection failed: {error}")))?;
    if response
        .headers()
        .get(header::SEC_WEBSOCKET_PROTOCOL)
        .and_then(|value| value.to_str().ok())
        != Some(SYNC_PROTOCOL)
    {
        return Err(Error::new(
            ErrorKind::InvalidResponse,
            "invalid_websocket_protocol",
            "The server did not negotiate terrascale.sync.v1.",
        ));
    }
    socket
        .send(Message::Binary(
            encode_subscribe(subscription, query, parameters, checkpoint).into(),
        ))
        .await
        .map_err(|error| Error::transport(format!("WebSocket subscribe failed: {error}")))?;
    let frame = tokio::time::timeout(std::time::Duration::from_secs(25), socket.next())
        .await
        .map_err(|_| Error::transport("WebSocket opening frame timed out"))?
        .ok_or_else(|| Error::transport("WebSocket closed before its opening frame"))?
        .map_err(|error| Error::transport(format!("WebSocket receive failed: {error}")))?;
    let result = match frame {
        Message::Binary(bytes) if bytes.len() <= MSGPACK_MAX_MESSAGE_BYTES => {
            decode_reactive_msgpack_json(&bytes).map(|_| ())
        }
        Message::Binary(_) => Err(Error::invalid_response(
            "reactive WebSocket frame exceeds 8 MiB",
        )),
        Message::Text(_) => Err(Error::new(
            ErrorKind::InvalidResponse,
            "malformed_frame",
            "reactive WebSocket frames must be binary MessagePack",
        )),
        _ => Err(Error::new(
            ErrorKind::InvalidResponse,
            "malformed_frame",
            "reactive WebSocket opening frame is not binary MessagePack",
        )),
    };
    result.map(|_| Session { socket })
}

fn encode_subscribe(
    subscription: &str,
    query: &str,
    parameters: &[u8],
    checkpoint: Option<&Checkpoint>,
) -> Vec<u8> {
    let mut out = vec![0x97, 1, 0, 0, 1, 0, 0, 0x94];
    put_str(&mut out, subscription);
    put_str(&mut out, query);
    put_bin(&mut out, parameters);
    match checkpoint {
        Some(value) => put_bin(&mut out, &value.to_bytes()),
        None => out.push(0xc0),
    }
    out
}

fn put_str(out: &mut Vec<u8>, value: &str) {
    let bytes = value.as_bytes();
    if bytes.len() < 32 {
        out.push(0xa0 | bytes.len() as u8);
    } else if bytes.len() <= u8::MAX as usize {
        out.extend([0xd9, bytes.len() as u8]);
    } else {
        out.extend([0xda]);
        out.extend((bytes.len() as u16).to_be_bytes());
    }
    out.extend(bytes);
}

fn put_bin(out: &mut Vec<u8>, value: &[u8]) {
    if value.len() <= u8::MAX as usize {
        out.extend([0xc4, value.len() as u8]);
    } else if value.len() <= u16::MAX as usize {
        out.push(0xc5);
        out.extend((value.len() as u16).to_be_bytes());
    } else {
        out.push(0xc6);
        out.extend((value.len() as u32).to_be_bytes());
    }
    out.extend(value);
}

fn percent_encode(value: &str) -> String {
    value.bytes().fold(String::new(), |mut output, byte| {
        if byte.is_ascii_alphanumeric() || b"-_.~".contains(&byte) {
            output.push(byte as char);
        } else {
            output.push_str(&format!("%{byte:02X}"));
        }
        output
    })
}

#[cfg(test)]
mod tests {
    use super::encode_subscribe;

    #[test]
    fn subscribe_encoding_matches_pinned_vector_shape() {
        assert_eq!(
            encode_subscribe("q", "q", &[1, 2], None),
            vec![0x97, 1, 0, 0, 1, 0, 0, 0x94, 0xa1, b'q', 0xa1, b'q', 0xc4, 2, 1, 2, 0xc0,]
        );
    }
}
