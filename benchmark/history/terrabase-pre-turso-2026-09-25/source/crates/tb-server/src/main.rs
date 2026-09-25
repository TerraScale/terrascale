use std::error::Error;
use std::io;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;

use clap::Parser;
use tb_server::{
    CgroupMemoryMonitor, DEFAULT_LISTEN_PORT, LocalServerState, ReplicationConfig,
    ReplicationServer, ServerConfig, bind_member_control, install_prometheus_recorder,
    parse_fixed_hex, parse_local_authority_scope, run_distributed_process, run_local_v1_process,
    serve, serve_member_control,
};
use tokio::net::TcpListener;
use tokio::sync::{oneshot, watch};

#[derive(Debug, Parser)]
#[command(
    name = "terrabase-server",
    about = "Run one local TerraBase shard with loopback-only health and metrics routes"
)]
struct Arguments {
    /// Directory for this process's local durable shard.
    #[arg(long)]
    data_dir: PathBuf,

    /// Maximum process memory in bytes, including the reserved headroom.
    #[arg(long)]
    memory_limit_bytes: usize,

    /// Memory in bytes reserved for the runtime and allocator outside admission.
    #[arg(long)]
    memory_headroom_bytes: usize,

    /// Loopback administrative listener port.
    #[arg(long = "port", default_value_t = DEFAULT_LISTEN_PORT)]
    listen_port: u16,

    /// Trusted, preprovisioned local-v1 shard identity and active schemas.
    #[arg(long, requires = "client_config")]
    local_v1_config: Option<PathBuf>,

    /// TLS paths, client bind, and API-key registry for the HTTPS listener.
    #[arg(long)]
    client_config: Option<PathBuf>,

    /// Permit the client listener to use a concrete nonloopback address.
    #[arg(long, requires = "client_config")]
    client_allow_nonloopback: bool,

    /// Public mTLS replication gRPC listener. Supplying this enables network mode.
    #[arg(long)]
    replication_listen_addr: Option<SocketAddr>,

    /// Separate directory holding the accepted authority and per-origin inboxes.
    #[arg(long)]
    replication_data_dir: Option<PathBuf>,

    /// Signed TBCAU001 shard authority certificate.
    #[arg(long)]
    authority_certificate: Option<PathBuf>,

    /// Raw 32-byte Ed25519 authority trust key.
    #[arg(long)]
    trusted_public_key: Option<PathBuf>,

    /// This member's server certificate chain in PEM format.
    #[arg(long)]
    replication_server_certificate: Option<PathBuf>,

    /// This member's server private key in PEM format.
    #[arg(long)]
    replication_server_private_key: Option<PathBuf>,

    /// PEM roots used to authenticate remote member certificates.
    #[arg(long)]
    replication_client_ca_roots: Option<PathBuf>,

    /// Sole outbound member ID in the signed two-member v2 authority.
    #[arg(long)]
    replication_peer_member_id: Option<String>,

    /// Concrete IP and port of the sole outbound member's gRPC listener.
    #[arg(long)]
    replication_peer_addr: Option<SocketAddr>,

    /// Separate PEM roots for outbound replication server authentication.
    #[arg(long)]
    replication_server_ca_roots: Option<PathBuf>,

    /// Fixed-scope authority identifiers required for network mode.
    #[arg(long)]
    cluster_id: Option<String>,
    #[arg(long)]
    tenant_id: Option<String>,
    #[arg(long)]
    database_id: Option<String>,
    #[arg(long)]
    shard_id: Option<u64>,
    #[arg(long)]
    member_id: Option<String>,
    #[arg(long)]
    member_incarnation: Option<String>,

    /// Root-supervisor status socket inside a private per-attempt directory.
    #[arg(long)]
    member_control_socket: Option<PathBuf>,

    /// One-shot barrier before a receiver append, used only by process tests.
    #[cfg(feature = "replication-test-hooks")]
    #[arg(long, hide = true)]
    replication_test_append_barrier_dir: Option<PathBuf>,

    /// One-shot barrier after receiver sync and before its DurableAck, used only by process tests.
    #[cfg(feature = "replication-test-hooks")]
    #[arg(long, hide = true)]
    replication_test_post_sync_barrier_dir: Option<PathBuf>,
}

