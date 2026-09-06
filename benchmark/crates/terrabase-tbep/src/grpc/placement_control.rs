//! Host-scoped mTLS control listener for durable pending placement intent.
//! It exposes no data operations and makes no membership or readiness claim.

use std::collections::BTreeSet;
use std::future::Future;
use std::net::SocketAddr;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::time::Duration;

use tokio_rustls::TlsAcceptor;
use tokio_stream::StreamExt as _;
use tonic::{Code, Request, Response, Status};

use super::{
    Permit, TbepGrpcConfig, authenticate_gateway_certificate, metadata_value, tls_server_config,
};
use crate::placement::{PlacementError, PlacementJournal};
use crate::proto::terrabase::v1 as pb;
use pb::placement_control_service_server::{
    PlacementControlService, PlacementControlServiceServer,
};

/// Authenticated placement control over one durable journal and bounded worker gate.
pub struct PlacementControlServer {
    journal: Arc<PlacementJournal>,
    config: TbepGrpcConfig,
    inflight: Arc<AtomicUsize>,
    draining: AtomicBool,
    observer_principals: BTreeSet<String>,
    observation_frames: AtomicU64,
}

impl PlacementControlServer {
    /// Constructs a separate control listener with exact orchestrator principals.
    ///
    /// # Errors
    /// Refuses an empty allowlist or non-SPIFFE principal.
    pub fn new(
        journal: Arc<PlacementJournal>,
        config: TbepGrpcConfig,
        observer_principals: BTreeSet<String>,
    ) -> Result<Self, PlacementError> {
        if (config.gateway_principals.is_empty() && observer_principals.is_empty())
            || !config.gateway_principals.is_disjoint(&observer_principals)
            || config.gateway_principals.iter().chain(observer_principals.iter()).any(|principal| {
                !principal.starts_with("spiffe://")
                    || principal.len() > super::MAX_PRINCIPAL_BYTES
                    || principal.chars().any(char::is_whitespace)
                    || principal.chars().any(char::is_control)
            })
        {
            return Err(PlacementError::Invalid);
        }
        Ok(Self {
            journal,
            config,
            inflight: Arc::new(AtomicUsize::new(0)),
            draining: AtomicBool::new(false),
            observer_principals,
            observation_frames: AtomicU64::new(0),
        })
    }

    /// Validates TLS 1.3 mutual authentication material and binds the control port.
    ///
    /// # Errors
    /// Returns invalid TLS material or a listener bind error before spawning service.
    pub async fn bind_tls(
        self: &Arc<Self>,
        address: SocketAddr,
        certificate: &str,
        key: &str,
        ca: &str,
    ) -> std::io::Result<PlacementControlListener> {
        let acceptor = TlsAcceptor::from(tls_server_config(certificate, key, ca)?);
        let listener = tokio::net::TcpListener::bind(address).await?;
        Ok(PlacementControlListener { listener, acceptor, server: Arc::clone(self) })
    }

    /// Prevents new control mutations while retaining already dispatched durable work.
    pub fn begin_drain(&self) {
        self.draining.store(true, Ordering::Release);
    }

    /// Waits for every dispatched SQLite transaction, including cancelled RPCs.
    ///
    /// # Errors
    /// Returns timeout if a durable transaction has not finished by the deadline.
    pub async fn drain(&self) -> std::io::Result<()> {
        self.begin_drain();
        tokio::time::timeout(self.config.shutdown_grace(), async {
            while self.inflight.load(Ordering::Acquire) != 0 {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        })
        .await
        .map_err(|_| {
            std::io::Error::new(
                std::io::ErrorKind::TimedOut,
                "placement control work did not drain",
            )
        })
    }

    fn authenticate<T>(&self, request: &Request<T>) -> Result<String, Status> {
        for forbidden in [super::TENANT_METADATA, super::PROJECT_METADATA, super::DATABASE_METADATA]
        {
            if request.metadata().contains_key(forbidden) {
                return Err(control_status(
                    Code::InvalidArgument,
                    "placement control is host scoped",
                ));
            }
        }
        let principal = metadata_value(
            request.metadata(),
            super::PRINCIPAL_METADATA,
            super::MAX_PRINCIPAL_BYTES,
        )?;
        if !self.config.gateway_principals.contains(&principal) {
            return Err(control_status(
                Code::PermissionDenied,
                "principal is not an admitted orchestrator",
            ));
        }
        let certificates = request.peer_certs().ok_or_else(|| {
            control_status(Code::Unauthenticated, "placement control requires mTLS")
        })?;
        let leaf = certificates.first().ok_or_else(|| {
            control_status(Code::Unauthenticated, "placement control requires mTLS")
        })?;
        authenticate_gateway_certificate(&principal, leaf.as_ref())?;
        Ok(principal)
    }

    async fn work<F>(&self, work: F) -> Result<pb::PlacementProposalResponse, Status>
    where
        F: FnOnce() -> Result<pb::PlacementProposalResponse, PlacementError> + Send + 'static,
    {
        if self.draining.load(Ordering::Acquire) {
            return Err(control_status(Code::Unavailable, "placement control is draining"));
        }
        if self.inflight.fetch_add(1, Ordering::AcqRel) >= self.config.max_inflight() {
            self.inflight.fetch_sub(1, Ordering::AcqRel);
            return Err(control_status(
                Code::ResourceExhausted,
                "placement control is at capacity",
            ));
        }
        let permit = Permit(Arc::clone(&self.inflight));
        // Recheck after reserving capacity so drain cannot pass between checks.
        if self.draining.load(Ordering::Acquire) {
            return Err(control_status(Code::Unavailable, "placement control is draining"));
        }
        let worker = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            work()
        });
        tokio::time::timeout(self.config.request_timeout(), worker)
            .await
            .map_err(|_| {
                control_status(
                    Code::DeadlineExceeded,
                    "placement journal outcome unknown; retry the same operation",
                )
            })?
            .map_err(|_| control_status(Code::Unavailable, "placement journal worker unavailable"))?
            .map_err(placement_status)
    }
}

