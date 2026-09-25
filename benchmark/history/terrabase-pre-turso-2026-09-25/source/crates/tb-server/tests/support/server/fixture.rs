//! Shared packaged-binary resolution for process-level integration scenarios.

use std::path::PathBuf;

pub(crate) fn server_binary() -> PathBuf {
    if let Some(path) = std::env::var_os("TB_SERVER_TEST_BINARY") {
        let path = PathBuf::from(path);
        assert!(
            path.is_file(),
            "TB_SERVER_TEST_BINARY is not a file: {}",
            path.display()
        );
        return path
            .canonicalize()
            .expect("canonicalize TB_SERVER_TEST_BINARY");
    }

    let executable_name = format!("tb-server{}", std::env::consts::EXE_SUFFIX);
    let mut candidates = Vec::new();
    if let Some(directory) = std::env::current_exe()
        .expect("find process test executable")
        .parent()
    {
        candidates.push(directory.join(&executable_name));
        if let Some(parent) = directory.parent() {
            candidates.push(parent.join(&executable_name));
        }
    }
    candidates.push(PathBuf::from(env!("CARGO_BIN_EXE_tb-server")));
    candidates.push(PathBuf::from("/root/fc-harness-payloads/tb-server"));

    candidates
        .iter()
        .find(|candidate| candidate.is_file())
        .unwrap_or_else(|| {
            panic!(
                "could not find the tb-server executable; stage it as /root/fc-harness-payloads/tb-server or set TB_SERVER_TEST_BINARY"
            )
        })
        .canonicalize()
        .expect("canonicalize co-packaged server binary")
}

pub(crate) fn keygen_binary() -> PathBuf {
    if let Some(path) = std::env::var_os("TB_KEYGEN_TEST_BINARY") {
        let path = PathBuf::from(path);
        assert!(
            path.is_file(),
            "TB_KEYGEN_TEST_BINARY is not a file: {}",
            path.display()
        );
        return path
            .canonicalize()
            .expect("canonicalize TB_KEYGEN_TEST_BINARY");
    }

    let path = PathBuf::from(env!("CARGO_BIN_EXE_terrabase-keygen"));
    assert!(
        path.is_file(),
        "Cargo-provided terrabase-keygen binary is not a file: {}",
        path.display()
    );
    path.canonicalize()
        .expect("canonicalize co-packaged keygen binary")
}
