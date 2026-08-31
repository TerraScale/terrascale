//! `TerraBase` production daemon entry point.

use std::error::Error;

use terrabase_server::ServerConfig;

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let path = ServerConfig::path_from_process()?;
    let config = ServerConfig::load(&path)?;
    terrabase_server::run(config).await
}
