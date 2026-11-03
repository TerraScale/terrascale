//! The Fast suite must pin the 1.98.1 toolchain with or without rustup on
//! PATH: the resolver prefers rustup, then a managed toolchain directory, and
//! only then a PATH compiler that identifies itself as 1.98.1.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

struct Sandbox {
    root: PathBuf,
}

impl Sandbox {
    fn new(name: &str) -> Self {
        let root = std::env::temp_dir().join(format!("ts1386-{}-{}", name, std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("bin")).unwrap();
        Self { root }
    }

    fn write_executable(&self, relative: &str, body: &str) -> PathBuf {
        let path = self.root.join(relative);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, body).unwrap();
        make_executable(&path);
        path
    }

    fn home(&self) -> PathBuf {
        self.root.join("home")
    }

    fn run(&self, body: &str) -> (bool, String) {
        let script = self.write_executable(
            "probe.sh",
            &format!(
                "export HOME='{home}'\nexport RUSTUP_HOME='{rustup_home}'\nexport PATH='{bin}'\n{body}\n",
                home = self.home().display(),
                rustup_home = self.home().join(".rustup").display(),
                bin = self.root.join("bin").display(),
            ),
        );
        let output = Command::new("bash")
            .arg(&script)
            .env_remove("RUSTC")
            .env_remove("RUSTDOC")
            .env_remove("CARGO")
            .output()
            .expect("bash runs the resolver");
        (
            output.status.success(),
            String::from_utf8_lossy(&output.stdout).trim().to_string(),
        )
    }

    fn install_path_rustc(&self, body: &str) {
        self.write_executable("bin/rustc", body);
    }

    fn install_toolchain(&self, tools: &str, version: &str) -> PathBuf {
        let managed = self.root.join("managed").join("bin");
        let target = self
            .home()
            .join(".rustup/toolchains/1.98.1-x86_64-unknown-linux-gnu/bin");
        for tool in tools.split(' ') {
            let body = format!("#!/bin/sh\necho '{tool} {version} (fake 2026-01-01)'\n");
            self.write_executable(&format!("managed/bin/{tool}"), &body);
            fs::create_dir_all(&target).unwrap();
            fs::copy(managed.join(tool), target.join(tool)).unwrap();
            make_executable(&target.join(tool));
        }
        target
    }
}

impl Drop for Sandbox {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}

fn make_executable(path: &Path) {
    use std::os::unix::fs::PermissionsExt;
    let mut permissions = fs::metadata(path).unwrap().permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(path, permissions).unwrap();
}

