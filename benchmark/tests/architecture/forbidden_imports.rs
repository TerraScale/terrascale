//! Gate 2 — forbidden-import and forbidden-central-dispatch scan (GOAL.md
//! P8: "Run ... forbidden-central-dispatch architecture gates").
//!
//! Rules enforced mechanically today, each with its authority:
//!
//! 1. Product sources may only reference workspace crates they declare as a
//!    dependency (GOAL.md 'Dependency direction': "Repository checks reject
//!    dependency cycles, forbidden imports, reverse-layer references" — a
//!    crate must not reach a sibling crate's internals across a boundary it
//!    never declared).
//! 2. No catch-all `util`/`common`/`manager`-style module may appear in
//!    product code (GOAL.md 'Maintainable and extensible engine
//!    architecture': repository checks "reject ... duplicate ownership" and
//!    the P8 forbidden-central-dispatch gate; the frozen name list below is
//!    the mechanical proxy for catch-all module ownership).
//! 3. No unsafe code outside an audited boundary (GOAL.md 'Dependency
//!    direction': "Unsafe code remains denied unless a separately accepted
//!    and audited record names the exact boundary").
//! 4. Every workspace crate denies unsafe code by lint or attribute, with
//!    any gap recorded in the reviewed list below.
//! 5. The engine core, native codecs, sync core, and replication core are Loro-free (AGENTS.md
//!    'Replication and sync rules' and GOAL.md AC-3: "No product crate or
//!    supported codec depends on Loro").
//!
//! Kept honest: feature-specific branches inside stable core dispatch and
//! public-module-surface drift are not mechanically detectable by this scan
//! yet and ride their own gates.

use std::fs;
use std::path::Path;

use crate::fsx;

/// One product crate of the workspace: its package name, the manifest that
/// declares it, and the source root scanned by this gate.
struct CrateRoot {
    name: String,
    crate_name: String,
    manifest: String,
    sources: String,
}

/// One reviewed exception to the repository-wide unsafe-code ban.
///
/// The digest binds the exception to one generated artifact. Regeneration,
/// a compiler upgrade, or a hand edit changes the digest and requires a new
/// audit before this gate passes again.
struct AuditedUnsafeBoundary {
    path: &'static str,
    audit_record: &'static str,
    blake3: &'static str,
}

const AUDITED_UNSAFE_BOUNDARIES: &[AuditedUnsafeBoundary] = &[AuditedUnsafeBoundary {
    path: "crates/terrabase-tbep/src/generated/fbs/tbep_generated.rs",
    audit_record: "docs/security/flatbuffers-generated-rust-unsafe-audit.md",
    blake3: "7f6a07d18ef1206629450c0e0a464e86dae12fe93b1d19e8fc83a48b039c0024",
}];

fn crate_roots() -> Vec<CrateRoot> {
    let root_manifest = fsx::read_repo_file("Cargo.toml");
    let root_package = fsx::package_name(&root_manifest).expect("root package name");
    let mut roots = vec![CrateRoot {
        crate_name: fsx::library_name(&root_manifest)
            .unwrap_or_else(|| hyphen_to_underscore(&root_package)),
        name: root_package,
        manifest: "Cargo.toml".to_string(),
        sources: "src".to_string(),
    }];
    for member in fsx::workspace_members(&root_manifest) {
        let manifest = fsx::read_repo_file(&format!("{member}/Cargo.toml"));
        let package_name = fsx::package_name(&manifest)
            .unwrap_or_else(|| panic!("member {member} declares no package name"));
        roots.push(CrateRoot {
            crate_name: fsx::library_name(&manifest)
                .unwrap_or_else(|| hyphen_to_underscore(&package_name)),
            name: package_name,
            manifest: format!("{member}/Cargo.toml"),
            sources: format!("{member}/src"),
        });
    }
    roots
}

fn hyphen_to_underscore(name: &str) -> String {
    name.replace('-', "_")
}

fn is_ident_byte(byte: u8) -> bool {
    byte.is_ascii_alphanumeric() || byte == b'_'
}

/// Collects the workspace-crate path references (`terrabase_x::...`) of one
/// text, with identifier boundaries respected.
fn crate_path_references(text: &str) -> Vec<String> {
    let bytes = text.as_bytes();
    let mut references = Vec::new();
    let mut cursor = 0;
    while let Some(found) = text[cursor..].find("terrabase_") {
        let start = cursor + found;
        let mut end = start + "terrabase_".len();
        while end < bytes.len() && is_ident_byte(bytes[end]) {
            end += 1;
        }
        let boundary_ok = start == 0 || !is_ident_byte(bytes[start - 1]);
        let mut probe = end;
        while probe < bytes.len() && bytes[probe].is_ascii_whitespace() {
            probe += 1;
        }
        if boundary_ok && text[probe..].starts_with("::") {
            references.push(text[start..end].to_string());
        }
        cursor = end;
    }
    references
}

