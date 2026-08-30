//! Shared manifest and source readers for the P8 architecture gates.
//!
//! The gates assert against the repository's committed text, so they read
//! manifests and sources as data instead of linking engine internals. The
//! Cargo-manifest readers intentionally understand only the TOML subset the
//! workspace manifests use (single-section tables, `key = value` entries,
//! and one bracketed `members` list); a manifest the readers cannot model
//! fails the gate that reads it, which is itself an architecture fact.

use std::fs;
use std::path::{Path, PathBuf};

/// Returns the repository root: the root package's manifest directory.
pub fn repo_root() -> &'static Path {
    Path::new(env!("CARGO_MANIFEST_DIR"))
}

/// Reads one repository-relative text file, naming the exact path on failure.
///
/// # Panics
///
/// Panics when the file cannot be read so the failing gate reports the path.
pub fn read_repo_file(relative: &str) -> String {
    let path = repo_root().join(relative);
    fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("architecture gate cannot read {}: {error}", path.display()))
}

/// Returns the text with full-line comments (`//`, `///`, `//!`) blanked so
/// registry parsers see code rather than prose. Line positions are kept.
pub fn strip_line_comments(text: &str) -> String {
    text.lines()
        .map(|line| if line.trim_start().starts_with("//") { "" } else { line })
        .collect::<Vec<_>>()
        .join("\n")
}

/// Recursively collects the `.rs` files under one repository-relative root,
/// skipping `target` directories, sorted for stable failure messages.
pub fn rust_files(relative_root: &str) -> Vec<PathBuf> {
    fn walk(dir: &Path, out: &mut Vec<PathBuf>) {
        let entries = fs::read_dir(dir)
            .unwrap_or_else(|error| panic!("cannot list {}: {error}", dir.display()));
        for entry in entries {
            let path = entry.expect("readable directory entry").path();
            if path.is_dir() {
                if path.file_name().and_then(|n| n.to_str()) != Some("target") {
                    walk(&path, out);
                }
            } else if path.extension().is_some_and(|extension| extension == "rs") {
                out.push(path);
            }
        }
    }
    let mut files = Vec::new();
    walk(&repo_root().join(relative_root), &mut files);
    files.sort();
    files
}

/// Returns the `key = value` entries of one exact TOML table, ignoring every
/// other table (`[dev-dependencies]`, `[build-dependencies]`, and
/// `[target.'cfg(...)'.dependencies]` never match the exact table name).
pub fn manifest_table_entries(manifest: &str, table: &str) -> Vec<(String, String)> {
    let mut entries = Vec::new();
    let mut section = String::new();
    for raw in manifest.lines() {
        let line = raw.trim();
        if line.starts_with('[') && line.ends_with(']') {
            section = line[1..line.len() - 1].trim().to_string();
            continue;
        }
        if section != table || line.is_empty() || line.starts_with('#') {
            continue;
        }
        if let Some((key, value)) = line.split_once('=') {
            entries.push((key.trim().to_string(), value.trim().to_string()));
        }
    }
    entries
}

/// Returns the dependency keys declared in the exact `[dependencies]` table.
pub fn dependency_keys(manifest: &str) -> Vec<String> {
    manifest_table_entries(manifest, "dependencies").into_iter().map(|(key, _)| key).collect()
}

/// Returns the package names targeted by the exact `[dependencies]` table.
///
/// Cargo permits a dependency key to retain an old source-level crate name
/// while its inline table points at a renamed package. Architecture checks
/// operate on package identities, so this resolves `package = "..."` when it
/// is present and otherwise returns the dependency key.
pub fn dependency_package_names(manifest: &str) -> Vec<String> {
    manifest_table_entries(manifest, "dependencies")
        .into_iter()
        .map(|(key, value)| inline_table_string(&value, "package").unwrap_or(key))
        .collect()
}

fn inline_table_string(value: &str, key: &str) -> Option<String> {
    let marker = format!("{key} =");
    let after = value.split_once(&marker)?.1.trim_start();
    let quoted = after.strip_prefix('"')?;
    let end = quoted.find('"')?;
    Some(quoted[..end].to_string())
}

/// Returns the `[package] name` of a manifest, when present.
pub fn package_name(manifest: &str) -> Option<String> {
    manifest_table_entries(manifest, "package")
        .into_iter()
        .find(|(key, _)| key == "name")
        .map(|(_, value)| value.trim_matches('"').to_string())
}

/// Returns the explicit `[lib] name`, when the package declares one.
pub fn library_name(manifest: &str) -> Option<String> {
    manifest_table_entries(manifest, "lib")
        .into_iter()
        .find(|(key, _)| key == "name")
        .map(|(_, value)| value.trim_matches('"').to_string())
}

/// Returns the `[workspace] members` paths of the root manifest.
pub fn workspace_members(root_manifest: &str) -> Vec<String> {
    let mut in_workspace = false;
    let mut collecting = false;
    let mut members = Vec::new();
    for raw in root_manifest.lines() {
        let line = raw.trim();
        if line.starts_with('[') && line.ends_with(']') {
            in_workspace = line[1..line.len() - 1].trim() == "workspace";
            collecting = false;
            continue;
        }
        if !in_workspace {
            continue;
        }
        let payload = if collecting {
            line
        } else if let Some(rest) = line.strip_prefix("members = [") {
            collecting = true;
            rest
        } else {
            continue;
        };
        members.extend(quoted_values(payload));
        if payload.contains(']') {
            break;
        }
    }
    members
}

/// Collects the quoted strings of one fragment up to its first `]`.
fn quoted_values(fragment: &str) -> Vec<String> {
    let mut values = Vec::new();
    let mut chars = fragment.chars().peekable();
    while let Some(&first) = chars.peek() {
        if first == '"' {
            chars.next();
            let mut value = String::new();
            for ch in chars.by_ref() {
                if ch == '"' {
                    break;
                }
                value.push(ch);
            }
            values.push(value);
        } else if first == ']' {
            break;
        } else {
            chars.next();
        }
    }
    values
}
