//! Optional PostgreSQL transport for the signed non-null INT8/TEXT point subset.
//! Statements and parameters remain separate throughout the mature driver path.

use std::fmt;
use std::future::Future;
use std::io;
use std::io::Cursor;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::task::{Context, Poll};
use std::time::Duration;

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;
use rustls::pki_types::{pem::PemObject, CertificateDer, ServerName};
use rustls::RootCertStore;
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio::task::{AbortHandle, JoinHandle};
use tokio_postgres::config::SslMode;
use tokio_postgres::tls::{ChannelBinding, MakeTlsConnect, TlsConnect, TlsStream};
use tokio_postgres::types::ToSql;
use tokio_postgres::{Client, GenericClient, Transaction};
use tokio_rustls::TlsConnector;

const MAX_BYTES: usize = 16 * 1024;

/// Dedicated PG credentials, explicit address and verified certificate trust.
pub struct PostgresConfig {
    pub host: String,
    pub port: u16,
    pub tls_server_name: String,
    pub ca_certificate_pem: Vec<u8>,
    pub database: String,
    pub key_id: String,
    pub password: String,
    pub connect_timeout: Duration,
}

impl fmt::Debug for PostgresConfig {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("PostgresConfig")
            .field("host", &self.host)
            .field("port", &self.port)
            .field("tls_server_name", &self.tls_server_name)
            .field("database", &self.database)
            .field("key_id", &self.key_id)
            .field("password", &"[redacted]")
            .finish_non_exhaustive()
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PostgresErrorKind {
    Configuration,
    Query,
    Connection,
    Database,
    Protocol,
}

/// SQLSTATE is preserved; driver text, statements, values and credentials are omitted.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PostgresError {
    pub kind: PostgresErrorKind,
    pub sql_state: Option<String>,
}

impl fmt::Display for PostgresError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "terrascale PostgreSQL {:?} failure", self.kind)?;
        if let Some(state) = &self.sql_state {
            write!(formatter, " ({state})")?;
        }
        Ok(())
    }
}
impl std::error::Error for PostgresError {}

