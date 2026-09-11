//! Local debugging probe (not shipped): open the engine at the exact failing
//! stack path and print the typed `EngineOpenError`.
fn main() {
    let path = std::path::Path::new(
        "/home/mariogk/Projects/TerraScale/terrabase/.terrascale/stack/gru/data/terrabase-local/local/local",
    );
    let config = match terrabase::engine::EngineConfig::durable(path) {
        Ok(config) => config,
        Err(error) => {
            println!("probe: EngineConfig::durable failed: {error:?}");
            return;
        }
    };
    match terrabase::engine::Engine::open(config) {
        Ok(_) => println!("probe: opened OK"),
        Err(error) => println!("probe: Engine::open failed: {error:?}"),
    }
}
