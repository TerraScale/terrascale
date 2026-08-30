//! The Datastar server-sent-event wire format.
//!
//! The browser runs the vendored official Datastar module. This server-side
//! module emits its documented SSE event format without adding a second browser
//! runtime. An action handler answers with `text/event-stream` and zero or more
//! events, each of which Datastar applies to the DOM.
//!
//! Event names follow the current Datastar wire protocol
//! (`datastar-patch-elements` and `datastar-patch-signals`).
//!
//! Every frame ends with the two newlines the SSE grammar requires to
//! terminate an event.

use std::time::Duration;

use axum::body::Body;
use axum::http::{HeaderValue, StatusCode, header};
use axum::response::Response;
use tokio_stream::StreamExt;

/// How a fragment patch attaches to its target element.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MergeMode {
    /// Replaces the target element itself (the Datastar default).
    Outer,
    /// Replaces the target element's children.
    Inner,
}

impl MergeMode {
    fn as_wire(self) -> &'static str {
        match self {
            Self::Outer => "outer",
            Self::Inner => "inner",
        }
    }
}

/// Splits one payload across as many `data:` lines as it needs.
///
/// The SSE grammar forbids raw newlines inside a `data:` field, so a
/// multi-line fragment becomes one `data: fragments` line per source line.
/// The runtime joins them back with newlines, which is what the Datastar SDKs
/// do as well.
fn data_lines(key: &str, payload: &str) -> String {
    let normalized = payload.replace("\r\n", "\n").replace('\r', "\n");
    let mut out = String::new();
    let mut emitted = false;
    for line in normalized.split_terminator('\n') {
        emitted = true;
        out.push_str("data: ");
        out.push_str(key);
        out.push(' ');
        out.push_str(line);
        out.push('\n');
    }
    if !emitted {
        out.push_str("data: ");
        out.push_str(key);
        out.push_str(" \n");
    }
    out
}

/// Builds one complete `event:` frame from pre-rendered `data:` lines.
fn frame(event: &str, data: &str) -> String {
    format!("event: {event}\n{data}\n", event = event, data = data)
}

/// Emits an event that patches one HTML fragment into the page.
///
/// `selector` is a CSS selector naming the target; `None` lets the runtime
/// match top-level fragment elements by their `id`, which is the mode the
/// studio's receipt and metric panels use.
pub fn merge_fragments(html: &str, selector: Option<&str>, mode: MergeMode) -> String {
    let mut data = String::new();
    if let Some(selector) = selector {
        data.push_str(&data_lines("selector", selector));
    }
    if mode != MergeMode::Outer {
        data.push_str(&data_lines("mode", mode.as_wire()));
    }
    data.push_str(&data_lines("elements", html));
    frame("datastar-patch-elements", &data)
}

/// Emits an event that removes every element matching `selector`.
pub fn remove_fragments(selector: &str) -> String {
    let data = format!("{}{}", data_lines("selector", selector), data_lines("mode", "remove"));
    frame("datastar-patch-elements", &data)
}

/// Emits an event that merges signals into the page's reactive store.
///
/// `signals` must already be a valid `data-signals` payload, so callers pass
/// rendered JSON.
pub fn merge_signals(signals: &str, only_if_missing: bool) -> String {
    let mut data = String::new();
    if only_if_missing {
        data.push_str(&data_lines("onlyIfMissing", "true"));
    }
    data.push_str(&data_lines("signals", signals));
    frame("datastar-patch-signals", &data)
}

/// Wraps already-rendered frames in an SSE response body.
///
/// The first frame is flushed immediately so a one-shot action answers with no
/// dependency on any timer.
pub fn response(frames: Vec<String>) -> Response {
    let stream = tokio_stream::iter(frames).map(Ok::<_, std::convert::Infallible>);
    sse_response(Body::from_stream(stream))
}