fn error(kind: PostgresErrorKind) -> PostgresError {
    PostgresError {
        kind,
        sql_state: None,
    }
}
fn driver_error(failure: tokio_postgres::Error) -> PostgresError {
    match failure.code() {
        Some(state) => PostgresError {
            kind: PostgresErrorKind::Database,
            sql_state: Some(state.code().to_owned()),
        },
        None => error(PostgresErrorKind::Connection),
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PostgresValue {
    Int8(i64),
    Text(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PostgresQuery {
    pub statement: String,
    pub parameters: Vec<PostgresValue>,
}

impl PostgresQuery {
    pub fn new(
        statement: impl Into<String>,
        parameters: Vec<PostgresValue>,
    ) -> Result<Self, PostgresError> {
        let query = Self {
            statement: statement.into(),
            parameters,
        };
        query.validate()?;
        Ok(query)
    }

    pub fn validate(&self) -> Result<(), PostgresError> {
        if self.statement.trim().is_empty()
            || !valid_text(&self.statement)
            || self.parameters.len() > 4
        {
            return Err(error(PostgresErrorKind::Query));
        }
        for value in &self.parameters {
            if let PostgresValue::Text(text) = value {
                if !valid_text(text) {
                    return Err(error(PostgresErrorKind::Query));
                }
            }
        }
        Ok(())
    }

    fn driver_parameters(&self) -> Vec<&(dyn ToSql + Sync)> {
        self.parameters
            .iter()
            .map(|value| match value {
                PostgresValue::Int8(value) => value as &(dyn ToSql + Sync),
                PostgresValue::Text(value) => value as &(dyn ToSql + Sync),
            })
            .collect()
    }
}

fn valid_text(value: &str) -> bool {
    value.len() <= MAX_BYTES && !value.contains('\0')
}
fn canonical_base64(value: &str, length: usize) -> bool {
    URL_SAFE_NO_PAD
        .decode(value)
        .is_ok_and(|decoded| decoded.len() == length && URL_SAFE_NO_PAD.encode(decoded) == value)
}
fn canonical_database(value: &str) -> bool {
    value.len() == 36
        && value.bytes().enumerate().all(|(index, byte)| {
            if matches!(index, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)
            }
        })
}

impl PostgresConfig {
    pub fn validate(&self) -> Result<(), PostgresError> {
        let parts = self.password.split('.').collect::<Vec<_>>();
        if self.host.is_empty()
            || self.host.len() > 253
            || self
                .host
                .chars()
                .any(|value| value.is_whitespace() || matches!(value, ',' | '/' | '\0'))
            || self.port == 0
            || self.tls_server_name.is_empty()
            || ServerName::try_from(self.tls_server_name.clone()).is_err()
            || self.connect_timeout.is_zero()
            || self.connect_timeout > Duration::from_secs(10)
            || !canonical_database(&self.database)
            || !canonical_base64(&self.key_id, 16)
            || parts.len() != 3
            || parts[0] != "tbk1"
            || parts[1] != self.key_id
            || !canonical_base64(parts[2], 32)
            || self.ca_certificate_pem.len() > 64 * 1024
        {
            return Err(error(PostgresErrorKind::Configuration));
        }
        self.tls()?;
        Ok(())
    }

    fn tls(&self) -> Result<NamedTls, PostgresError> {
        let mut roots = RootCertStore::empty();
        let mut input = Cursor::new(&self.ca_certificate_pem);
        for certificate in CertificateDer::pem_reader_iter(&mut input) {
            let certificate = certificate.map_err(|_| error(PostgresErrorKind::Configuration))?;
            roots
                .add(certificate)
                .map_err(|_| error(PostgresErrorKind::Configuration))?;
        }
        if roots.is_empty() {
            return Err(error(PostgresErrorKind::Configuration));
        }
        let provider = Arc::new(rustls::crypto::ring::default_provider());
        let mut config = rustls::ClientConfig::builder_with_provider(provider)
            .with_protocol_versions(&[&rustls::version::TLS13])
            .map_err(|_| error(PostgresErrorKind::Configuration))?
            .with_root_certificates(roots)
            .with_no_client_auth();
        config.alpn_protocols = vec![b"postgresql".to_vec()];
        Ok(NamedTls {
            config: Arc::new(config),
            server_name: self.tls_server_name.clone(),
        })
    }
}

#[derive(Clone)]
struct NamedTls {
    config: Arc<rustls::ClientConfig>,
    server_name: String,
}

struct NamedTlsConnector {
    config: Arc<rustls::ClientConfig>,
    server_name: ServerName<'static>,
}

struct VerifiedTlsStream<S>(tokio_rustls::client::TlsStream<S>);

impl<S> MakeTlsConnect<S> for NamedTls
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    type Stream = VerifiedTlsStream<S>;
    type TlsConnect = NamedTlsConnector;
    type Error = io::Error;
    fn make_tls_connect(&mut self, _domain: &str) -> Result<Self::TlsConnect, Self::Error> {
        let server_name = ServerName::try_from(self.server_name.clone())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid TLS server name"))?;
        Ok(NamedTlsConnector {
            config: Arc::clone(&self.config),
            server_name,
        })
    }
}

impl<S> TlsConnect<S> for NamedTlsConnector
where
    S: AsyncRead + AsyncWrite + Unpin + Send + 'static,
{
    type Stream = VerifiedTlsStream<S>;
    type Error = io::Error;
    type Future = Pin<Box<dyn Future<Output = io::Result<Self::Stream>> + Send>>;

    fn connect(self, stream: S) -> Self::Future {
        Box::pin(async move {
            let stream = TlsConnector::from(self.config)
                .connect(self.server_name, stream)
                .await?;
            // Returning a stream permits the driver to send startup and credentials.
            // Offering ALPN alone is insufficient: a peer can negotiate none.
            if stream.get_ref().1.alpn_protocol() != Some(b"postgresql".as_slice()) {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "PostgreSQL ALPN was not negotiated",
                ));
            }
            Ok(VerifiedTlsStream(stream))
        })
    }
}

impl<S: AsyncRead + AsyncWrite + Unpin> TlsStream for VerifiedTlsStream<S> {
    fn channel_binding(&self) -> ChannelBinding {
        // The admitted TerraBase protocol authenticates its scoped TBK1 password
        // inside verified TLS, and does not expose SCRAM channel binding.
        ChannelBinding::none()
    }
}

impl<S: AsyncRead + AsyncWrite + Unpin> AsyncRead for VerifiedTlsStream<S> {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_read(cx, buf)
    }
}

impl<S: AsyncRead + AsyncWrite + Unpin> AsyncWrite for VerifiedTlsStream<S> {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.0).poll_write(cx, buf)
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_flush(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.0).poll_shutdown(cx)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PostgresColumn {
    pub name: String,
    pub oid: u32,
}
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PostgresRows {
    pub columns: Vec<PostgresColumn>,
    pub rows: Vec<Vec<PostgresValue>>,
}

