use std::env;
use std::process::Command;

fn main() {
    println!("cargo:rerun-if-env-changed=RUSTC");
    println!("cargo:rerun-if-env-changed=CARGO");
    record_version("RUSTC", "FC_IMAGE_BUILDER_RUSTC_VERSION");
    record_version("CARGO", "FC_IMAGE_BUILDER_CARGO_VERSION");
}

fn record_version(executable_env: &str, output_env: &str) {
    let executable = env::var_os(executable_env)
        .unwrap_or_else(|| panic!("Cargo did not provide {executable_env}"));
    let output = Command::new(executable)
        .arg("--version")
        .output()
        .unwrap_or_else(|error| panic!("failed to read {executable_env} version: {error}"));
    assert!(
        output.status.success(),
        "{executable_env} --version exited unsuccessfully"
    );
    let version = String::from_utf8(output.stdout)
        .unwrap_or_else(|_| panic!("{executable_env} version output is not UTF-8"));
    let version = version.trim();
    assert!(
        !version.is_empty() && !version.contains('\n') && !version.contains('\0'),
        "{executable_env} version output is malformed"
    );
    println!("cargo:rustc-env={output_env}={version}");
}
