use mimalloc::MiMalloc;

// Release builds link musl statically; its allocator serializes threads, so
// the server uses mimalloc on every target.
#[global_allocator]
static GLOBAL: MiMalloc = MiMalloc;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::from_default_env())
        .init();
    let options = auth_app::AuthOptions::load(std::env::args().skip(1))?;
    auth_app::app::serve(options).await
}