async fn query<C: GenericClient + Sync>(
    client: &C,
    input: &PostgresQuery,
) -> Result<PostgresRows, PostgresError> {
    input.validate()?;
    let statement = client
        .prepare(&input.statement)
        .await
        .map_err(driver_error)?;
    let columns = statement
        .columns()
        .iter()
        .map(|column| {
            let oid = column.type_().oid();
            if !matches!(oid, 20 | 25) {
                return Err(error(PostgresErrorKind::Protocol));
            }
            Ok(PostgresColumn {
                name: column.name().to_owned(),
                oid,
            })
        })
        .collect::<Result<Vec<_>, _>>()?;
    let rows = client
        .query(&statement, &input.driver_parameters())
        .await
        .map_err(driver_error)?;
    if rows.len() > 1 {
        return Err(error(PostgresErrorKind::Protocol));
    }
    let rows = rows
        .into_iter()
        .map(|row| {
            columns
                .iter()
                .enumerate()
                .map(|(index, column)| match column.oid {
                    20 => row
                        .try_get::<usize, i64>(index)
                        .map(PostgresValue::Int8)
                        .map_err(|_| error(PostgresErrorKind::Protocol)),
                    25 => {
                        let text = row
                            .try_get::<usize, String>(index)
                            .map_err(|_| error(PostgresErrorKind::Protocol))?;
                        if !valid_text(&text) {
                            return Err(error(PostgresErrorKind::Protocol));
                        }
                        Ok(PostgresValue::Text(text))
                    }
                    _ => Err(error(PostgresErrorKind::Protocol)),
                })
                .collect::<Result<Vec<_>, _>>()
        })
        .collect::<Result<Vec<_>, _>>()?;
    Ok(PostgresRows { columns, rows })
}

async fn execute<C: GenericClient + Sync>(
    client: &C,
    input: &PostgresQuery,
) -> Result<u64, PostgresError> {
    input.validate()?;
    let statement = client
        .prepare(&input.statement)
        .await
        .map_err(driver_error)?;
    let affected = client
        .execute(&statement, &input.driver_parameters())
        .await
        .map_err(driver_error)?;
    if affected > 2 {
        return Err(error(PostgresErrorKind::Protocol));
    }
    Ok(affected)
}

struct ConnectionState {
    discarded: AtomicBool,
    abort: AbortHandle,
}

impl ConnectionState {
    fn discard(&self) {
        // Fence synchronously before abort scheduling can let a later call enqueue.
        if !self.discarded.swap(true, Ordering::AcqRel) {
            self.abort.abort();
        }
    }
}

/// A driver request can outlive its calling future. Cancellation must discard
/// the entire connection, including a COMMIT the driver has already marked done.
struct OperationGuard {
    state: Arc<ConnectionState>,
    armed: bool,
}

impl OperationGuard {
    fn new(state: Arc<ConnectionState>) -> Result<Self, PostgresError> {
        if state.discarded.load(Ordering::Acquire) || state.abort.is_finished() {
            return Err(error(PostgresErrorKind::Connection));
        }
        Ok(Self { state, armed: true })
    }

    fn finish<T>(mut self, result: &Result<T, PostgresError>) {
        // A completed server rejection is reusable, except completion-unknown.
        // Connection/protocol errors and uncertain outcomes close conservatively.
        if result.is_ok()
            || result.as_ref().err().is_some_and(|failure| {
                failure.kind == PostgresErrorKind::Database
                    && failure.sql_state.as_deref() != Some("40003")
            })
        {
            self.armed = false;
        }
    }

    fn succeeded(mut self) {
        self.armed = false;
    }
}

impl Drop for OperationGuard {
    fn drop(&mut self) {
        if self.armed {
            self.state.discard();
        }
    }
}

/// Owned driver task and socket. Canceling an in-flight operation immediately
/// fences this client and aborts its socket. No reconnect or mutation replay.
pub struct PostgresClient {
    client: Option<Client>,
    task: Option<JoinHandle<Result<(), PostgresError>>>,
    state: Arc<ConnectionState>,
    timeout: Duration,
}

impl PostgresClient {
    pub async fn connect(config: PostgresConfig) -> Result<Self, PostgresError> {
        config.validate()?;
        let tls = config.tls()?;
        let mut driver = tokio_postgres::Config::new();
        driver
            .host(&config.host)
            .port(config.port)
            .dbname(&config.database)
            .user(&config.key_id)
            .password(&config.password)
            .ssl_mode(SslMode::Require)
            .connect_timeout(config.connect_timeout);
        let (client, connection) =
            tokio::time::timeout(config.connect_timeout, driver.connect(tls))
                .await
                .map_err(|_| error(PostgresErrorKind::Connection))?
                .map_err(driver_error)?;
        let task = tokio::spawn(async move { connection.await.map_err(driver_error) });
        let state = Arc::new(ConnectionState {
            discarded: AtomicBool::new(false),
            abort: task.abort_handle(),
        });
        Ok(Self {
            client: Some(client),
            task: Some(task),
            state,
            timeout: config.connect_timeout,
        })
    }

