//! Database-wide SSE changes; reconnect explicitly with the last completed cursor.
use super::{
    documents::Database,
    errors::{Error, Result},
    ids::check_name,
};
use bytes::Bytes;
use futures_util::{Stream, StreamExt};
use std::pin::Pin;

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
        check_name("database", self.name())?;
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
}
