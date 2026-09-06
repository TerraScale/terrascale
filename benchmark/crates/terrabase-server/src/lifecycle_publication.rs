//! Durable, bounded publication of native lifecycle observations to dashboard authority.
use bytes::Bytes;
use http_body_util::{BodyExt as _, Full, Limited};
use hyper_util::rt::TokioIo;
use rusqlite::OptionalExtension as _;
use serde::{Deserialize, Serialize};
use std::{
    path::Path,
    sync::{Arc, Mutex},
    time::Duration,
};
use zeroize::Zeroizing;

/// One native observation; unknown timestamps stay explicitly absent.
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct LifecyclePublicationSnapshot {
    /// Contract version.
    pub schema_version: u8,
    /// Trusted owning organization.
    pub organization_id: String,
    /// Trusted owning project.
    pub project_id: String,
    /// Canonical admitted database.
    pub database_id: String,
    /// Native observation time in RFC3339.
    pub observed_at: String,
    /// Actual authorized activity, never inferred from creation.
    pub last_authorized_request_at: Option<String>,
    /// Durable owner deletion protection.
    pub deletion_protection: bool,
    /// Native accepted access fence.
    pub access_revoked: bool,
    /// Native accepted billing stop.
    pub usage_stopped_at: Option<String>,
    /// Authority supplied retention boundary.
    pub retain_until: Option<String>,
}
impl LifecyclePublicationSnapshot {
    /// Builds a snapshot from actual durable engine lifecycle state.
    ///
    /// # Errors
    /// Returns [`PublicationError::Invalid`] when a timestamp cannot be represented.
    pub fn from_native(
        organization: String,
        project: String,
        database: String,
        observed_at: u64,
        state: &terrabase::engine::lifecycle_state::LifecycleState,
    ) -> Result<Self, PublicationError> {
        Ok(Self {
            schema_version: 1,
            organization_id: organization,
            project_id: project,
            database_id: database,
            observed_at: timestamp(observed_at)?,
            last_authorized_request_at: state
                .last_authorized_request_at
                .map(timestamp)
                .transpose()?,
            deletion_protection: state.deletion_protection,
            access_revoked: state.access_revoked(),
            usage_stopped_at: state
                .deletion
                .as_ref()
                .map(|v| timestamp(v.usage_stopped_at))
                .transpose()?,
            retain_until: state
                .deletion
                .as_ref()
                .and_then(|v| v.retain_until)
                .map(timestamp)
                .transpose()?,
        })
    }
}
fn timestamp(millis: u64) -> Result<String, PublicationError> {
    chrono::DateTime::from_timestamp_millis(
        i64::try_from(millis).map_err(|_| PublicationError::Invalid)?,
    )
    .map(|time| time.to_rfc3339_opts(chrono::SecondsFormat::Millis, true))
    .ok_or(PublicationError::Invalid)
}
/// Publication failed without discarding its durable retry bytes.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PublicationError {
    /// Invalid configuration or source observation.
    Invalid,
    /// Local durable state could not be read or committed.
    Storage,
    /// Dashboard CAS disagrees; operator reconciliation is required.
    Conflict,
    /// The transport or acknowledgement was unavailable or invalid.
    Unavailable,
}
/// One durable pending payload per database and a globally bounded sender.
pub struct NativeLifecyclePublisher {
    store: Mutex<rusqlite::Connection>,
    uri: hyper::Uri,
    key: Zeroizing<String>,
    tls: Arc<rustls::ClientConfig>,
    permit: tokio::sync::Semaphore,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Ack {
    schema_version: u8,
    generation: String,
}
impl NativeLifecyclePublisher {
    /// Opens the native durable outbox. The key is separate from lifecycle policy credentials.
    ///
    /// # Errors
    /// Returns an error for invalid endpoint/key/path configuration or unavailable storage/TLS setup.
    pub fn open(path: &Path, endpoint: &str, key: String) -> Result<Self, PublicationError> {
        let uri: hyper::Uri = endpoint.parse().map_err(|_| PublicationError::Invalid)?;
        if !path.is_absolute()
            || key.len() < 32
            || key.len() > 4096
            || !key.bytes().all(|v| (33..=126).contains(&v))
            || uri.scheme_str() != Some("https")
            || uri.host().is_none()
            || uri.authority().is_some_and(|v| v.as_str().contains('@'))
            || uri.path() != "/api/internal/lifecycle/publish"
            || uri.query().is_some()
        {
            return Err(PublicationError::Invalid);
        }
        let store = rusqlite::Connection::open(path).map_err(|_| PublicationError::Storage)?;
        store
            .execute_batch(
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS lifecycle_publication_outbox (
              scope TEXT PRIMARY KEY, generation INTEGER NOT NULL DEFAULT 0,
              observed TEXT, pending_generation INTEGER, payload BLOB
            ) WITHOUT ROWID;",
            )
            .map_err(|_| PublicationError::Storage)?;
        let roots: rustls::RootCertStore = webpki_roots::TLS_SERVER_ROOTS.iter().cloned().collect();
        let tls = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::aws_lc_rs::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .map_err(|_| PublicationError::Invalid)?
        .with_root_certificates(roots)
        .with_no_client_auth();
        Ok(Self {
            store: Mutex::new(store),
            uri,
            key: Zeroizing::new(key),
            tls: Arc::new(tls),
            permit: tokio::sync::Semaphore::new(16),
        })
    }