#[test]
fn product_sources_reference_only_declared_workspace_crates() {
    for root in crate_roots() {
        let manifest = fsx::read_repo_file(&root.manifest);
        let mut allowed: Vec<String> = fsx::dependency_keys(&manifest)
            .into_iter()
            .filter(|key| key.starts_with("terrabase"))
            .map(|key| hyphen_to_underscore(&key))
            .collect();
        allowed.push(root.crate_name.clone());
        for file in fsx::rust_files(&root.sources) {
            let text = fs::read_to_string(&file)
                .unwrap_or_else(|error| panic!("cannot read {}: {error}", file.display()));
            let undeclared: Vec<String> = {
                let stripped = fsx::strip_line_comments(&text);
                let references = crate_path_references(&stripped);
                references
                    .iter()
                    .filter(|reference| !allowed.contains(reference))
                    .cloned()
                    .collect()
            };
            assert!(
                undeclared.is_empty(),
                "{} references workspace crates {undeclared:?} that {} does not declare as \
                 dependencies (allowed: {allowed:?}); cross-boundary imports of sibling \
                 crates are forbidden (GOAL 'Dependency direction')",
                file.display(),
                root.name,
            );
        }
    }
}

/// The frozen catch-all module-name list. Adding a name requires a review
/// note; the list is the mechanical proxy for "catch-all util/common/manager
/// module ownership" named by the P8 forbidden-central-dispatch gate.
const CATCH_ALL_MODULE_NAMES: &[&str] =
    &["util", "utils", "common", "manager", "managers", "misc", "helpers", "shared", "globals"];

fn declared_module_name(line: &str) -> Option<&str> {
    let mut trimmed = line.trim();
    for prefix in ["pub(crate) ", "pub(super) ", "pub "] {
        trimmed = trimmed.strip_prefix(prefix).unwrap_or(trimmed);
    }
    let rest = trimmed.strip_prefix("mod ")?;
    let name = rest
        .split(|character: char| !(character.is_alphanumeric() || character == '_'))
        .next()
        .unwrap_or_default();
    (!name.is_empty()).then_some(name)
}

#[test]
fn no_catch_all_module_ownership() {
    for root in crate_roots() {
        for file in fsx::rust_files(&root.sources) {
            let relative = file
                .strip_prefix(fsx::repo_root())
                .unwrap_or(file.as_path())
                .to_string_lossy()
                .into_owned();
            for component in Path::new(&relative).components() {
                let name = component.as_os_str().to_string_lossy();
                let stem = name.strip_suffix(".rs").unwrap_or(&name);
                assert!(
                    !CATCH_ALL_MODULE_NAMES.contains(&stem),
                    "{relative} places ownership in the catch-all module `{stem}`, which the \
                     forbidden-central-dispatch gate rejects; give the module a name that \
                     states what it owns",
                );
            }
            let text = fs::read_to_string(&file)
                .unwrap_or_else(|error| panic!("cannot read {}: {error}", file.display()));
            for line in fsx::strip_line_comments(&text).lines() {
                if let Some(name) = declared_module_name(line) {
                    assert!(
                        !CATCH_ALL_MODULE_NAMES.contains(&name),
                        "{relative} declares catch-all module `{name}` \
                         (line `{line}`); the forbidden-central-dispatch gate rejects it",
                    );
                }
            }
        }
    }
}

/// Collects the unsafe usages (`unsafe fn`, `unsafe {`, `unsafe impl`,
/// `unsafe trait`) of one text.
fn unsafe_usages(text: &str) -> Vec<String> {
    let bytes = text.as_bytes();
    let mut usages = Vec::new();
    let mut cursor = 0;
    while let Some(found) = text[cursor..].find("unsafe") {
        let start = cursor + found;
        let end = start + "unsafe".len();
        let boundary_ok = (start == 0 || !is_ident_byte(bytes[start - 1]))
            && (end == bytes.len() || !is_ident_byte(bytes[end]));
        if boundary_ok {
            let mut probe = end;
            while probe < bytes.len() && bytes[probe].is_ascii_whitespace() {
                probe += 1;
            }
            let rest = &text[probe..];
            let is_usage = rest.starts_with("fn ")
                || rest.starts_with("impl ")
                || rest.starts_with("trait ")
                || rest.starts_with('{');
            if is_usage {
                let snippet_end = (probe + 24).min(text.len());
                usages.push(text[start..snippet_end].replace(['\n', '\t'], " "));
            }
        }
        cursor = end;
    }
    usages
}

fn repository_relative(path: &Path) -> String {
    path.strip_prefix(fsx::repo_root()).unwrap_or(path).to_string_lossy().replace('\\', "/")
}

