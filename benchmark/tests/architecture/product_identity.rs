//! Gate 4: canonical product identity and rename-drift gate.
//!
//! This gate is deliberately Rust-native. It asks Git for tracked and
//! non-ignored untracked paths using NUL-delimited output, then scans every
//! bounded UTF-8 file unless its path is an explicitly declared binary or
//! vendored-generated asset class. Retired values are allowed only through
//! the fingerprinted registry in `docs/rename/legacy-name-allowlist.tsv`.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::process::Command;

use crate::fsx;

const MAX_FILE_BYTES: usize = 8 * 1024 * 1024;
const ALLOWLIST: &str = "docs/rename/legacy-name-allowlist.tsv";
const ALLOWLIST_HEADER: &str = "# path<TAB>expected-match-count<TAB>match-fingerprint-sha256<TAB>compatibility-class<TAB>reason";

const RULES: &[(&str, &[&str])] = &[
    ("retired-product-name", &["terra", "base", "v2"]),
    ("retired-short-prefix", &["tb", "v2"]),
    ("retired-lane-symbol", &["v2", "lane"]),
];

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
enum CompatibilityClass {
    Historical,
    Persisted,
    Policy,
    Wire,
}

impl CompatibilityClass {
    fn parse(value: &str) -> Result<Self, String> {
        match value {
            "historical" => Ok(Self::Historical),
            "persisted" => Ok(Self::Persisted),
            "policy" => Ok(Self::Policy),
            "wire" => Ok(Self::Wire),
            _ => Err(format!("unknown compatibility class `{value}`")),
        }
    }

