use std::env;
use std::path::Path;
use std::process::Command;

use sha2::{Digest, Sha256};

const PINNED_TURSO: &str = "8549c16595d2faf1bdd6ee24aee0be8bfabb3d4a";
// Git tree of the pinned gitlink plus TerraBase's ordered patches/turso series,
// as printed by TerraBase's `scripts/ci/prepare-turso.sh --tree`.
const PINNED_TURSO_TREE: &str = "4da45c6e8290634cff70f371d123b0564206e4b0";

fn git(turso: &Path, index: Option<&Path>, args: &[&str]) -> Vec<u8> {
    let mut command = Command::new("git");
    command.arg("-C").arg(turso).args(args);
    if let Some(index) = index {
        command.env("GIT_INDEX_FILE", index);
    }
    let output = command
        .output()
        .expect("git is required to identify the pinned Turso source");
    assert!(
        output.status.success(),
        "cannot identify Turso source: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    output.stdout
}

fn text(bytes: Vec<u8>) -> String {
    String::from_utf8(bytes)
        .expect("Turso git output is UTF-8")
        .trim()
        .to_owned()
}

fn main() {
    let manifest = env::var("CARGO_MANIFEST_DIR").expect("Cargo manifest directory");
    let turso = Path::new(&manifest).join("../../terrabase/crates/turso");
    let revision = text(git(&turso, None, &["rev-parse", "HEAD"]));
    assert_eq!(
        revision, PINNED_TURSO,
        "Turso source differs from the pinned TerraBase gitlink"
    );
    // Hash the whole checkout, including files the patch series adds, through
    // a fresh scratch index built from HEAD. Copying the submodule's own index
    // would inherit assume-unchanged/skip-worktree flags that hide edits.
    let index = Path::new(&env::var("OUT_DIR").expect("Cargo out directory")).join("turso-index");
    let _ = std::fs::remove_file(&index);
    git(&turso, Some(&index), &["read-tree", "HEAD"]);
    git(&turso, Some(&index), &["add", "--all"]);
    let tree = text(git(&turso, Some(&index), &["write-tree"]));
    assert_eq!(
        tree, PINNED_TURSO_TREE,
        "Turso checkout differs from the pinned gitlink plus TerraBase's patches/turso series"
    );
    let diff = git(&turso, None, &["diff", "--binary", "HEAD", &tree, "--"]);
    let diff_sha256 = format!("{:x}", Sha256::digest(&diff));
    let changed = text(git(
        &turso,
        None,
        &["diff", "--name-only", "HEAD", &tree, "--"],
    ));
    let mut file_inventory = Vec::new();
    for name in changed.lines() {
        assert!(
            !name.contains(';') && !name.contains('='),
            "Turso changed path cannot be encoded in the receipt"
        );
        let path = turso.join(name);
        let digest = if path.is_file() {
            format!(
                "{:x}",
                Sha256::digest(std::fs::read(path).expect("read changed Turso file"))
            )
        } else {
            "deleted".to_owned()
        };
        file_inventory.push(format!("{name}={digest}"));
    }
    println!("cargo:rustc-env=PINNED_TURSO_REVISION={revision}");
    println!("cargo:rustc-env=TURSO_SOURCE_TREE={tree}");
    println!("cargo:rustc-env=TURSO_PATCH_DIFF_SHA256={diff_sha256}");
    println!("cargo:rustc-env=TURSO_PATCHED={}", !diff.is_empty());
    println!(
        "cargo:rustc-env=TURSO_PATCH_FILE_INVENTORY={}",
        file_inventory.join(";")
    );
    // Rerun the validation whenever anything in the validated checkout or the
    // submodule's checked-out revision changes, not only the crates Cargo builds.
    println!("cargo:rerun-if-changed={}", turso.display());
    let mut watched = vec![
        "HEAD".to_owned(),
        "index".to_owned(),
        "packed-refs".to_owned(),
    ];
    // A symbolic HEAD resolves through its branch ref, which can move alone.
    let head_ref = Command::new("git")
        .arg("-C")
        .arg(&turso)
        .args(["symbolic-ref", "-q", "HEAD"])
        .output()
        .expect("git is required to identify the pinned Turso source");
    if head_ref.status.success() {
        watched.push(text(head_ref.stdout));
    }
    for path in &watched {
        let git_path = text(git(
            &turso,
            None,
            &[
                "rev-parse",
                "--path-format=absolute",
                "--git-path",
                path.as_str(),
            ],
        ));
        println!("cargo:rerun-if-changed={git_path}");
    }
}