    pub async fn query(&self, input: &PostgresQuery) -> Result<PostgresRows, PostgresError> {
        input.validate()?;
        let guard = OperationGuard::new(Arc::clone(&self.state))?;
        let result = query(
            self.client
                .as_ref()
                .ok_or_else(|| error(PostgresErrorKind::Connection))?,
            input,
        )
        .await;
        guard.finish(&result);
        result
    }

    pub async fn execute(&self, input: &PostgresQuery) -> Result<u64, PostgresError> {
        input.validate()?;
        let guard = OperationGuard::new(Arc::clone(&self.state))?;
        let result = execute(
            self.client
                .as_ref()
                .ok_or_else(|| error(PostgresErrorKind::Connection))?,
            input,
        )
        .await;
        guard.finish(&result);
        result
    }

    /// A borrowed transaction prevents interleaving through this client while
    /// active. Dropping it without completed commit/rollback discards the socket.
    pub async fn transaction(&mut self) -> Result<PostgresTransaction<'_>, PostgresError> {
        let state = Arc::clone(&self.state);
        let guard = OperationGuard::new(Arc::clone(&state))?;
        let transaction = self
            .client
            .as_mut()
            .ok_or_else(|| error(PostgresErrorKind::Connection))?
            .transaction()
            .await
            .map_err(driver_error)?;
        guard.succeeded();
        Ok(PostgresTransaction {
            transaction: Some(transaction),
            state,
        })
    }

    pub async fn close(mut self) -> Result<(), PostgresError> {
        let guard = OperationGuard {
            state: Arc::clone(&self.state),
            armed: true,
        };
        drop(self.client.take());
        let result = if let Some(mut task) = self.task.take() {
            match tokio::time::timeout(self.timeout, &mut task).await {
                Ok(result) => result.map_err(|_| error(PostgresErrorKind::Connection))?,
                Err(_) => {
                    task.abort();
                    let _ = task.await;
                    Err(error(PostgresErrorKind::Connection))
                }
            }
        } else {
            Ok(())
        };
        guard.finish(&result);
        result
    }
}

impl Drop for PostgresClient {
    fn drop(&mut self) {
        self.state.discard();
    }
}

pub struct PostgresTransaction<'a> {
    transaction: Option<Transaction<'a>>,
    state: Arc<ConnectionState>,
}
impl PostgresTransaction<'_> {
    pub async fn query(&self, input: &PostgresQuery) -> Result<PostgresRows, PostgresError> {
        input.validate()?;
        let guard = OperationGuard::new(Arc::clone(&self.state))?;
        let result = query(
            self.transaction
                .as_ref()
                .ok_or_else(|| error(PostgresErrorKind::Connection))?,
            input,
        )
        .await;
        guard.finish(&result);
        result
    }
    pub async fn execute(&self, input: &PostgresQuery) -> Result<u64, PostgresError> {
        input.validate()?;
        let guard = OperationGuard::new(Arc::clone(&self.state))?;
        let result = execute(
            self.transaction
                .as_ref()
                .ok_or_else(|| error(PostgresErrorKind::Connection))?,
            input,
        )
        .await;
        guard.finish(&result);
        result
    }
    pub async fn commit(mut self) -> Result<(), PostgresError> {
        let guard = OperationGuard::new(Arc::clone(&self.state))?;
        let result = self
            .transaction
            .take()
            .ok_or_else(|| error(PostgresErrorKind::Connection))?
            .commit()
            .await
            .map_err(driver_error);
        if result.is_ok() {
            guard.succeeded();
        }
        result
    }
    pub async fn rollback(mut self) -> Result<(), PostgresError> {
        let guard = OperationGuard::new(Arc::clone(&self.state))?;
        let result = self
            .transaction
            .take()
            .ok_or_else(|| error(PostgresErrorKind::Connection))?
            .rollback()
            .await
            .map_err(driver_error);
        if result.is_ok() {
            guard.succeeded();
        }
        result
    }
}

impl Drop for PostgresTransaction<'_> {
    fn drop(&mut self) {
        if self.transaction.is_some() {
            self.state.discard();
        }
    }
}
