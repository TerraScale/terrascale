//! TerraBase Studio — the operator's loopback web UI over one engine location.
//!
//! The studio opens exactly one durable engine location and performs every
//! operation in-process through the `terrabase` library. It is not a customer
//! boundary and it is not a TBEP client: `terrabase-go` is the only client
//! allowed to talk to TerraBase, and the studio is an operator lane bound to
//! loopback with no authentication.
//!
//! ```text
//! TERRABASE_STUDIO_DIR=./my-location TERRABASE_STUDIO_ADDR=127.0.0.1:8090 \
//!   cargo run --manifest-path crates/terrabase-studio/Cargo.toml --release
//! ```

mod datastar;
mod engine_ops;
mod html;
mod json;
mod metrics;
mod pages;
mod routes;
mod s3_backup;
mod state;

use std::sync::Arc;

use state::{Config, StudioState};

fn main() {
    let config = Config::from_env();
    println!("TerraBase Studio {}", terrabase::VERSION);
    println!("  engine location : {}", config.data_dir.display());
    println!("  listen address  : {} (loopback, no authentication)", config.addr);
    println!("  engine status   : {}", terrabase::STATUS);

    let runtime = match tokio::runtime::Builder::new_multi_thread().enable_all().build() {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("cannot start the async runtime: {error}");
            std::process::exit(1);
        }
    };

    let address = config.addr.clone();
    match runtime.block_on(run(config)) {
        Ok(()) => println!("studio stopped on {address}"),
        Err(failure) => {
            eprintln!("{failure}");
            std::process::exit(1);
        }
    }
}

async fn run(config: Config) -> Result<(), String> {
    let address = config.addr.clone();
    let _socket = loopback_address(&address)?;
    let state = Arc::new(StudioState::open(config)?);
    let app = routes::router(Arc::clone(&state));
    tokio::spawn(s3_backup::run_scheduler(Arc::clone(&state)));
    let shutdown_state = Arc::clone(&state);

    let listener = tokio::net::TcpListener::bind(&address)
        .await
        .map_err(|error| format!("cannot bind {address}: {error}"))?;
    println!("  serving         : http://{address}/");

    axum::serve(listener, app)
        .with_graceful_shutdown(async move {
            shutdown_signal().await;
            shutdown_state.begin_shutdown();
        })
        .await
        .map_err(|error| format!("the listener failed: {error}"))
}

fn loopback_address(address: &str) -> Result<std::net::SocketAddr, String> {
    let socket = address.parse::<std::net::SocketAddr>().map_err(|error| {
        format!("TERRABASE_STUDIO_ADDR must be a numeric socket address: {error}")
    })?;
    if !socket.ip().is_loopback() {
        return Err(format!(
            "refusing to bind unauthenticated Studio outside loopback ({address})"
        ));
    }
    Ok(socket)
}

async fn shutdown_signal() {
    let control_c = async {
        let _ = tokio::signal::ctrl_c().await;
    };
    #[cfg(unix)]
    let terminate = async {
        match tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()) {
            Ok(mut signal) => {
                signal.recv().await;
            }
            Err(_) => std::future::pending::<()>().await,
        }
    };
    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    tokio::select! {
        () = control_c => {},
        () = terminate => {},
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn studio_accepts_only_numeric_loopback_listeners() {
        assert_eq!(loopback_address("127.0.0.1:8090").unwrap().port(), 8090);
        assert!(loopback_address("[::1]:8090").unwrap().ip().is_loopback());
        assert!(loopback_address("0.0.0.0:8090").is_err());
        assert!(loopback_address("192.0.2.1:8090").is_err());
        assert!(loopback_address("localhost:8090").is_err());
    }
}
