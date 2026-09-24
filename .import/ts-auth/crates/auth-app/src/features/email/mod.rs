//! Transactional email delivery for hosted identity flows.

use std::{future::Future, pin::Pin, time::Duration};

use axum::Router;
use lettre::{
    AsyncSmtpTransport, AsyncTransport, Message, Tokio1Executor,
    transport::smtp::authentication::Credentials,
};
use utoipa::OpenApi;

use crate::AppState;

/// Object-safe asynchronous email sender shared by hosted authentication flows.
///
/// Implementations must handle their own delivery errors. A failed email must not
/// fail the authentication flow around it, matching the original sender contract.
pub trait EmailSender: Send + Sync + 'static {
    fn send<'a>(
        &'a self,
        to: &'a str,
        subject: &'a str,
        body: &'a str,
    ) -> Pin<Box<dyn Future<Output = ()> + Send + 'a>>;
}

/// Logs emails instead of sending them, for zero-configuration development setups.
#[derive(Clone, Copy, Debug, Default)]
pub struct LoggingEmailSender;

impl EmailSender for LoggingEmailSender {
    fn send<'a>(
        &'a self,
        to: &'a str,
        subject: &'a str,
        body: &'a str,
    ) -> Pin<Box<dyn Future<Output = ()> + Send + 'a>> {
        Box::pin(async move {
            tracing::info!(to, subject, body, "email delivery (logging sender)");
        })
    }
}

/// Sends transactional mail over authenticated SMTP with mandatory STARTTLS.
///
/// SMTP failures are logged and swallowed, so account creation, sign-in and
/// password reset requests keep the same outcome if the mail host is unavailable.
pub struct SmtpEmailSender {
    transport: Result<AsyncSmtpTransport<Tokio1Executor>, String>,
    from_address: String,
}

impl SmtpEmailSender {
    /// Creates an SMTP sender. Configuration completeness and port validation are
    /// checked by `AuthOptions` before this constructor is called.
    #[must_use]
    pub fn new(
        host: impl Into<String>,
        port: u16,
        username: impl Into<String>,
        password: impl Into<String>,
        from_address: impl Into<String>,
    ) -> Self {
        let host = host.into();
        let credentials = Credentials::new(username.into(), password.into());
        let transport = AsyncSmtpTransport::<Tokio1Executor>::starttls_relay(&host)
            .map(|builder| {
                builder
                    .port(port)
                    .credentials(credentials)
                    .timeout(Some(Duration::from_secs(30)))
                    .build()
            })
            .map_err(|error| error.to_string());
        Self {
            transport,
            from_address: from_address.into(),
        }
    }
}

impl EmailSender for SmtpEmailSender {
    fn send<'a>(
        &'a self,
        to: &'a str,
        subject: &'a str,
        body: &'a str,
    ) -> Pin<Box<dyn Future<Output = ()> + Send + 'a>> {
        Box::pin(async move {
            let from = self.from_address.parse();
            let recipient = to.parse();
            let (from, recipient) = match (from, recipient) {
                (Ok(from), Ok(recipient)) => (from, recipient),
                (Err(error), _) | (_, Err(error)) => {
                    tracing::error!(%error, recipient = to, subject, "invalid SMTP mailbox");
                    return;
                }
            };
            let message = Message::builder()
                .from(from)
                .to(recipient)
                .subject(subject)
                .body(body.to_owned());
            let message = match message {
                Ok(message) => message,
                Err(error) => {
                    tracing::error!(%error, recipient = to, subject, "failed to build SMTP message");
                    return;
                }
            };

            let transport = match &self.transport {
                Ok(transport) => transport,
                Err(error) => {
                    tracing::error!(%error, recipient = to, subject, "SMTP transport configuration failed");
                    return;
                }
            };
            if let Err(error) = transport.send(message).await {
                tracing::error!(%error, recipient = to, subject, "SMTP delivery failed");
            }
        })
    }
}

/// Email delivery is an application service and has no HTTP routes.
pub fn router() -> Router<AppState> {
    Router::new()
}

#[derive(OpenApi)]
#[openapi()]
struct EmailOpenApi;

/// Returns an empty OpenAPI document because this module exposes no route surface.
pub fn openapi() -> utoipa::openapi::OpenApi {
    EmailOpenApi::openapi()
}
