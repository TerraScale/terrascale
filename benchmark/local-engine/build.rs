use std::env;
use std::io::Write as _;
use std::os::unix::fs::PermissionsExt as _;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use sha2::{Digest, Sha256};

const PINNED_TURSO: &str = "8549c16595d2faf1bdd6ee24aee0be8bfabb3d4a";
// Git tree of the pinned gitlink plus TerraBase's ordered patches/turso series,
// as printed by TerraBase's `scripts/ci/prepare-turso.sh --tree`.
const PINNED_TURSO_TREE: &str = "4da45c6e8290634cff70f371d123b0564206e4b0";

/// Git reading only the Turso repository's own configuration: system and
/// global settings (attributes files, filters, EOL policy) cannot change how
/// the checkout is identified.
fn isolated_git(turso: &Path) -> Command {
    let mut command = Command::new("git");
    command
        .env("GIT_CONFIG_NOSYSTEM", "1")
        .env("GIT_CONFIG_GLOBAL", "/dev/null")
        .arg("-C")
        .arg(turso);
    command
}

fn git(turso: &Path, index: Option<&Path>, args: &[&str]) -> Vec<u8> {
    let mut command = isolated_git(turso);
    command.args(args);
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

fn git_input(turso: &Path, index: Option<&Path>, args: &[&str], input: &[u8]) -> Vec<u8> {
    let mut command = isolated_git(turso);
    command
        .args(args)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(index) = index {
        command.env("GIT_INDEX_FILE", index);
    }
    let mut child = command
        .spawn()
        .expect("git is required to identify the pinned Turso source");
    let mut stdin = child.stdin.take().expect("git stdin");
    let input = input.to_vec();
    let writer = std::thread::spawn(move || stdin.write_all(&input));
    let output = child.wait_with_output().expect("wait for git");
    writer
        .join()
        .expect("git stdin writer")
        .expect("write git stdin");
    assert!(
        output.status.success(),
        "cannot identify Turso source: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    output.stdout
}

/// Refuse repository-local attribute and filter settings that could map
/// edited bytes back onto pinned blobs.
fn assert_hash_policy(turso: &Path) {
    let common_dir = PathBuf::from(text(git(
        turso,
        None,
        &["rev-parse", "--path-format=absolute", "--git-common-dir"],
    )));
    let info_attributes = common_dir.join("info/attributes");
    assert!(
        std::fs::read(&info_attributes).map_or(true, |bytes| bytes
            .split(|byte| *byte == b'\n')
            .all(|line| line.trim_ascii().is_empty() || line.trim_ascii().starts_with(b"#"))),
        "Turso info/attributes would change how the checkout is hashed"
    );
    let filters = isolated_git(turso)
        .args(["config", "--get-regexp", r"^filter\."])
        .output()
        .expect("git is required to identify the pinned Turso source");
    assert!(
        filters.stdout.is_empty(),
        "a configured git filter driver would change how the Turso checkout is hashed"
    );
}

/// Stage the working-tree bytes of every tracked and untracked (non-ignored)
/// path into `index` under an explicit normalization policy: only the pinned
/// tree's own `.gitattributes` apply (upstream checks a few `.bat` files out
/// with CRLF). Global, system and `info/attributes` sources and every
/// autocrlf/eol setting are overridden, and a configured clean filter is
/// refused, so nothing outside the pinned tree can map edited bytes back onto
/// pinned blobs.
fn stage_raw_worktree(turso: &Path, index: &Path) {
    assert_hash_policy(turso);
    let listed = git(
        turso,
        Some(index),
        &[
            "ls-files",
            "-z",
            "--cached",
            "--others",
            "--exclude-standard",
        ],
    );
    let mut entries: Vec<(String, &'static str)> = Vec::new();
    let mut removed = Vec::new();
    let mut symlinks = Vec::new();
    for name in listed
        .split(|byte| *byte == 0)
        .filter(|name| !name.is_empty())
    {
        let name = String::from_utf8(name.to_vec()).expect("Turso path is UTF-8");
        match std::fs::symlink_metadata(turso.join(&name)) {
            Err(_) => removed.push(name),
            Ok(meta) if meta.file_type().is_symlink() => symlinks.push(name),
            // A nested repository keeps its recorded gitlink.
            Ok(meta) if meta.is_dir() => {}
            Ok(meta) => {
                let mode = if meta.permissions().mode() & 0o111 == 0 {
                    "100644"
                } else {
                    "100755"
                };
                entries.push((name, mode));
            }
        }
    }
    let paths: Vec<u8> = entries
        .iter()
        .flat_map(|(name, _)| format!("{name}\n").into_bytes())
        .collect();
    let ids = git_input(
        turso,
        None,
        &[
            "-c",
            "core.attributesFile=/dev/null",
            "-c",
            "core.autocrlf=false",
            "-c",
            "core.eol=lf",
            "-c",
            "core.safecrlf=false",
            "hash-object",
            "-w",
            "--stdin-paths",
        ],
        &paths,
    );
    let ids = String::from_utf8(ids).expect("blob ids are UTF-8");
    let ids: Vec<&str> = ids.lines().collect();
    assert_eq!(ids.len(), entries.len(), "one blob id per Turso file");
    let mut info = Vec::new();
    for ((name, mode), id) in entries.iter().zip(ids) {
        info.extend(format!("{mode} {id}\t{name}\0").into_bytes());
    }
    for name in symlinks {
        let target = std::fs::read_link(turso.join(&name)).expect("read Turso symlink");
        let target = target.to_str().expect("Turso symlink target is UTF-8");
        let id = text(git_input(
            turso,
            None,
            &["hash-object", "-w", "--no-filters", "--stdin"],
            target.as_bytes(),
        ));
        info.extend(format!("120000 {id}\t{name}\0").into_bytes());
    }
    for name in removed {
        info.extend(format!("0 {}\t{name}\0", "0".repeat(40)).into_bytes());
    }
    git_input(
        turso,
        Some(index),
        &["update-index", "-z", "--index-info"],
        &info,
    );
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
    // Hash the raw bytes of the whole checkout, including files the patch
    // series adds, through a fresh scratch index built from HEAD. Copying the
    // submodule's own index would inherit assume-unchanged/skip-worktree flags,
    // and `git add` would apply clean filters and EOL normalization.
    let index = Path::new(&env::var("OUT_DIR").expect("Cargo out directory")).join("turso-index");
    let _ = std::fs::remove_file(&index);
    git(&turso, Some(&index), &["read-tree", "HEAD"]);
    stage_raw_worktree(&turso, &index);
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
    // Watch the metadata that decides the checked-out revision. Optional
    // files are watched only when present (a missing path would rerun every
    // build); the refs directory covers loose branch refs appearing, moving or
    // being packed away.
    let git_dir = PathBuf::from(text(git(
        &turso,
        None,
        &["rev-parse", "--path-format=absolute", "--git-dir"],
    )));
    // Branch refs and packed-refs live in the common directory, which differs
    // from the per-worktree HEAD/index directory in a linked worktree.
    let common_dir = PathBuf::from(text(git(
        &turso,
        None,
        &["rev-parse", "--path-format=absolute", "--git-common-dir"],
    )));
    let mut watched = vec![git_dir.join("HEAD"), common_dir.join("refs")];
    let head_ref = isolated_git(&turso)
        .args(["symbolic-ref", "-q", "HEAD"])
        .output()
        .expect("git is required to identify the pinned Turso source");
    if head_ref.status.success() {
        watched.push(common_dir.join(text(head_ref.stdout)));
    }
    watched.extend([git_dir.join("index"), common_dir.join("packed-refs")]);
    for path in watched.iter().filter(|path| path.exists()) {
        println!("cargo:rerun-if-changed={}", path.display());
    }
}