fn repository_root() -> PathBuf {
    Path::new(&env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(2)
        .unwrap()
        .to_path_buf()
}

fn helper() -> String {
    repository_root()
        .join("sdk/rust/scripts/toolchain.sh")
        .display()
        .to_string()
}

fn use_helper() -> String {
    format!(
        ". '{h}'\nif toolchain_use 2>\"{{PROBE_DIR}}/err\"; then\n",
        h = helper()
    )
}

fn reject_branch() -> String {
    "else echo REJECTED; [[ \"$(<{PROBE_DIR}/err)\" == *1.98.1* ]] && echo MESSAGE_NAMES_TOOLCHAIN; fi\n".to_string()
}

fn fill_probe_dir(template: &str, sandbox: &Sandbox) -> String {
    template.replace("{PROBE_DIR}", &sandbox.root.display().to_string())
}

#[test]
fn resolves_the_managed_toolchain_without_rustup_and_pins_every_binary() {
    let sandbox = Sandbox::new("managed");
    let bin = sandbox.install_toolchain("rustc rustdoc cargo", "1.98.1");
    let body = format!(
        "{use_helper}\
         test \"$RUSTC\" = '{bin}/rustc' && test \"$CARGO\" = '{bin}/cargo' && test \"$RUSTDOC\" = '{bin}/rustdoc'\n\
         case \"$PATH\" in \"{bin}:\"*) echo MANAGED_PINNED;; *) echo WRONG_PATH;; esac\n\
         {reject}",
        use_helper = use_helper(),
        bin = bin.display(),
        reject = reject_branch(),
    );
    let (ok, out) = sandbox.run(&fill_probe_dir(&body, &sandbox));
    assert!(ok, "managed toolchain resolution failed: {out}");
    assert_eq!(
        out.lines().collect::<Vec<_>>(),
        ["MANAGED_PINNED"],
        "unexpected resolution: {out}"
    );
}

#[test]
fn accepts_a_path_rustc_only_when_it_reports_the_pinned_version() {
    let matching = Sandbox::new("path-matching");
    matching.install_path_rustc("#!/bin/sh\necho 'rustc 1.98.1 (fake 2026-01-01)'\n");
    matching.write_executable(
        "bin/rustdoc",
        "#!/bin/sh\necho 'rustdoc 1.98.1 (fake 2026-01-01)'\n",
    );
    matching.write_executable(
        "bin/cargo",
        "#!/bin/sh\necho 'cargo 1.98.1 (fake 2026-01-01)'\n",
    );
    let body = format!(
        "{use_helper}test \"$RUSTC\" = '{bin}/rustc' && echo PATH_ACCEPTED\n{reject}",
        use_helper = use_helper(),
        bin = matching.root.join("bin").display(),
        reject = reject_branch(),
    );
    let (ok, out) = matching.run(&fill_probe_dir(&body, &matching));
    assert!(ok, "matching PATH compiler resolution failed: {out}");
    assert_eq!(
        out.lines().collect::<Vec<_>>(),
        ["PATH_ACCEPTED"],
        "a 1.98.1 PATH compiler must resolve: {out}"
    );

    let mismatching = Sandbox::new("path-mismatch");
    mismatching.install_path_rustc("#!/bin/sh\necho 'rustc 1.99.0 (fake 2026-01-01)'\n");
    mismatching.write_executable(
        "bin/rustdoc",
        "#!/bin/sh\necho 'rustdoc 1.99.0 (fake 2026-01-01)'\n",
    );
    mismatching.write_executable(
        "bin/cargo",
        "#!/bin/sh\necho 'cargo 1.99.0 (fake 2026-01-01)'\n",
    );
    let body = format!(
        "{use_helper}echo WRONGLY_ACCEPTED\n{reject}",
        use_helper = use_helper(),
        reject = reject_branch(),
    );
    let (ok, out) = mismatching.run(&fill_probe_dir(&body, &mismatching));
    assert!(ok, "mismatched compiler probe failed: {out}");
    assert_eq!(
        out.lines().collect::<Vec<_>>(),
        ["REJECTED", "MESSAGE_NAMES_TOOLCHAIN"],
        "a different compiler must be rejected with the pinned version named: {out}"
    );
}

#[test]
fn fails_naming_the_pinned_toolchain_when_nothing_resolves() {
    let sandbox = Sandbox::new("missing");
    let body = format!(
        "{use_helper}echo WRONGLY_ACCEPTED\n{reject}",
        use_helper = use_helper(),
        reject = reject_branch(),
    );
    let (ok, out) = sandbox.run(&fill_probe_dir(&body, &sandbox));
    assert!(ok, "missing toolchain probe failed: {out}");
    assert_eq!(
        out.lines().collect::<Vec<_>>(),
        ["REJECTED", "MESSAGE_NAMES_TOOLCHAIN"],
        "missing toolchain must be rejected with the pinned version named: {out}"
    );
}

#[test]
fn prefers_rustup_when_it_is_available() {
    let sandbox = Sandbox::new("rustup");
    sandbox.install_toolchain("rustc rustdoc cargo", "1.98.1");
    let choice = sandbox.root.join("rustup-choice");
    sandbox.write_executable(
        "rustup-choice/rustc",
        "#!/bin/sh\necho 'rustc 1.98.1 (fake 2026-01-01)'\n",
    );
    sandbox.write_executable(
        "rustup-choice/rustdoc",
        "#!/bin/sh\necho 'rustdoc 1.98.1 (fake 2026-01-01)'\n",
    );
    sandbox.write_executable(
        "rustup-choice/cargo",
        "#!/bin/sh\necho 'cargo 1.98.1 (fake 2026-01-01)'\n",
    );
    sandbox.write_executable(
        "bin/rustup",
        &format!(
            "#!/bin/sh\ncase \"$4\" in rustc) echo '{choice}/rustc';; rustdoc) echo '{choice}/rustdoc';; esac\n",
            choice = choice.display(),
        ),
    );
    let body = format!(
        "{use_helper}\
         test \"$RUSTC\" = '{choice}/rustc' && test \"$CARGO\" = '{choice}/cargo' && echo RUSTUP_PREFERRED\n\
         {reject}",
        use_helper = use_helper(),
        choice = choice.display(),
        reject = reject_branch(),
    );
    let (ok, out) = sandbox.run(&fill_probe_dir(&body, &sandbox));
    assert!(ok, "rustup resolution failed: {out}");
    assert_eq!(
        out.lines().collect::<Vec<_>>(),
        ["RUSTUP_PREFERRED"],
        "rustup must win over the managed directory: {out}"
    );
}