    fn stage(
        &self,
        snapshot: &LifecyclePublicationSnapshot,
    ) -> Result<(String, String, Vec<u8>), PublicationError> {
        if snapshot.schema_version != 1
            || [&snapshot.organization_id, &snapshot.project_id, &snapshot.database_id]
                .iter()
                .any(|id| id.is_empty() || id.len() > 255)
        {
            return Err(PublicationError::Invalid);
        }
        let observation = chrono::DateTime::parse_from_rfc3339(&snapshot.observed_at)
            .map_err(|_| PublicationError::Invalid)?;
        for value in [
            &snapshot.last_authorized_request_at,
            &snapshot.usage_stopped_at,
            &snapshot.retain_until,
        ]
        .into_iter()
        .flatten()
        {
            chrono::DateTime::parse_from_rfc3339(value).map_err(|_| PublicationError::Invalid)?;
        }
        if !snapshot.access_revoked && snapshot.usage_stopped_at.is_some() {
            return Err(PublicationError::Invalid);
        }
        let scope = serde_json::to_string(&(
            &snapshot.organization_id,
            &snapshot.project_id,
            &snapshot.database_id,
        ))
        .map_err(|_| PublicationError::Invalid)?;
        let mut store = self.store.lock().map_err(|_| PublicationError::Storage)?;
        let tx = store.transaction().map_err(|_| PublicationError::Storage)?;
        type PriorPublication = (i64, Option<String>, Option<i64>, Option<Vec<u8>>);
        let prior: Option<PriorPublication> = tx.query_row(
            "SELECT generation, observed, pending_generation, payload FROM lifecycle_publication_outbox WHERE scope=?1", [&scope],
            |r| Ok((r.get(0)?,r.get(1)?,r.get(2)?,r.get(3)?))).optional().map_err(|_| PublicationError::Storage)?;
        if let Some((_, _, Some(generation), Some(payload))) = &prior {
            return Ok((scope, generation.to_string(), payload.clone()));
        }
        let generation = prior.as_ref().map_or(0, |p| p.0);
        if let Some(last) = prior.as_ref().and_then(|p| p.1.as_ref())
            && observation
                < chrono::DateTime::parse_from_rfc3339(last)
                    .map_err(|_| PublicationError::Storage)?
        {
            return Err(PublicationError::Invalid);
        }
        let next = generation.checked_add(1).ok_or(PublicationError::Storage)?;
        let mut state = serde_json::to_value(snapshot).map_err(|_| PublicationError::Invalid)?;
        state["generation"] = serde_json::Value::String(next.to_string());
        let payload = serde_json::to_vec(&serde_json::json!({"state":state,
            "expectedGeneration":if generation==0 {None} else {Some(generation.to_string())}}))
        .map_err(|_| PublicationError::Invalid)?;
        if payload.len() > 16_384 {
            return Err(PublicationError::Invalid);
        }
        if prior.is_none() {
            let count: i64 = tx
                .query_row("SELECT count(*) FROM lifecycle_publication_outbox", [], |r| r.get(0))
                .map_err(|_| PublicationError::Storage)?;
            if count >= 100_000 {
                return Err(PublicationError::Storage);
            }
        }
        tx.execute("INSERT INTO lifecycle_publication_outbox(scope,generation,observed,pending_generation,payload)
            VALUES(?1,?2,?3,?4,?5) ON CONFLICT(scope) DO UPDATE SET observed=excluded.observed,
            pending_generation=excluded.pending_generation,payload=excluded.payload",
            rusqlite::params![scope,generation,snapshot.observed_at,next,payload]).map_err(|_|PublicationError::Storage)?;
        tx.commit().map_err(|_| PublicationError::Storage)?;
        Ok((scope, next.to_string(), payload))
    }

