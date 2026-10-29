//! Incremental export reader. Child of client to reuse its scoped authorization.
use super::{Client, Resp};
use crate::v1::{
    ids::{check_database_selector, check_name},
    Database, DocumentId, Error, ExportDocument, Result,
};
use bytes::Bytes;
use futures_util::{stream, Stream, StreamExt};
use reqwest::{Method, StatusCode};
use serde::de::DeserializeOwned;
use std::pin::Pin;

type Body = Pin<Box<dyn Stream<Item = std::result::Result<Bytes, reqwest::Error>> + Send>>;
struct ExportRead {
    body: Body,
    pending: Bytes,
    line: Vec<u8>,
    limit: usize,
    context: Resp,
}
impl Client {
    async fn export_response(
        &self,
        database: &str,
        after: Option<(&str, DocumentId)>,
    ) -> Result<reqwest::Response> {
        if self.inner.project.is_none() {
            return Err(Error::InvalidInput(
                "export requires a project client".into(),
            ));
        }
        check_database_selector(database)?;
        let mut query = Vec::new();
        if let Some((collection, id)) = after {
            check_name("collection", collection)?;
            query = vec![
                ("after_collection", collection.to_owned()),
                ("after_id", id.to_string()),
            ];
        }
        let url = self.url(
            &self.inner.base,
            &["v1", "databases", database, "export"],
            &query,
        )?;
        let mut request = self
            .inner
            .http
            .request(Method::GET, url)
            .header("Accept", "application/x-ndjson");
        if let Some(region) = &self.inner.region_pin {
            request = request.header("X-TerraScale-Region-Pin", region);
        }
        // Exactly one first-party 401 refresh; no transport replay or endpoint change.
        let response = self.authorized(request, false, &mut false).await?;
        let wrong_region = self.inner.region_pin.as_ref().is_some_and(|region| {
            let mut headers = response.headers().get_all("X-TerraBase-Region").iter();
            headers.next().and_then(|s| s.to_str().ok()) != Some(region.as_str())
                || headers.next().is_some()
        });
        if response.status() != StatusCode::OK || wrong_region {
            let mut context = Resp {
                status: response.status(),
                headers: response.headers().clone(),
                body: Vec::new(),
                mutation: false,
            };
            let mut body = response.bytes_stream();
            while let Some(chunk) = body.next().await {
                let chunk = chunk
                    .map_err(|_| context.invalid("export refusal body was interrupted".into()))?;
                if context.body.len().saturating_add(chunk.len()) > self.inner.max_response {
                    return Err(context.invalid("export refusal exceeded response bound".into()));
                }
                context.body.extend_from_slice(&chunk);
            }
            context.check_status(&[StatusCode::OK])?;
            return Err(context.invalid("export status or serving region is invalid".into()));
        }
        Ok(response)
    }
}
impl Database {
    /// Streams live documents from one snapshot. Dropping the stream cancels I/O.
    /// The existing response bound applies per NDJSON line, not to the whole export.
    /// On error, explicitly resume after the last item consumed; that opens a new snapshot.
    pub async fn stream_export<T: DeserializeOwned + Send + 'static>(
        &self,
        after: Option<(&str, DocumentId)>,
    ) -> Result<impl Stream<Item = Result<ExportDocument<T>>> + Send + 'static> {
        let response = self.client().export_response(self.name(), after).await?;
        let context = Resp {
            status: response.status(),
            headers: response.headers().clone(),
            body: Vec::new(),
            mutation: false,
        };
        let state = ExportRead {
            body: Box::pin(response.bytes_stream()),
            pending: Bytes::new(),
            line: Vec::new(),
            limit: self.client().inner.max_response,
            context,
        };
        Ok(stream::unfold(Some(state), |state| async move {
            let mut state = state?;
            loop {
                let newline = state.pending.iter().position(|b| *b == b'\n');
                let take = newline.unwrap_or(state.pending.len());
                if state.line.len().saturating_add(take) > state.limit {
                    return Some((
                        Err(state
                            .context
                            .invalid("export NDJSON line exceeds response bound".into())),
                        None,
                    ));
                }
                state.line.extend_from_slice(&state.pending[..take]);
                if let Some(end) = newline {
                    state.pending = state.pending.slice(end + 1..);
                    let document = serde_json::from_slice(&state.line)
                        .map_err(|_| state.context.invalid("invalid export NDJSON line".into()));
                    state.line.clear();
                    let next = document.is_ok().then_some(state);
                    return Some((document, next));
                }
                state.pending = Bytes::new();
                match state.body.next().await {
                    Some(Ok(chunk)) => state.pending = chunk,
                    Some(Err(_)) => {
                        return Some((
                            Err(state
                                .context
                                .invalid("export HTTP body was interrupted".into())),
                            None,
                        ))
                    }
                    None if state.line.is_empty() => return None,
                    None => {
                        return Some((
                            Err(state
                                .context
                                .invalid("export ended within an NDJSON line".into())),
                            None,
                        ))
                    }
                }
            }
        }))
    }
}
