use std::error::Error;
use std::io;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;

use clap::Parser;
use tb_server::{
    CgroupMemoryMonitor, DEFAULT_LISTEN_PORT, LocalServerState, ReplicationConfig,
    ReplicationServer, ServerConfig, install_prometheus_recorder, parse_fixed_hex,
    parse_local_authority_scope, serve,
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

    /// One-shot append barrier used only by process tests.
    #[cfg(feature = "replication-test-hooks")]
    #[arg(long, hide = true)]
    replication_test_append_barrier_dir: Option<PathBuf>,
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
    let replication_config = arguments.replication_config()?;
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
                let server = Arc::new(
                    tokio::task::spawn_blocking(move || {
                        LocalServerState::open_network_with_budget(&config, budget)
                    })
                    .await??,
                );
                (
                    server,
                    Some((replication_server, replication_listener)),
                    Some(sampler_task),
                )
            }
            (None, None) => (Arc::new(LocalServerState::open(&config)?), None, None),
            _ => unreachable!("replication configuration and listener are paired"),
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

    let result = if let Some((replication_server, replication_listener)) = replication_server {
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
        tokio::try_join!(admin, replication, readiness).map(|_| ())
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
        if !supplied.iter().any(|value| *value) {
            #[cfg(feature = "replication-test-hooks")]
            if self.replication_test_append_barrier_dir.is_some() {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "--replication-test-append-barrier-dir requires replication mode",
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
        let config = ReplicationConfig::new(
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
        #[cfg(feature = "replication-test-hooks")]
        let config = {
            let mut config = config;
            config.set_test_append_barrier_dir(self.replication_test_append_barrier_dir.clone());
            config
        };
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