#[tokio::main]
async fn main() {
    if let Err(error) = run().await {
        eprintln!("terrabase-server: {error}");
        std::process::exit(1);
    }
}

async fn run() -> Result<(), Box<dyn Error>> {
    let arguments = Arguments::parse();
    let mut replication_config = arguments.replication_config()?;
    if replication_config.is_none() && arguments.member_control_socket.is_some() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "--member-control-socket requires member replication mode",
        )
        .into());
    }
    if arguments.local_v1_config.is_some() && arguments.client_config.is_none() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "--local-v1-config requires --client-config",
        )
        .into());
    }
    if arguments.client_config.is_some()
        && arguments.local_v1_config.is_none()
        && replication_config
            .as_ref()
            .and_then(ReplicationConfig::outbound_peer)
            .is_none()
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "signed-v2 HTTPS requires replication mode and the outbound peer triple",
        )
        .into());
    }
    if arguments.client_allow_nonloopback && arguments.client_config.is_none() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "--client-allow-nonloopback requires --client-config",
        )
        .into());
    }
    if arguments.local_v1_config.is_some() && replication_config.is_some() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "local-v1 client mode cannot be combined with member replication mode",
        )
        .into());
    }
    let config = ServerConfig::new(
        arguments.data_dir,
        arguments.listen_port,
        arguments.memory_limit_bytes,
        arguments.memory_headroom_bytes,
    )?;

    // Bind before Storage::open so an occupied port cannot create a shard
    // directory as a side effect of a server that will never start serving.
    let listener = TcpListener::bind(config.listen_addr()).await?;
    let address: SocketAddr = listener.local_addr()?;
    if let (Some(local_config_path), Some(client_config_path)) =
        (&arguments.local_v1_config, &arguments.client_config)
    {
        let prometheus = install_prometheus_recorder().map_err(std::io::Error::other)?;
        return run_local_v1_process(
            config,
            listener,
            local_config_path,
            client_config_path,
            arguments.client_allow_nonloopback,
            prometheus,
        )
        .await
        .map_err(Into::into);
    }
    if let Some(client_config_path) = arguments.client_config.as_deref() {
        let prometheus = install_prometheus_recorder().map_err(std::io::Error::other)?;
        return run_distributed_process(
            config,
            listener,
            replication_config
                .take()
                .expect("validated signed-v2 replication mode"),
            client_config_path,
            arguments.client_allow_nonloopback,
            arguments.member_control_socket.as_deref(),
            prometheus,
        )
        .await
        .map_err(Into::into);
    }
    let member_control_listener = arguments
        .member_control_socket
        .as_deref()
        .map(bind_member_control)
        .transpose()?;
    let replication_listener = match &replication_config {
        Some(replication) => Some(TcpListener::bind(replication.listen_addr).await?),
        None => None,
    };
    let prometheus = install_prometheus_recorder().map_err(std::io::Error::other)?;

    let (replication_shutdown_sender, replication_shutdown_receiver) = watch::channel(false);
    let (server, replication_server, sampler_task) =
        match (replication_config, replication_listener) {
            (Some(replication_config), Some(replication_listener)) => {
                let budget = config.initialize_memory_budget()?;
                let monitor = CgroupMemoryMonitor::open(replication_config.memory_profile).await?;
                let sampler_task = monitor.spawn_sampler(replication_shutdown_receiver.clone());
                // Recover the signed authority before touching the local shard directory.
                let replication_budget = budget.clone();
                let replication_server = tokio::task::spawn_blocking(move || {
                    ReplicationServer::open(&replication_config, replication_budget, monitor)
                })
                .await??;
                let document_member = replication_server.document_member_mode();
                let server = Arc::new(
                    tokio::task::spawn_blocking(move || {
                        if document_member {
                            LocalServerState::open_document_member_with_budget(budget)
                        } else {
                            LocalServerState::open_network_with_budget(&config, budget)
                        }
                    })
                    .await??,
                );
                (
                    server,
                    Some((
                        replication_server,
                        replication_listener,
                        member_control_listener,
                    )),
                    Some(sampler_task),
                )
            }
            (None, None) => (Arc::new(LocalServerState::open(&config)?), None, None),
            _ => unreachable!("replication configuration and listeners are paired"),
        };
    if replication_server.is_some() {
        eprintln!("terrabase-server starting on http://{address}");
    } else {
        eprintln!("terrabase-server ready on http://{address}");
    }

    let (shutdown_sender, shutdown_receiver) = oneshot::channel();
    let signal_state = Arc::clone(&server);
    let signal_task = tokio::spawn(async move {
        wait_for_shutdown_signal().await;
        signal_state.begin_draining();
        let _ = shutdown_sender.send(());
        let _ = replication_shutdown_sender.send(true);
    });

    let result = if let Some((replication_server, replication_listener, member_control_listener)) =
        replication_server
    {
        let member_identity = replication_server.member_control_identity();
        let (replication_ready_sender, replication_ready_receiver) = oneshot::channel();
        let readiness_state = Arc::clone(&server);
        let readiness = async move {
            if replication_ready_receiver.await.is_ok() {
                readiness_state.mark_replication_ready();
                eprintln!("terrabase-server ready on http://{address}");
            }
            Ok::<(), io::Error>(())
        };
        let replication_state = Arc::clone(&server);
        let member_control_state = Arc::clone(&server);
        let member_control_shutdown = replication_shutdown_receiver.clone();
        let member_control = async move {
            if let Some(listener) = member_control_listener {
                serve_member_control(
                    listener,
                    member_identity,
                    member_control_state,
                    member_control_shutdown,
                )
                .await
            } else {
                let mut shutdown = member_control_shutdown;
                while !*shutdown.borrow() {
                    if shutdown.changed().await.is_err() {
                        break;
                    }
                }
                Ok(())
            }
        };
        let admin = async {
            serve(listener, server, prometheus, shutdown_receiver)
                .await
                .map_err(io::Error::other)
        };
        let shutdown_state = replication_shutdown_receiver.clone();
        let replication = async {
            let result = replication_server
                .serve(
                    replication_listener,
                    replication_shutdown_receiver,
                    replication_ready_sender,
                    Arc::clone(&replication_state),
                )
                .await;
            if result.is_err() || !*shutdown_state.borrow() {
                replication_state.mark_failed();
            }
            result.map_err(io::Error::other)
        };
        tokio::try_join!(admin, replication, readiness, member_control).map(|_| ())
    } else {
        serve(listener, server, prometheus, shutdown_receiver)
            .await
            .map_err(io::Error::other)
    };
    signal_task.abort();
    if let Some(sampler_task) = sampler_task {
        sampler_task.abort();
    }
    result?;
    Ok(())
}