    /// Sends one observation; retries always recover the exact previously committed payload.
    /// Call from a periodic aggregate task at most once per scope per 60 seconds.
    ///
    /// # Errors
    /// Returns an error when staging, transport, acknowledgement, or durable
    /// acknowledgement commit fails.
    pub async fn publish(
        &self,
        snapshot: &LifecyclePublicationSnapshot,
    ) -> Result<(), PublicationError> {
        let _permit = self.permit.acquire().await.map_err(|_| PublicationError::Unavailable)?;
        let (scope, generation, payload) = self.stage(snapshot)?;
        let response = tokio::time::timeout(Duration::from_secs(10), self.send(payload.clone()))
            .await
            .map_err(|_| PublicationError::Unavailable)??;
        if response.schema_version != 1 || response.generation != generation {
            return Err(PublicationError::Unavailable);
        }
        let store = self.store.lock().map_err(|_| PublicationError::Storage)?;
        let changed = store.execute("UPDATE lifecycle_publication_outbox SET generation=pending_generation,
            pending_generation=NULL,payload=NULL WHERE scope=?1 AND pending_generation=?2 AND payload=?3",
            rusqlite::params![scope,generation.parse::<i64>().map_err(|_|PublicationError::Storage)?,payload])
            .map_err(|_|PublicationError::Storage)?;
        if changed != 1 {
            return Err(PublicationError::Storage);
        }
        Ok(())
    }

    async fn send(&self, payload: Vec<u8>) -> Result<Ack, PublicationError> {
        let host = self.uri.host().ok_or(PublicationError::Invalid)?;
        let tcp = tokio::net::TcpStream::connect((host, self.uri.port_u16().unwrap_or(443)))
            .await
            .map_err(|_| PublicationError::Unavailable)?;
        let name = rustls::pki_types::ServerName::try_from(host.to_owned())
            .map_err(|_| PublicationError::Invalid)?;
        let tls = tokio_rustls::TlsConnector::from(self.tls.clone())
            .connect(name, tcp)
            .await
            .map_err(|_| PublicationError::Unavailable)?;
        let (mut sender, connection) = hyper::client::conn::http1::Builder::new()
            .max_headers(32)
            .max_buf_size(16_384)
            .handshake(TokioIo::new(tls))
            .await
            .map_err(|_| PublicationError::Unavailable)?;
        let request = hyper::Request::post(self.uri.path())
            .header("host", self.uri.authority().ok_or(PublicationError::Invalid)?.as_str())
            .header("content-type", "application/json")
            .header("x-terrascale-lifecycle-publisher-key", self.key.as_str())
            .body(Full::new(Bytes::from(payload)))
            .map_err(|_| PublicationError::Invalid)?;
        let receive = async {
            let response =
                sender.send_request(request).await.map_err(|_| PublicationError::Unavailable)?;
            if response.status() == hyper::StatusCode::CONFLICT {
                return Err(PublicationError::Conflict);
            }
            if response.status() != hyper::StatusCode::OK {
                return Err(PublicationError::Unavailable);
            }
            let body = Limited::new(response.into_body(), 4096)
                .collect()
                .await
                .map_err(|_| PublicationError::Unavailable)?
                .to_bytes();
            serde_json::from_slice(&body).map_err(|_| PublicationError::Unavailable)
        };
        tokio::pin!(receive, connection);
        tokio::select! { biased; result=&mut receive=>result, _=&mut connection=>receive.await }
    }
}

/// Owned maintenance task; shutdown cancels any bounded in-flight transport.
pub struct LifecyclePublicationTask {
    stop: Option<tokio::sync::oneshot::Sender<()>>,
    join: tokio::task::JoinHandle<()>,
}
impl LifecyclePublicationTask {
    /// Returns an observation handle for the retained publication task.
    #[must_use]
    pub(crate) fn abort_handle(&self) -> tokio::task::AbortHandle {
        self.join.abort_handle()
    }

