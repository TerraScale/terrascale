//! Collection change webhooks. Requires the collection webhook producer API.
use super::{client::Req, ids::valid_entity_id, Collection, Error, Result};
use reqwest::{Method, StatusCode};
use serde::Deserialize;

/// Registration state. A feed gap requires reconciliation and recreation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WebhookStatus {
    Active,
    FeedGap,
}

/// Registration metadata, never a signing secret. Timestamps are RFC3339.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Webhook {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    pub url: String,
    pub created_at: String,
    pub status: WebhookStatus,
}

/// Creation-only response. Store the base64url-encoded 32-byte key securely now;
/// it cannot be retrieved later. Debug output redacts it.
#[derive(Clone, Deserialize)]
pub struct CreatedWebhook {
    #[serde(flatten)]
    pub webhook: Webhook,
    pub secret: String,
}
impl std::fmt::Debug for CreatedWebhook {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CreatedWebhook")
            .field("webhook", &self.webhook)
            .field("secret", &"[REDACTED]")
            .finish()
    }
}

/// Pending batches retry indefinitely until acknowledged or deleted.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WebhookDeliveryStatus {
    Pending,
    Delivered,
}

/// Sanitized failure category; receiver bodies and exception text are omitted.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WebhookDeliveryError {
    HttpError,
    TransportError,
    TargetRejected,
}

/// One of the latest 100 delivery records. Its ID stays stable across retries.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WebhookDelivery {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    pub created_at: String,
    pub status: WebhookDeliveryStatus,
    pub attempts: i64,
    pub last_attempt_at: Option<String>,
    pub next_attempt_at: Option<String>,
    pub http_status: Option<u16>,
    pub error_code: Option<WebhookDeliveryError>,
}

impl Collection {
    /// Registers an existing collection's public HTTPS receiver. Requires both
    /// `documents:read` and `documents:write`; write-only tokens are insufficient.
    /// Starts at the current feed head. The server validates public DNS and URL
    /// safety. Creation is never automatically replayed after an uncertain result.
    pub async fn create_webhook(&self, url: &str) -> Result<CreatedWebhook> {
        self.check()?;
        let req = Req::new(Method::POST, self.segments(&["webhooks"]))
            .json(&serde_json::json!({"url": url}))?;
        self.client()
            .send_project(req)
            .await
            .map_err(redact_creation_error)?
            .decode_status(&[StatusCode::CREATED])
            .map_err(redact_creation_error)
    }

    /// Lists registrations (`documents:read`) in ascending ULID order, without keys.
    /// Default limit 50, maximum 100. Continue after the last ID until empty.
    pub async fn list_webhooks(
        &self,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<Vec<Webhook>> {
        self.check()?;
        let mut req = Req::new(Method::GET, self.segments(&["webhooks"]));
        req.query = webhook_query(limit, after)?;
        self.client()
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK])
    }

    /// Stops future attempts; an in-flight attempt may finish. Requires both
    /// `documents:read` and `documents:write`.
    pub async fn delete_webhook(&self, webhook: &str) -> Result<()> {
        self.check()?;
        check_webhook_id(webhook)?;
        self.client()
            .send_project(Req::new(
                Method::DELETE,
                self.segments(&["webhooks", webhook]),
            ))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }

    /// Lists retained delivery logs (`documents:read`), with the same pagination as
    /// registrations. Dropping any webhook request future cancels the request.
    pub async fn list_webhook_deliveries(
        &self,
        webhook: &str,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<Vec<WebhookDelivery>> {
        self.check()?;
        check_webhook_id(webhook)?;
        let mut req = Req::new(
            Method::GET,
            self.segments(&["webhooks", webhook, "deliveries"]),
        );
        req.query = webhook_query(limit, after)?;
        self.client()
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK])
    }
}

// A failed creation can still contain its one-time key, including in partial bytes
// or a decoder's quoted value. Keep HTTP diagnostics, never credential content.
fn redact_creation_error(error: Error) -> Error {
    match error {
        Error::Response {
            status,
            error,
            request_id,
            ..
        } => Error::Response {
            status,
            error: Box::new(redact_creation_error(*error)),
            body: Vec::new(),
            request_id,
        },
        Error::Api {
            status, code, op, ..
        } => Error::Api {
            status,
            code,
            op,
            message: None,
            details: None,
        },
        Error::UndecodableAck { status, .. } => Error::UndecodableAck {
            status,
            message: "webhook creation response does not match the contract".into(),
        },
        Error::Decode(_) => Error::Decode("invalid webhook creation response".into()),
        Error::Transport(_) => Error::Transport("webhook creation transport failed".into()),
        Error::UniqueViolation { op, .. } => Error::UniqueViolation {
            constraint: "[REDACTED]".into(),
            op,
        },
        other => other,
    }
}

fn check_webhook_id(id: &str) -> Result<()> {
    if valid_entity_id(id) {
        Ok(())
    } else {
        Err(Error::InvalidInput(
            "webhook and pagination IDs must be canonical uppercase ULIDs".into(),
        ))
    }
}

fn webhook_query(limit: Option<u32>, after: Option<&str>) -> Result<Vec<(&'static str, String)>> {
    let mut query = Vec::new();
    if let Some(limit) = limit {
        if !(1..=100).contains(&limit) {
            return Err(Error::InvalidInput("webhook limit must be 1 to 100".into()));
        }
        query.push(("limit", limit.to_string()));
    }
    if let Some(after) = after {
        check_webhook_id(after)?;
        query.push(("after", after.to_owned()));
    }
    Ok(query)
}