impl Arguments {
    fn replication_config(&self) -> Result<Option<ReplicationConfig>, io::Error> {
        let outbound_supplied = [
            self.replication_peer_member_id.is_some(),
            self.replication_peer_addr.is_some(),
            self.replication_server_ca_roots.is_some(),
        ];
        let supplied = [
            self.replication_listen_addr.is_some(),
            self.replication_data_dir.is_some(),
            self.authority_certificate.is_some(),
            self.trusted_public_key.is_some(),
            self.replication_server_certificate.is_some(),
            self.replication_server_private_key.is_some(),
            self.replication_client_ca_roots.is_some(),
            self.cluster_id.is_some(),
            self.tenant_id.is_some(),
            self.database_id.is_some(),
            self.shard_id.is_some(),
            self.member_id.is_some(),
            self.member_incarnation.is_some(),
        ];
        if !supplied.iter().any(|value| *value) && !outbound_supplied.iter().any(|value| *value) {
            #[cfg(feature = "replication-test-hooks")]
            if self.replication_test_append_barrier_dir.is_some()
                || self.replication_test_post_sync_barrier_dir.is_some()
            {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "replication test barriers require replication mode",
                ));
            }
            return Ok(None);
        }
        if supplied.iter().any(|value| !*value) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "replication mode requires all --replication-* and authority identity options",
            ));
        }
        if outbound_supplied.iter().any(|value| *value)
            && outbound_supplied.iter().any(|value| !*value)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "outbound replication requires --replication-peer-member-id, --replication-peer-addr, and --replication-server-ca-roots together",
            ));
        }
        let invalid = || {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "replication identity options are invalid",
            )
        };
        let local_scope = parse_local_authority_scope(
            self.cluster_id.as_deref().ok_or_else(invalid)?,
            self.tenant_id.as_deref().ok_or_else(invalid)?,
            self.database_id.as_deref().ok_or_else(invalid)?,
            self.shard_id.ok_or_else(invalid)?,
            self.member_id.as_deref().ok_or_else(invalid)?,
        )
        .map_err(io::Error::other)?;
        let incarnation =
            parse_fixed_hex::<16>(self.member_incarnation.as_deref().ok_or_else(invalid)?)
                .filter(|incarnation| *incarnation != [0; 16])
                .ok_or_else(invalid)?;
        let mut config = ReplicationConfig::new(
            self.replication_listen_addr.ok_or_else(invalid)?,
            self.replication_data_dir.clone().ok_or_else(invalid)?,
            self.authority_certificate.clone().ok_or_else(invalid)?,
            self.trusted_public_key.clone().ok_or_else(invalid)?,
            self.replication_server_certificate
                .clone()
                .ok_or_else(invalid)?,
            self.replication_server_private_key
                .clone()
                .ok_or_else(invalid)?,
            self.replication_client_ca_roots
                .clone()
                .ok_or_else(invalid)?,
            local_scope,
            incarnation,
            self.memory_limit_bytes,
            self.memory_headroom_bytes,
        )
        .map_err(io::Error::other)?;
        if outbound_supplied.iter().all(|value| *value) {
            let member = self
                .replication_peer_member_id
                .as_deref()
                .ok_or_else(invalid)?
                .parse()
                .map_err(|_| invalid())?;
            config = config
                .with_outbound_peer(
                    member,
                    self.replication_peer_addr.ok_or_else(invalid)?,
                    self.replication_server_ca_roots
                        .clone()
                        .ok_or_else(invalid)?,
                )
                .map_err(io::Error::other)?;
        }
        #[cfg(feature = "replication-test-hooks")]
        {
            config.set_test_append_barrier_dir(self.replication_test_append_barrier_dir.clone());
            config.set_test_post_sync_barrier_dir(
                self.replication_test_post_sync_barrier_dir.clone(),
            );
        }
        Ok(Some(config))
    }
}