/// A bound control listener whose service lifetime is explicitly owned by the daemon.
pub struct PlacementControlListener {
    listener: tokio::net::TcpListener,
    acceptor: TlsAcceptor,
    server: Arc<PlacementControlServer>,
}

impl PlacementControlListener {
    /// Returns the actual bound address, including an assigned port in tests.
    ///
    /// # Errors
    /// Returns a socket observation failure.
    pub fn local_addr(&self) -> std::io::Result<SocketAddr> {
        self.listener.local_addr()
    }

    /// Runs only placement control RPCs until shutdown. TLS handshakes, messages,
    /// requests, and SQLite work remain independently bounded.
    ///
    /// # Errors
    /// Returns a listener, TLS, or HTTP/2 serving error.
    pub async fn serve(
        self,
        shutdown: impl Future<Output = ()> + Send + 'static,
    ) -> Result<(), tonic::transport::Error> {
        let max_inflight = self.server.config.max_inflight();
        let handshake_timeout = self.server.config.tls_handshake_timeout();
        let incoming =
            tokio_stream::wrappers::TcpListenerStream::new(self.listener).map(move |accepted| {
                let acceptor = self.acceptor.clone();
                async move {
                    let stream = accepted?;
                    stream.set_nodelay(true)?;
                    tokio::time::timeout(handshake_timeout, acceptor.accept(stream)).await.map_err(
                        |_| {
                            std::io::Error::new(
                                std::io::ErrorKind::TimedOut,
                                "placement TLS handshake timeout",
                            )
                        },
                    )?
                }
            });
        let incoming = futures_util::StreamExt::buffer_unordered(incoming, max_inflight);
        tonic::transport::Server::builder()
            .concurrency_limit_per_connection(max_inflight)
            .max_concurrent_streams(u32::try_from(max_inflight).unwrap_or(u32::MAX))
            .http2_max_pending_accept_reset_streams(Some(max_inflight))
            .load_shed(true)
            .timeout(self.server.config.request_timeout())
            .add_service(
                PlacementControlServiceServer::new(Arc::clone(&self.server))
                    .max_decoding_message_size(self.server.config.max_message_bytes())
                    .max_encoding_message_size(self.server.config.max_message_bytes()),
            )
            .add_service(
                pb::maintenance_service_server::MaintenanceServiceServer::new(Arc::clone(
                    &self.server,
                ))
                .max_decoding_message_size(self.server.config.max_message_bytes())
                .max_encoding_message_size(self.server.config.max_message_bytes()),
            )
            .serve_with_incoming_shutdown(incoming, shutdown)
            .await
    }
}

#[tonic::async_trait]
impl PlacementControlService for Arc<PlacementControlServer> {
    async fn stage(
        &self,
        request: Request<pb::PlacementStageRequest>,
    ) -> Result<Response<pb::PlacementProposalResponse>, Status> {
        let principal = self.authenticate(&request)?;
        let operation = metadata_value(
            request.metadata(),
            super::IDEMPOTENCY_METADATA,
            super::MAX_IDEMPOTENCY_BYTES,
        )?;
        let request = request.into_inner();
        let journal = Arc::clone(&self.journal);
        self.work(move || journal.stage(&principal, &operation, request)).await.map(Response::new)
    }

    async fn get(
        &self,
        request: Request<pb::PlacementGetRequest>,
    ) -> Result<Response<pb::PlacementProposalResponse>, Status> {
        self.authenticate(&request)?;
        let journal = Arc::clone(&self.journal);
        self.work(move || journal.latest()).await.map(Response::new)
    }
}

fn placement_status(error: PlacementError) -> Status {
    let code = match error {
        PlacementError::Invalid => Code::InvalidArgument,
        PlacementError::IdentityMismatch | PlacementError::Conflict => Code::FailedPrecondition,
        PlacementError::RetryMismatch => Code::AlreadyExists,
        PlacementError::Storage => Code::Unavailable,
        PlacementError::Exhausted => Code::ResourceExhausted,
        PlacementError::NotFound => Code::NotFound,
    };
    control_status(code, &error.to_string())
}

fn control_status(code: Code, detail: &str) -> Status {
    let error = match code {
        Code::InvalidArgument => pb::ErrorCode::InvalidRequest,
        Code::Unauthenticated => pb::ErrorCode::UnboundChannel,
        Code::AlreadyExists => pb::ErrorCode::IdempotencyPairing,
        Code::ResourceExhausted => pb::ErrorCode::Overloaded,
        Code::Unavailable | Code::DeadlineExceeded => pb::ErrorCode::EngineFailure,
        _ => pb::ErrorCode::OperationRefused,
    };
    super::tbep_status(code, error, detail)
}

#[cfg(test)]
mod tests;

mod observation;
