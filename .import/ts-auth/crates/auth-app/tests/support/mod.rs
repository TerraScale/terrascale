pub mod terrabase_http;

/// Version the health endpoints report for this build: the package version,
/// plus `+<revision>` when `TS_AUTH_REVISION` was set at compile time.
#[allow(dead_code)]
pub fn expected_version() -> String {
    let package = env!("CARGO_PKG_VERSION");
    match option_env!("TS_AUTH_REVISION").filter(|revision| !revision.is_empty()) {
        Some(revision) => format!("{package}+{revision}"),
        None => package.to_owned(),
    }
}