/// Builds a response that pushes `render`'s frames now and then every
/// `period`, over one long-lived connection.
///
/// This is the studio's live channel: the overview opens exactly one of these
/// and the server refreshes the metrics panel on its own cadence, so the page
/// never polls with separate requests. The task ends when the client goes
/// away, because the channel send is the loop's only wait point.
pub fn periodic<F, Fut>(
    period: Duration,
    mut shutdown: tokio::sync::watch::Receiver<bool>,
    render: F,
) -> Response
where
    F: Fn() -> Fut + Send + 'static,
    Fut: Future<Output = Vec<String>> + Send,
{
    let (sender, receiver) = tokio::sync::mpsc::channel::<String>(4);
    tokio::spawn(async move {
        let mut ticker = tokio::time::interval(period);
        ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        loop {
            tokio::select! {
                changed = shutdown.changed() => {
                    if changed.is_err() || *shutdown.borrow() {
                        return;
                    }
                }
                _ = ticker.tick() => {
                    let body = render().await.concat();
                    if sender.send(body).await.is_err() {
                        return;
                    }
                }
            }
        }
    });
    let stream = tokio_stream::wrappers::ReceiverStream::new(receiver)
        .map(Ok::<_, std::convert::Infallible>);
    sse_response(Body::from_stream(stream))
}

fn sse_response(body: Body) -> Response {
    let mut response = Response::new(body);
    *response.status_mut() = StatusCode::OK;
    let headers = response.headers_mut();
    headers.insert(header::CONTENT_TYPE, HeaderValue::from_static("text/event-stream"));
    headers.insert(header::CACHE_CONTROL, HeaderValue::from_static("no-cache, no-transform"));
    headers.insert(
        header::HeaderName::from_static("x-accel-buffering"),
        HeaderValue::from_static("no"),
    );
    response
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn merge_fragments_targets_a_selector_with_a_mode() {
        let frame = merge_fragments("<div id=\"a\">hi</div>", Some("#a"), MergeMode::Inner);
        assert!(frame.starts_with("event: datastar-patch-elements\n"));
        assert!(frame.contains("data: selector #a\n"));
        assert!(frame.contains("data: mode inner\n"));
        assert!(frame.contains("data: elements <div id=\"a\">hi</div>\n"));
        assert!(frame.ends_with("\n\n"));
    }

    #[test]
    fn merge_fragments_omits_defaults_and_splits_lines() {
        let frame = merge_fragments("<tr>\n<td>x</td>\n</tr>", None, MergeMode::Outer);
        assert!(!frame.contains("selector"));
        assert!(!frame.contains("mode"));
        assert_eq!(
            frame,
            "event: datastar-patch-elements\ndata: elements <tr>\ndata: elements <td>x</td>\ndata: elements </tr>\n\n"
        );
    }

    #[test]
    fn remove_fragments_carries_only_a_selector() {
        assert_eq!(
            remove_fragments("#stale-row"),
            "event: datastar-patch-elements\ndata: selector #stale-row\ndata: mode remove\n\n"
        );
    }

    #[test]
    fn merge_signals_round_trips_json() {
        assert_eq!(
            merge_signals("{\"engine_sequence\": 42}", false),
            "event: datastar-patch-signals\ndata: signals {\"engine_sequence\": 42}\n\n"
        );
        assert!(merge_signals("{}", true).contains("data: onlyIfMissing true\n"));
    }

    #[test]
    fn a_fragment_with_a_trailing_newline_emits_no_blank_data_line() {
        let frame = merge_fragments("<p>a</p>\n", None, MergeMode::Outer);
        assert!(frame.contains("data: elements <p>a</p>\n"));
        assert!(!frame.contains("data: elements \n"));
    }

    #[test]
    fn blank_lines_and_all_carriage_returns_round_trip_for_official_datastar() {
        let frame =
            merge_fragments("<pre>first\n\nsecond\rthird\r\n</pre>", None, MergeMode::Outer);
        assert_eq!(
            frame,
            "event: datastar-patch-elements\n\
data: elements <pre>first\n\
data: elements \n\
data: elements second\n\
data: elements third\n\
data: elements </pre>\n\n"
        );
    }

    #[tokio::test]
    async fn a_periodic_stream_ends_when_shutdown_begins() {
        let (shutdown, receiver) = tokio::sync::watch::channel(false);
        let response = periodic(Duration::from_millis(1), receiver, || async {
            vec![merge_signals("{\"alive\":true}", false)]
        });
        shutdown.send(true).expect("the stream still holds a receiver");
        let body = tokio::time::timeout(
            Duration::from_secs(1),
            axum::body::to_bytes(response.into_body(), 4096),
        )
        .await
        .expect("shutdown closes the stream")
        .expect("the bounded body reads");
        assert!(body.len() <= 4096);
    }
}
