mod upstream {
    #![allow(dead_code, unused_imports)]
    include!(concat!(env!("CARGO_MANIFEST_DIR"), "/../cli/sync_server.rs"));
    pub mod revision;
    pub mod sandbox;
    pub mod server;
    pub mod tenant;
}

fn main() -> anyhow::Result<()> {
    upstream::server::run()
}