    /// Stops and joins the sole publication task.
    ///
    /// # Errors
    /// Reports an unexpected task failure.
    pub async fn shutdown(mut self) -> Result<(), tokio::task::JoinError> {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
        (&mut self.join).await
    }
}
impl Drop for LifecyclePublicationTask {
    fn drop(&mut self) {
        if let Some(stop) = self.stop.take() {
            stop.send(()).ok();
        }
        self.join.abort();
    }
}

/// Opens validated configuration and starts one 60-second aggregate task.
/// Missing regional aggregation or source state leaves the projection stale,
/// so the dashboard fails closed instead of accepting invented observations.
///
/// # Errors
/// Returns an error when the key file, outbox, endpoint, or publisher setup is invalid.
pub fn spawn(
    config: &crate::config::LifecyclePublicationConfig,
    registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
) -> Result<LifecyclePublicationTask, PublicationError> {
    use std::io::Read as _;
    let file = std::fs::File::open(&config.key_file).map_err(|_| PublicationError::Invalid)?;
    let mut secret = Zeroizing::new(String::new());
    file.take(4098).read_to_string(&mut secret).map_err(|_| PublicationError::Invalid)?;
    let publisher = Arc::new(NativeLifecyclePublisher::open(
        &config.outbox_path,
        &config.endpoint,
        secret.trim().to_owned(),
    )?);
    let (stop, mut signal) = tokio::sync::oneshot::channel();
    let join = tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_mins(1));
        interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        loop {
            tokio::select! { biased; _=&mut signal=>break, _=interval.tick()=>{} }
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .ok()
                .and_then(|v| u64::try_from(v.as_millis()).ok());
            let Some(now) = now else {
                continue;
            };
            let source = registry.clone();
            let snapshots =
                tokio::task::spawn_blocking(move || source.lifecycle_snapshots(now)).await;
            let Ok(Ok(snapshots)) = snapshots else {
                eprintln!("lifecycle publication source unavailable");
                continue;
            };
            let mut sends = tokio::task::JoinSet::new();
            for snapshot in snapshots {
                if sends.len() >= 16 {
                    tokio::select! { biased; _=&mut signal=>return, _=sends.join_next()=>{} }
                }
                let publisher = publisher.clone();
                sends.spawn(async move {
                    if publisher.publish(&snapshot).await.is_err() {
                        eprintln!("lifecycle publication pending retry");
                    }
                });
            }
            while !sends.is_empty() {
                tokio::select! { biased; _=&mut signal=>return, _=sends.join_next()=>{} }
            }
        }
    });
    Ok(LifecyclePublicationTask { stop: Some(stop), join })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn publication_shutdown_reports_worker_panic() {
        let (stop, _signal) = tokio::sync::oneshot::channel();
        let join = tokio::spawn(async {
            panic!("synthetic publication failure");
        });
        let worker = LifecyclePublicationTask { stop: Some(stop), join };
        assert!(worker.shutdown().await.is_err());
    }

    fn path() -> std::path::PathBuf {
        std::env::temp_dir().join(format!(
            "terrabase-lifecycle-outbox-{}-{}.sqlite",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ))
    }
    fn snapshot(at: u64) -> LifecyclePublicationSnapshot {
        LifecyclePublicationSnapshot::from_native(
            "org".into(),
            "project".into(),
            "database".into(),
            at,
            &terrabase::engine::lifecycle_state::LifecycleState::default(),
        )
        .unwrap()
    }
    #[tokio::test]
    async fn actual_https_lost_ack_restart_retries_same_bytes_then_advances_cas() {
        let certified = rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
        let certificate = certified.cert.der().clone();
        let key = rustls::pki_types::PrivatePkcs8KeyDer::from(certified.key_pair.serialize_der());
        let provider = Arc::new(rustls::crypto::aws_lc_rs::default_provider());
        let server = rustls::ServerConfig::builder_with_provider(provider.clone())
            .with_safe_default_protocol_versions()
            .unwrap()
            .with_no_client_auth()
            .with_single_cert(vec![certificate.clone()], key.into())
            .unwrap();
        let mut roots = rustls::RootCertStore::empty();
        roots.add(certificate).unwrap();
        let client = Arc::new(
            rustls::ClientConfig::builder_with_provider(provider)
                .with_safe_default_protocol_versions()
                .unwrap()
                .with_root_certificates(roots)
                .with_no_client_auth(),
        );
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let endpoint = format!(
            "https://localhost:{}/api/internal/lifecycle/publish",
            listener.local_addr().unwrap().port()
        );
        let received = Arc::new(Mutex::new(Vec::<Vec<u8>>::new()));
        let captured = received.clone();
        let remote = tokio::spawn(async move {
            for attempt in 0..3 {
                let (tcp, _) = listener.accept().await.unwrap();
                let tls = tokio_rustls::TlsAcceptor::from(Arc::new(server.clone()))
                    .accept(tcp)
                    .await
                    .unwrap();
                let captured = captured.clone();
                let service = hyper::service::service_fn(
                    move |request: hyper::Request<hyper::body::Incoming>| {
                        let captured = captured.clone();
                        async move {
                            assert_eq!(request.uri().path(), "/api/internal/lifecycle/publish");
                            assert_eq!(
                                request.headers()["x-terrascale-lifecycle-publisher-key"],
                                "k".repeat(32)
                            );
                            let bytes =
                                request.into_body().collect().await.unwrap().to_bytes().to_vec();
                            let value: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
                            captured.lock().unwrap().push(bytes);
                            let status = if attempt == 0 { 503 } else { 200 };
                            let ack = serde_json::json!({"schemaVersion":1,"generation":value["state"]["generation"]});
                            Ok::<_, std::convert::Infallible>(
                                hyper::Response::builder()
                                    .status(status)
                                    .body(Full::new(Bytes::from(ack.to_string())))
                                    .unwrap(),
                            )
                        }
                    },
                );
                hyper::server::conn::http1::Builder::new()
                    .serve_connection(TokioIo::new(tls), service)
                    .await
                    .unwrap();
            }
        });
        let location = path();
        {
            let mut publisher =
                NativeLifecyclePublisher::open(&location, &endpoint, "k".repeat(32)).unwrap();
            publisher.tls = client.clone();
            assert_eq!(
                publisher.publish(&snapshot(1000)).await,
                Err(PublicationError::Unavailable)
            );
        }
        {
            let mut publisher =
                NativeLifecyclePublisher::open(&location, &endpoint, "k".repeat(32)).unwrap();
            publisher.tls = client;
            publisher.publish(&snapshot(2000)).await.unwrap();
            publisher.publish(&snapshot(3000)).await.unwrap();
        }
        remote.await.unwrap();
        let messages = received.lock().unwrap();
        assert_eq!(messages[0], messages[1]);
        let first: serde_json::Value = serde_json::from_slice(&messages[0]).unwrap();
        let next: serde_json::Value = serde_json::from_slice(&messages[2]).unwrap();
        assert_eq!(first["expectedGeneration"], serde_json::Value::Null);
        assert_eq!(first["state"]["lastAuthorizedRequestAt"], serde_json::Value::Null);
        assert_eq!(next["expectedGeneration"], "1");
        assert_eq!(next["state"]["generation"], "2");
        drop(messages);
        std::fs::remove_file(location).unwrap();
    }

    #[test]
    fn outbox_rejects_clock_regression_and_invalid_secret_without_logging_it() {
        let location = path();
        assert!(
            NativeLifecyclePublisher::open(
                &location,
                "http://localhost/api/internal/lifecycle/publish",
                "k".repeat(32)
            )
            .is_err()
        );
        assert!(
            NativeLifecyclePublisher::open(
                &location,
                "https://localhost/api/internal/lifecycle/publish",
                "short".into()
            )
            .is_err()
        );
        let publisher = NativeLifecyclePublisher::open(
            &location,
            "https://localhost/api/internal/lifecycle/publish",
            "k".repeat(32),
        )
        .unwrap();
        let (scope, _, _) = publisher.stage(&snapshot(2000)).unwrap();
        publisher.store.lock().unwrap().execute("UPDATE lifecycle_publication_outbox SET generation=1,pending_generation=NULL,payload=NULL WHERE scope=?1",[scope]).unwrap();
        assert!(matches!(publisher.stage(&snapshot(1000)), Err(PublicationError::Invalid)));
        drop(publisher);
        std::fs::remove_file(location).unwrap();
    }
}