async fn wait_for_shutdown_signal() {
    #[cfg(unix)]
    {
        let terminate =
            match tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()) {
                Ok(signal) => Some(signal),
                Err(error) => {
                    eprintln!("terrabase-server: failed to register SIGTERM handler: {error}");
                    None
                }
            };

        if let Some(mut terminate) = terminate {
            tokio::select! {
                result = tokio::signal::ctrl_c() => {
                    if let Err(error) = result {
                        eprintln!("terrabase-server: failed to register Ctrl+C handler: {error}");
                    }
                }
                _ = terminate.recv() => {}
            }
        } else if let Err(error) = tokio::signal::ctrl_c().await {
            eprintln!("terrabase-server: failed to register Ctrl+C handler: {error}");
        }
    }

    #[cfg(not(unix))]
    if let Err(error) = tokio::signal::ctrl_c().await {
        eprintln!("terrabase-server: failed to register Ctrl+C handler: {error}");
    }
}

#[cfg(test)]
mod tests {
    use clap::Parser;

    use super::Arguments;
    use tb_server::DEFAULT_LISTEN_PORT;

    #[test]
    fn cli_requires_both_memory_bounds_and_defaults_to_loopback_admin_port() {
        let parsed = Arguments::try_parse_from([
            "terrabase-server",
            "--data-dir",
            "/var/lib/terrabase/local",
            "--memory-limit-bytes",
            "100000000",
            "--memory-headroom-bytes",
            "10000000",
        ])
        .expect("complete server arguments");

        assert_eq!(parsed.listen_port, DEFAULT_LISTEN_PORT);
        assert_eq!(parsed.memory_limit_bytes, 100_000_000);
        assert!(Arguments::try_parse_from(["terrabase-server", "--data-dir", "/tmp/db"]).is_err());
        assert!(
            Arguments::try_parse_from([
                "terrabase-server",
                "--data-dir",
                "/tmp/db",
                "--memory-limit-bytes",
                "184467440737095516160000",
                "--memory-headroom-bytes",
                "1",
            ])
            .is_err()
        );
    }
}