#[test]
fn no_unsafe_code_outside_audited_boundaries() {
    let mut observed_boundaries = Vec::new();
    for root in crate_roots() {
        for file in fsx::rust_files(&root.sources) {
            let text = fs::read_to_string(&file)
                .unwrap_or_else(|error| panic!("cannot read {}: {error}", file.display()));
            let usages = unsafe_usages(&fsx::strip_line_comments(&text));
            if usages.is_empty() {
                continue;
            }

            let relative = repository_relative(&file);
            let boundary = AUDITED_UNSAFE_BOUNDARIES
                .iter()
                .find(|boundary| boundary.path == relative)
                .unwrap_or_else(|| {
                    panic!(
                        "{} contains unsafe usages {usages:?}; GOAL 'Dependency direction' \
                         denies unsafe code unless a separately accepted and audited record \
                         names the exact boundary",
                        file.display(),
                    )
                });

            assert!(
                text.starts_with(
                    "// automatically generated by the FlatBuffers compiler, do not modify\n\
                     // @generated\n"
                ),
                "{} is allowlisted only as pinned FlatBuffers-generated code, but its generated \
                 header is absent",
                boundary.path,
            );
            let actual_digest = blake3::hash(text.as_bytes()).to_hex().to_string();
            assert_eq!(
                actual_digest, boundary.blake3,
                "{} changed after its unsafe audit; regenerate deterministically, review the \
                 unsafe output, and update the separate audit record and bound digest",
                boundary.path,
            );

            let audit = fsx::read_repo_file(boundary.audit_record);
            for required in [boundary.path, boundary.blake3, "flatc 25.12.19"] {
                assert!(
                    audit.contains(required),
                    "{} does not bind the audited unsafe boundary to `{required}`",
                    boundary.audit_record,
                );
            }
            observed_boundaries.push(relative);
        }
    }

    for boundary in AUDITED_UNSAFE_BOUNDARIES {
        assert!(
            observed_boundaries.iter().any(|path| path == boundary.path),
            "{} is a stale unsafe-boundary exception; remove it and its audit entry",
            boundary.path,
        );
    }

    let receiver = fsx::read_repo_file("crates/terrabase-tbep/tools/tbep-fbs/src/lib.rs");
    for unchecked in
        ["root_as_tbep_envelope_unchecked(", "size_prefixed_root_as_tbep_envelope_unchecked("]
    {
        assert!(
            !fsx::strip_line_comments(&receiver).contains(unchecked),
            "the hand-written FlatBuffers receiver calls generated unchecked entry point \
             `{unchecked}`; network bytes must pass the bounded verifier",
        );
    }
}

/// Crates that neither carry the `[lints.rust] unsafe_code = "deny"` table
/// nor a `#![deny(unsafe_code)]` attribute today. Closing a gap removes its
/// entry here (review note); adding a gap is never allowed.
const KNOWN_UNSAFE_LINT_GAPS: &[&str] = &["terrabase-cluster"];

#[test]
fn unsafe_denial_lints_cover_workspace_crates() {
    for root in crate_roots() {
        let manifest = fsx::read_repo_file(&root.manifest);
        let linted = fsx::manifest_table_entries(&manifest, "lints.rust")
            .iter()
            .any(|(key, value)| key == "unsafe_code" && value.contains("deny"));
        let lib_path = Path::new(fsx::repo_root()).join(&root.sources).join("lib.rs");
        let attributed = fs::read_to_string(&lib_path)
            .map(|lib| lib.contains("#![deny(unsafe_code)]"))
            .unwrap_or(false);
        if !linted && !attributed {
            assert!(
                KNOWN_UNSAFE_LINT_GAPS.contains(&root.name.as_str()),
                "{} neither denies unsafe code by `[lints.rust]` nor in its lib.rs; add the \
                 denial (or record the gap with a review note)",
                root.name,
            );
        }
    }
}

/// The scanned roots of the Loro-free rule. Sync and replication joined this
/// gate after their native causal migration landed on 2026-08-29.
const LORO_FREE_ROOTS: &[&str] = &[
    "src",
    "crates/terrabase-causal/src",
    "crates/terrabase-crdt/src",
    "crates/terrabase-sync-core/src",
    "crates/terrabase-replication-core/src",
];

#[test]
fn engine_core_and_native_codecs_are_loro_free() {
    for root in LORO_FREE_ROOTS {
        for file in fsx::rust_files(root) {
            let text = fs::read_to_string(&file)
                .unwrap_or_else(|error| panic!("cannot read {}: {error}", file.display()));
            let lowered = fsx::strip_line_comments(&text).to_lowercase();
            let mut start = 0;
            while let Some(found) = lowered[start..].find("loro") {
                let hit = start + found;
                let bytes = lowered.as_bytes();
                let boundary_ok = (hit == 0 || !is_ident_byte(bytes[hit - 1]))
                    && (hit + 4 >= bytes.len() || !is_ident_byte(bytes[hit + 4]));
                assert!(
                    !boundary_ok,
                    "{} references Loro at byte {hit}; native engine, sync, and replication code must \
                     stay Loro-free (AGENTS 'Replication and sync rules', GOAL AC-3)",
                    file.display(),
                );
                start = hit + 4;
            }
        }
    }
}