    fn as_str(self) -> &'static str {
        match self {
            Self::Historical => "historical",
            Self::Persisted => "persisted",
            Self::Policy => "policy",
            Self::Wire => "wire",
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct Match {
    rule: &'static str,
    kind: &'static str,
    line: usize,
    column: usize,
    value: Vec<u8>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct Allowance {
    expected_matches: usize,
    fingerprint: String,
    class: CompatibilityClass,
    reason: String,
}

fn relative(path: &Path, root: &Path) -> String {
    path.strip_prefix(root).unwrap_or(path).display().to_string()
}

fn validate_relative_path(path: &str) -> Result<(), String> {
    if path.is_empty() || path.contains('\\') || Path::new(path).is_absolute() {
        return Err("path must be a non-empty repository-relative slash path".into());
    }
    if Path::new(path).components().any(|component| {
        matches!(component, Component::ParentDir | Component::RootDir | Component::Prefix(_))
    }) {
        return Err("path traversal or an absolute path is not allowed".into());
    }
    Ok(())
}

fn parse_allowlist(text: &str) -> Result<BTreeMap<String, Allowance>, String> {
    let mut allowances = BTreeMap::new();
    for (line_number, line) in text.lines().enumerate() {
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let fields: Vec<&str> = line.split('\t').collect();
        if fields.len() != 5 || fields.iter().any(|field| field.trim().is_empty()) {
            return Err(format!(
                "{ALLOWLIST}:{line_number}: expected path, count, fingerprint, class, and reason"
            ));
        }
        let path = fields[0].to_owned();
        validate_relative_path(&path)
            .map_err(|error| format!("{ALLOWLIST}:{line_number}: {error}"))?;
        let expected_matches = fields[1].parse::<usize>().map_err(|_| {
            format!("{ALLOWLIST}:{line_number}: match count must be a positive integer")
        })?;
        if expected_matches == 0 {
            return Err(format!("{ALLOWLIST}:{line_number}: match count must be positive"));
        }
        if fields[2].len() != 64
            || !fields[2].bytes().all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase())
        {
            return Err(format!(
                "{ALLOWLIST}:{line_number}: fingerprint must be lowercase SHA-256"
            ));
        }
        let class = CompatibilityClass::parse(fields[3])
            .map_err(|error| format!("{ALLOWLIST}:{line_number}: {error}"))?;
        let allowance = Allowance {
            expected_matches,
            fingerprint: fields[2].to_owned(),
            class,
            reason: fields[4].to_owned(),
        };
        if allowances.insert(path.clone(), allowance).is_some() {
            return Err(format!("{ALLOWLIST}:{line_number}: duplicate path `{path}`"));
        }
    }
    Ok(allowances)
}

fn git_paths(root: &Path, arguments: &[&str]) -> Result<Vec<String>, String> {
    let output = Command::new("git")
        .args(arguments)
        .current_dir(root)
        .output()
        .map_err(|error| format!("cannot execute Git for path inventory: {error}"))?;
    if !output.status.success() {
        return Err(format!(
            "Git path inventory failed: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    let mut paths = Vec::new();
    for raw in output.stdout.split(|byte| *byte == 0).filter(|raw| !raw.is_empty()) {
        let path = std::str::from_utf8(raw)
            .map_err(|_| "Git returned a non-UTF-8 repository path".to_owned())?;
        validate_relative_path(path)?;
        paths.push(path.to_owned());
    }
    paths.sort();
    paths.dedup();
    Ok(paths)
}

fn repository_paths(root: &Path) -> Result<Vec<String>, String> {
    let mut paths = git_paths(root, &["ls-files", "-z"])?;
    paths.extend(git_paths(root, &["ls-files", "-z", "--others", "--exclude-standard"])?);
    paths.sort();
    paths.dedup();
    Ok(paths)
}

fn ascii_lower(byte: u8) -> u8 {
    byte.to_ascii_lowercase()
}

fn separator(byte: u8) -> bool {
    matches!(byte, b' ' | b'\t' | b'.' | b'_' | b'-')
}

fn rule_match(line: &[u8], start: usize, parts: &[&str]) -> Option<usize> {
    let mut cursor = start;
    for (part_index, part) in parts.iter().enumerate() {
        if part_index != 0 {
            while cursor < line.len() && separator(line[cursor]) {
                cursor += 1;
            }
        }
        for expected in part.bytes() {
            if cursor >= line.len() || ascii_lower(line[cursor]) != expected {
                return None;
            }
            cursor += 1;
        }
    }
    Some(cursor)
}

fn collect_matches(value: &[u8], kind: &'static str, line_number: usize) -> Vec<Match> {
    let mut matches = Vec::new();
    for start in 0..value.len() {
        for &(rule, parts) in RULES {
            if let Some(end) = rule_match(value, start, parts) {
                matches.push(Match {
                    rule,
                    kind,
                    line: line_number,
                    column: start + 1,
                    value: value[start..end].to_vec(),
                });
            }
        }
    }
    matches.sort_by(|left, right| {
        (left.column, left.rule, &left.value).cmp(&(right.column, right.rule, &right.value))
    });
    matches
}

fn explicit_asset_class(path: &str) -> Option<&'static str> {
    const BINARY_EXTENSIONS: &[&str] = &[
        "7z", "a", "avif", "bin", "bmp", "bz2", "class", "dylib", "eot", "exe", "gif", "gz", "ico",
        "jpeg", "jpg", "mov", "mp3", "mp4", "otf", "pdf", "so", "tar", "tgz", "tif", "tiff", "ttf",
        "wasm", "webm", "webp", "woff", "woff2", "xz", "zip",
    ];
    let extension = Path::new(path).extension().and_then(|value| value.to_str());
    if extension
        .is_some_and(|value| BINARY_EXTENSIONS.contains(&value.to_ascii_lowercase().as_str()))
    {
        return Some("binary");
    }
    if path.starts_with("crates/terrabase-studio/static/monaco/") {
        return Some("vendored-generated");
    }
    None
}

fn scan_file(root: &Path, path: &str) -> Result<Vec<Match>, String> {
    let absolute = root.join(path);
    let mut matches = collect_matches(path.as_bytes(), "path", 0);
    if explicit_asset_class(path).is_some() {
        return Ok(matches);
    }
    let metadata = fs::symlink_metadata(&absolute)
        .map_err(|error| format!("{path}: cannot inspect file: {error}"))?;
    if !metadata.file_type().is_file() {
        return Err(format!("{path}: repository path is not a regular file"));
    }
    let data = fs::read(&absolute).map_err(|error| format!("{path}: cannot read file: {error}"))?;
    if data.len() > MAX_FILE_BYTES {
        return Err(format!("{path}: file exceeds the {MAX_FILE_BYTES}-byte scan limit"));
    }
    let text = String::from_utf8(data).map_err(|_| {
        format!("{path}: non-UTF-8 content requires an explicit binary or vendored-generated asset class")
    })?;
    for (line_number, line) in text.split('\n').enumerate() {
        matches.extend(collect_matches(line.as_bytes(), "text", line_number + 1));
    }
    matches.sort_by(|left, right| {
        (left.kind, left.line, left.column, left.rule, &left.value).cmp(&(
            right.kind,
            right.line,
            right.column,
            right.rule,
            &right.value,
        ))
    });
    Ok(matches)
}

fn sha256(input: &[u8]) -> [u8; 32] {
    const K: [u32; 64] = [
        0x428a_2f98,
        0x7137_4491,
        0xb5c0_fbcf,
        0xe9b5_dba5,
        0x3956_c25b,
        0x59f1_11f1,
        0x923f_82a4,
        0xab1c_5ed5,
        0xd807_aa98,
        0x1283_5b01,
        0x2431_85be,
        0x550c_7dc3,
        0x72be_5d74,
        0x80de_b1fe,
        0x9bdc_06a7,
        0xc19b_f174,
        0xe49b_69c1,
        0xefbe_4786,
        0x0fc1_9dc6,
        0x240c_a1cc,
        0x2de9_2c6f,
        0x4a74_84aa,
        0x5cb0_a9dc,
        0x76f9_88da,
        0x983e_5152,
        0xa831_c66d,
        0xb003_27c8,
        0xbf59_7fc7,
        0xc6e0_0bf3,
        0xd5a7_9147,
        0x06ca_6351,
        0x1429_2967,
        0x27b7_0a85,
        0x2e1b_2138,
        0x4d2c_6dfc,
        0x5338_0d13,
        0x650a_7354,
        0x766a_0abb,
        0x81c2_c92e,
        0x9272_2c85,
        0xa2bf_e8a1,
        0xa81a_664b,
        0xc24b_8b70,
        0xc76c_51a3,
        0xd192_e819,
        0xd699_0624,
        0xf40e_3585,
        0x106a_a070,
        0x19a4_c116,
        0x1e37_6c08,
        0x2748_774c,
        0x34b0_bcb5,
        0x391c_0cb3,
        0x4ed8_aa4a,
        0x5b9c_ca4f,
        0x682e_6ff3,
        0x748f_82ee,
        0x78a5_636f,
        0x84c8_7814,
        0x8cc7_0208,
        0x90be_fffa,
        0xa450_6ceb,
        0xbef9_a3f7,
        0xc671_78f2,
    ];
    let mut message = input.to_vec();
    let bit_length = (message.len() as u64) * 8;
    message.push(0x80);
    while message.len() % 64 != 56 {
        message.push(0);
    }
    message.extend_from_slice(&bit_length.to_be_bytes());
    let mut state: [u32; 8] = [
        0x6a09_e667,
        0xbb67_ae85,
        0x3c6e_f372,
        0xa54f_f53a,
        0x510e_527f,
        0x9b05_688c,
        0x1f83_d9ab,
        0x5be0_cd19,
    ];
    for chunk in message.chunks_exact(64) {
        let mut schedule = [0u32; 64];
        for (index, word) in schedule.iter_mut().take(16).enumerate() {
            let offset = index * 4;
            *word = u32::from_be_bytes(
                chunk[offset..offset + 4].try_into().expect("word is four bytes"),
            );
        }
        for index in 16..64 {
            let x = schedule[index - 15];
            let y = schedule[index - 2];
            let sigma_one = y.rotate_right(17) ^ y.rotate_right(19) ^ (y >> 10);
            let sigma_zero = x.rotate_right(7) ^ x.rotate_right(18) ^ (x >> 3);
            schedule[index] = sigma_one
                .wrapping_add(schedule[index - 7])
                .wrapping_add(sigma_zero)
                .wrapping_add(schedule[index - 16]);
        }
        let mut working = state;
        for index in 0..64 {
            let choose = (working[4] & working[5]) ^ (!working[4] & working[6]);
            let majority =
                (working[0] & working[1]) ^ (working[0] & working[2]) ^ (working[1] & working[2]);
            let temp1 = working[7]
                .wrapping_add(
                    working[4].rotate_right(6)
                        ^ working[4].rotate_right(11)
                        ^ working[4].rotate_right(25),
                )
                .wrapping_add(choose)
                .wrapping_add(K[index])
                .wrapping_add(schedule[index]);
            let temp2 = (working[0].rotate_right(2)
                ^ working[0].rotate_right(13)
                ^ working[0].rotate_right(22))
            .wrapping_add(majority);
            working[7] = working[6];
            working[6] = working[5];
            working[5] = working[4];
            working[4] = working[3].wrapping_add(temp1);
            working[3] = working[2];
            working[2] = working[1];
            working[1] = working[0];
            working[0] = temp1.wrapping_add(temp2);
        }
        for index in 0..8 {
            state[index] = state[index].wrapping_add(working[index]);
        }
    }
    let mut digest = [0u8; 32];
    for (index, word) in state.iter().enumerate() {
        digest[index * 4..index * 4 + 4].copy_from_slice(&word.to_be_bytes());
    }
    digest
}

fn fingerprint(path: &str, matches: &[Match]) -> String {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(b"terrabase.product-name-allowance/v1\0");
    bytes.extend_from_slice(path.as_bytes());
    bytes.push(0);
    for item in matches {
        bytes.extend_from_slice(item.rule.as_bytes());
        bytes.push(0);
        bytes.extend_from_slice(item.kind.as_bytes());
        bytes.push(0);
        bytes.extend_from_slice(item.line.to_string().as_bytes());
        bytes.push(0);
        bytes.extend_from_slice(item.column.to_string().as_bytes());
        bytes.push(0);
        bytes.extend_from_slice(&item.value);
        bytes.push(0);
    }
    sha256(&bytes).iter().map(|byte| format!("{byte:02x}")).collect()
}

fn describe_match(path: &str, item: &Match) -> String {
    format!(
        "{path}:{}:{} [{} {}: {:?}]",
        item.line,
        item.column,
        item.kind,
        item.rule,
        String::from_utf8_lossy(&item.value)
    )
}

fn scan_repository(root: &Path, allowlist_text: &str) -> Result<Vec<String>, String> {
    let allowances = parse_allowlist(allowlist_text)?;
    let paths = repository_paths(root)?;
    let path_set: std::collections::BTreeSet<&str> = paths.iter().map(String::as_str).collect();
    let mut violations = Vec::new();
    for path in &paths {
        let matches = scan_file(root, path)?;
        match allowances.get(path) {
            Some(allowance) if matches.is_empty() => violations.push(format!(
                "{ALLOWLIST}: stale allowance for {path}: no retired value remains (class {}, reason: {})",
                allowance.class.as_str(), allowance.reason
            )),
            Some(allowance) => {
                let actual = fingerprint(path, &matches);
                if matches.len() != allowance.expected_matches || actual != allowance.fingerprint {
                    violations.push(format!(
                        "{ALLOWLIST}: allowance mismatch for {path}: expected {} matches / {}, found {} / {}. Review exact token locations before updating the registry.",
                        allowance.expected_matches, allowance.fingerprint, matches.len(), actual
                    ));
                }
            }
            None => violations.extend(matches.iter().map(|item| format!(
                "{path}: unregistered retired value in {}. Add one exact allowance with a compatibility class and reason, or remove the value.",
                describe_match(path, item)
            ))),
        }
    }
    for (path, allowance) in &allowances {
        if !path_set.contains(path.as_str()) {
            violations.push(format!(
                "{ALLOWLIST}: stale allowance for {path}: path is missing or ignored (class {}, reason: {})",
                allowance.class.as_str(), allowance.reason
            ));
        }
    }
    Ok(violations)
}

#[test]
fn package_and_library_targets_use_the_canonical_identity() {
    let root = fsx::read_repo_file("Cargo.toml");
    assert_eq!(fsx::package_name(&root).as_deref(), Some("terrabase"));
    assert_eq!(fsx::library_name(&root).as_deref(), Some("terrabase"));
    for (path, manifest) in manifests() {
        let package = fsx::package_name(&manifest)
            .unwrap_or_else(|| panic!("{} has no package name", relative(&path, fsx::repo_root())));
        assert!(
            package == "terrabase" || package.starts_with("terrabase-"),
            "{} uses non-canonical package identity `{package}`",
            relative(&path, fsx::repo_root())
        );
        for identity in RETIRED_SOURCE_IDENTITIES {
            assert!(
                !manifest.contains(identity),
                "{} retains retired identity `{identity}`",
                relative(&path, fsx::repo_root())
            );
        }
        for dependency in fsx::dependency_keys(&manifest) {
            assert_ne!(
                dependency,
                concat!("terrabase", "_v2"),
                "{} retains the closed dependency alias",
                relative(&path, fsx::repo_root())
            );
            assert_ne!(
                dependency,
                concat!("terrabase", "-v2"),
                "{} retains the closed package identity",
                relative(&path, fsx::repo_root())
            );
        }
    }
}

fn manifests() -> Vec<(PathBuf, String)> {
    let root = fsx::repo_root();
    let mut paths = vec![root.join("Cargo.toml")];
    for entry in fs::read_dir(root.join("crates")).expect("crates directory is readable") {
        let path = entry.expect("crate entry is readable").path().join("Cargo.toml");
        if path.is_file() {
            paths.push(path);
        }
    }
    paths.push(root.join("bench/Cargo.toml"));
    paths.push(root.join("crates/terrabase-studio/Cargo.toml"));
    paths.sort();
    paths
        .into_iter()
        .map(|path| (path.clone(), fs::read_to_string(path).expect("manifest is readable")))
        .collect()
}

const RETIRED_SOURCE_IDENTITIES: &[&str] = &[
    concat!("terrabase", " v2"),
    concat!("terrabase", "_v2"),
    concat!("terrabase", "-v2"),
    concat!("terrabase", "v2"),
];

#[test]
fn repository_rename_scan_is_clean() {
    let allowlist = fsx::read_repo_file(ALLOWLIST);
    let violations = scan_repository(fsx::repo_root(), &allowlist).expect("rename scan completed");
    assert!(violations.is_empty(), "rename gate violations:\n{}", violations.join("\n"));
}

#[test]
fn generated_client_binding_stays_on_the_canonical_go_package() {
    let proto = fsx::read_repo_file("crates/terrabase-tbep/proto/terrabase/v1/engine.proto");
    assert!(proto.contains("package terrabase.v1;"), "TBEP package identity drifted");
    assert!(
        proto.contains("option go_package = \"terrascale.tech/terrabase-go/terrabase/v1;v1\";"),
        "TBEP Go binding target drifted from the terrabase-go client"
    );
    for path in
        ["terrabase-go/terrabase/v1/engine.pb.go", "terrabase-go/terrabase/v1/engine_grpc.pb.go"]
    {
        let binding = fsx::read_repo_file(path);
        assert!(binding.contains("package v1"), "{path} is not a v1 Go binding");
        assert!(
            !binding.contains(concat!("terrabase", "_v2")),
            "{path} retains the closed source alias"
        );
        assert!(
            !binding.contains(concat!("terrabase", "-v2")),
            "{path} retains the closed package identity"
        );
    }
}

#[cfg(test)]
mod hostile_fixtures {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT_FIXTURE: AtomicU64 = AtomicU64::new(0);

    struct Fixture {
        root: PathBuf,
    }
    impl Fixture {
        fn new() -> Self {
            let serial = NEXT_FIXTURE.fetch_add(1, Ordering::Relaxed);
            let root = std::env::temp_dir()
                .join(format!("terrabase-rename-{serial}-{}", std::process::id()));
            fs::create_dir(&root).expect("fixture directory is created");
            let status = Command::new("git")
                .args(["init", "--quiet"])
                .current_dir(&root)
                .status()
                .expect("git init runs");
            assert!(status.success());
            Self { root }
        }
        fn write(&self, path: &str, data: &[u8]) {
            let absolute = self.root.join(path);
            if let Some(parent) = absolute.parent() {
                fs::create_dir_all(parent).expect("fixture parent is created");
            }
            fs::write(absolute, data).expect("fixture file is written");
        }
        fn track(&self, path: &str) {
            let status = Command::new("git")
                .args(["add", "--", path])
                .current_dir(&self.root)
                .status()
                .expect("git add runs");
            assert!(status.success(), "git add failed for {path}");
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }

    fn allowance(path: &str, content: &str, class: &str) -> String {
        let matches = collect_matches(content.as_bytes(), "text", 1);
        format!(
            "{ALLOWLIST_HEADER}\n{path}\t{}\t{}\t{class}\tfixture\n",
            matches.len(),
            fingerprint(path, &matches)
        )
    }

    #[test]
    fn rejects_allowlist_path_traversal_and_duplicates() {
        let traversal =
            format!("{ALLOWLIST_HEADER}\n../outside\t1\t{}\thistorical\tfixture\n", "0".repeat(64));
        assert!(parse_allowlist(&traversal).is_err());
        let duplicate = format!(
            "{ALLOWLIST_HEADER}\nfile.txt\t1\t{}\thistorical\tfixture\nfile.txt\t1\t{}\thistorical\tfixture\n",
            "0".repeat(64),
            "0".repeat(64)
        );
        assert!(parse_allowlist(&duplicate).unwrap_err().contains("duplicate path"));
    }

    #[test]
    fn rejects_stale_allowances_and_accepts_untracked_detection() {
        let fixture = Fixture::new();
        fixture.write("tracked.txt", b"TerraBase");
        fixture.track("tracked.txt");
        let stale = format!(
            "{ALLOWLIST_HEADER}\nmissing.txt\t1\t{}\thistorical\tfixture\n",
            "0".repeat(64)
        );
        let report = scan_repository(&fixture.root, &stale).unwrap();
        assert!(report.iter().any(|line| line.contains("missing.txt")));
        let retired = ["terra", "base", "v2"].join("-");
        fixture.write("scratch.txt", retired.as_bytes());
        let report = scan_repository(&fixture.root, &format!("{ALLOWLIST_HEADER}\n")).unwrap();
        assert!(report.iter().any(|line| line.contains("scratch.txt")));
    }

    #[test]
    fn near_misses_do_not_trigger_and_explicit_binary_assets_are_skipped() {
        let fixture = Fixture::new();
        let near_misses = [
            "TerraBase".to_owned(),
            "v3".to_owned(),
            ["tb", "v3"].concat(),
            ["base", "v2"].concat(),
            ["terabse", "-v2"].concat(),
        ]
        .join(" ");
        fixture.write("near.txt", near_misses.as_bytes());
        fixture.track("near.txt");
        let binary_value = ["Terra", "Base", "V2"].concat();
        let binary = [b'\0'].into_iter().chain(binary_value.bytes()).collect::<Vec<_>>();
        fixture.write("payload.bin", &binary);
        fixture.track("payload.bin");
        let report = scan_repository(&fixture.root, &format!("{ALLOWLIST_HEADER}\n")).unwrap();
        assert!(report.is_empty(), "near-miss or binary false positive: {report:?}");
    }

    #[test]
    fn allowance_fingerprint_binds_exact_token_and_location() {
        let fixture = Fixture::new();
        let retired = ["terra", "base", "v2"].join("-");
        fixture.write("record.md", retired.as_bytes());
        fixture.track("record.md");
        let valid = allowance("record.md", &retired, "historical");
        assert!(scan_repository(&fixture.root, &valid).unwrap().is_empty());
        let changed = valid.replacen("record.md\t1\t", "record.md\t2\t", 1);
        assert!(
            scan_repository(&fixture.root, &changed)
                .unwrap()
                .iter()
                .any(|line| line.contains("allowance mismatch"))
        );
    }

    #[test]
    fn fingerprint_hash_implementation_matches_sha256() {
        let digest = sha256(b"abc");
        assert_eq!(
            digest.iter().map(|byte| format!("{byte:02x}")).collect::<String>(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }
}
