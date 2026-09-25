//! Bounded verification of the official Firecracker release asset.
//!
//! GitHub's release API supplies an unsigned archive digest and size. These
//! bytes are candidate provenance, not a signature or source-build proof.

use std::collections::BTreeSet;
use std::error::Error;
use std::fs;
use std::io::{self, Cursor, Read, Write};
use std::os::unix::fs::PermissionsExt;
use std::path::{Component, Path};

use flate2::read::GzDecoder;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tar::Archive;

use crate::manifest::SourceArtifactReceipt;
use crate::run_dir::RunDirectory;

const RELEASE_URL: &str = "https://github.com/firecracker-microvm/firecracker/releases/download/v1.17.0/firecracker-v1.17.0-x86_64.tgz";
const RELEASE_SIZE: u64 = 7_464_385;
const RELEASE_SHA256: &str = "06094a1108ae9e82aa4c23a775aa92758f53f1175d422270d9d6162cb9ade558";
const METADATA_SOURCE: &str = "github-release-assets-api";
const MAX_TAR_BYTES: u64 = 256 * 1024 * 1024;
const MAX_MEMBERS: usize = 256;
const MAX_MEMBER_BYTES: u64 = 128 * 1024 * 1024;
const MAX_PATH_BYTES: usize = 512;
const OUTPUT_NAME: &str = "firecracker-release-executable";

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct FirecrackerReleaseEvidence {
    pub archive: SourceArtifactReceipt,
    pub executable_member: String,
    pub executable_path: String,
    pub executable_size_bytes: u64,
    pub executable_sha256: String,
    pub archive_metadata_source: String,
}

#[derive(Clone, Debug)]
struct MemberIdentity {
    size_bytes: u64,
    sha256: String,
}

fn invalid(message: impl Into<String>) -> Box<dyn Error> {
    io::Error::other(message.into()).into()
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn hash_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

/// Verify the exact official archive, reject unsafe physical tar records, and
/// install only the named regular ELF member into a private run directory.
/// `None` for the executable hash is only for candidate observation; a reviewed
/// caller must supply the reviewed hash and the same member name.
///
/// # Errors
///
/// Fails if the release byte identity, archive format, member set, ELF header,
/// or reviewed executable hash differs from its pin. No output is installed on
/// any validation failure.
#[allow(clippy::too_many_arguments)] // The independent archive and member pins are an explicit trust boundary.
pub(crate) fn inspect_firecracker_release_archive(
    archive_path: &Path,
    archive_url: &str,
    expected_archive_size_bytes: u64,
    expected_archive_sha256: &str,
    expected_member: &str,
    expected_executable_sha256: Option<&str>,
    run: &RunDirectory,
) -> Result<FirecrackerReleaseEvidence, Box<dyn Error>> {
    if archive_url != RELEASE_URL
        || expected_archive_size_bytes != RELEASE_SIZE
        || !expected_archive_sha256.eq_ignore_ascii_case(RELEASE_SHA256)
    {
        return Err(invalid(
            "Firecracker release archive pin differs from official asset metadata",
        ));
    }
    safe_member_path(expected_member.as_bytes(), false)?;
    if let Some(expected) = expected_executable_sha256 {
        if !is_sha256(expected) {
            return Err(invalid(
                "reviewed Firecracker executable SHA-256 is invalid",
            ));
        }
    }
    let metadata = fs::symlink_metadata(archive_path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() != RELEASE_SIZE {
        return Err(invalid(
            "Firecracker release archive is not a regular file of its pinned size",
        ));
    }
    let supplied_relative = archive_path
        .strip_prefix(run.path())
        .map_err(|_| invalid("Firecracker release archive is outside its private run"))?;
    let mut current = run.path().to_path_buf();
    for component in supplied_relative.components() {
        let Component::Normal(part) = component else {
            return Err(invalid("Firecracker release archive path is unsafe"));
        };
        current.push(part);
        if fs::symlink_metadata(&current)?.file_type().is_symlink() {
            return Err(invalid(
                "Firecracker release archive path traverses a symlink",
            ));
        }
    }
    let canonical_path = fs::canonicalize(archive_path)?;
    let relative = canonical_path
        .strip_prefix(run.path())
        .map_err(|_| invalid("Firecracker release archive is outside its private run"))?;
    if !relative
        .components()
        .all(|component| matches!(component, Component::Normal(_)))
    {
        return Err(invalid("Firecracker release archive path is unsafe"));
    }
    let mut compressed = Vec::with_capacity(usize::try_from(RELEASE_SIZE)?);
    let mut input = fs::File::open(&canonical_path)?.take(RELEASE_SIZE + 1);
    input.read_to_end(&mut compressed)?;
    if compressed.len() as u64 != RELEASE_SIZE
        || !hash_bytes(&compressed).eq_ignore_ascii_case(expected_archive_sha256)
    {
        return Err(invalid(
            "Firecracker release archive SHA-256 or size differs from pin",
        ));
    }

    // The immutable in-memory bytes are scanned in full before a destination
    // is created, then streamed a second time to install exactly one member.
    let inspected = scan_archive(&compressed, expected_member)?;
    if let Some(expected) = expected_executable_sha256 {
        if !inspected.sha256.eq_ignore_ascii_case(expected) {
            return Err(invalid(
                "Firecracker executable differs from reviewed SHA-256",
            ));
        }
    }
    let output_path = extract_member(&compressed, expected_member, &inspected, run)?;
    Ok(FirecrackerReleaseEvidence {
        archive: SourceArtifactReceipt {
            source_url: archive_url.to_owned(),
            path: relative.to_string_lossy().into_owned(),
            size_bytes: RELEASE_SIZE,
            sha256: RELEASE_SHA256.to_owned(),
        },
        executable_member: expected_member.to_owned(),
        executable_path: output_path.display().to_string(),
        executable_size_bytes: inspected.size_bytes,
        executable_sha256: inspected.sha256,
        archive_metadata_source: METADATA_SOURCE.to_owned(),
    })
}

/// Keep the gzip decoder and tar reader below a fixed expanded-byte budget.
struct LimitedReader<R> {
    inner: R,
    remaining: u64,
}

impl<R: Read> Read for LimitedReader<R> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if buffer.is_empty() {
            return Ok(0);
        }
        if self.remaining == 0 {
            let mut probe = [0_u8; 1];
            return if self.inner.read(&mut probe)? == 0 {
                Ok(0)
            } else {
                Err(io::Error::other(
                    "Firecracker tar expansion exceeds its byte limit",
                ))
            };
        }
        let permitted = buffer
            .len()
            .min(usize::try_from(self.remaining).unwrap_or(usize::MAX));
        let count = self.inner.read(&mut buffer[..permitted])?;
        self.remaining -= count as u64;
        Ok(count)
    }
}

fn archive_reader(bytes: &[u8]) -> Archive<LimitedReader<GzDecoder<Cursor<&[u8]>>>> {
    Archive::new(LimitedReader {
        inner: GzDecoder::new(Cursor::new(bytes)),
        remaining: MAX_TAR_BYTES,
    })
}

fn safe_member_path(raw: &[u8], directory: bool) -> Result<(String, String), Box<dyn Error>> {
    if raw.is_empty() || raw.len() > MAX_PATH_BYTES {
        return Err(invalid("Firecracker tar member path is empty or too long"));
    }
    let source = std::str::from_utf8(raw)?;
    if source.contains('\\')
        || source.contains('\0')
        || source.starts_with('/')
        || source.contains("//")
    {
        return Err(invalid("Firecracker tar member path is unsafe"));
    }
    if directory && matches!(source, "." | "./") {
        return Ok((source.to_owned(), ".".to_owned()));
    }
    let stripped = source.strip_prefix("./").unwrap_or(source);
    let normalized = if directory {
        stripped.trim_end_matches('/')
    } else {
        stripped
    };
    if normalized.is_empty()
        || normalized.starts_with("./")
        || normalized.ends_with('/')
        || normalized
            .split('/')
            .any(|part| part.is_empty() || part == "." || part == "..")
        || !Path::new(normalized)
            .components()
            .all(|part| matches!(part, Component::Normal(_)))
    {
        return Err(invalid(
            "Firecracker tar member path has traversal or ambiguous components",
        ));
    }
    Ok((source.to_owned(), normalized.to_owned()))
}

fn scan_archive(bytes: &[u8], expected_member: &str) -> Result<MemberIdentity, Box<dyn Error>> {
    let mut archive = archive_reader(bytes);
    let mut entries = archive.entries()?.raw(true);
    let mut seen = BTreeSet::new();
    let mut requested = None;
    let mut count = 0_usize;
    let mut declared_bytes = 0_u64;
    for next in entries.by_ref() {
        let mut entry = next?;
        count += 1;
        if count > MAX_MEMBERS {
            return Err(invalid("Firecracker tar has too many physical members"));
        }
        let kind = entry.header().entry_type().as_byte();
        let directory = kind == b'5';
        if !matches!(kind, b'0' | 0 | b'5') {
            return Err(invalid(
                "Firecracker tar contains an extension, link, or special member",
            ));
        }
        if entry.link_name_bytes().is_some() {
            return Err(invalid(
                "Firecracker tar member carries an unexpected link target",
            ));
        }
        let (raw_path, normalized) = safe_member_path(&entry.path_bytes(), directory)?;
        if !seen.insert(normalized) {
            return Err(invalid("Firecracker tar repeats a member path"));
        }
        let size = entry.size();
        if size > MAX_MEMBER_BYTES || (directory && size != 0) {
            return Err(invalid("Firecracker tar member is oversized or malformed"));
        }
        declared_bytes = declared_bytes
            .checked_add(size)
            .ok_or_else(|| invalid("Firecracker tar member lengths overflow"))?;
        if declared_bytes > MAX_TAR_BYTES {
            return Err(invalid("Firecracker tar declares too many payload bytes"));
        }
        if raw_path == expected_member {
            if kind != b'0' || entry.header().mode()? != 0o755 || size < 64 {
                return Err(invalid(
                    "requested Firecracker executable type or mode differs from the official asset",
                ));
            }
            let mut hash = Sha256::new();
            let mut prefix = [0_u8; 64];
            entry.read_exact(&mut prefix)?;
            if prefix[..6] != *b"\x7fELF\x02\x01"
                || prefix[18..20] != 62_u16.to_le_bytes()
                || !matches!(u16::from_le_bytes(prefix[16..18].try_into()?), 2 | 3)
            {
                return Err(invalid(
                    "requested Firecracker member is not an x86_64 ELF executable",
                ));
            }
            hash.update(prefix);
            let mut copied = 64_u64;
            let mut buffer = [0_u8; 16 * 1024];
            loop {
                let read = entry.read(&mut buffer)?;
                if read == 0 {
                    break;
                }
                copied += read as u64;
                if copied > size {
                    return Err(invalid(
                        "Firecracker executable exceeds its declared length",
                    ));
                }
                hash.update(&buffer[..read]);
            }
            if copied != size {
                return Err(invalid("Firecracker executable member is truncated"));
            }
            requested = Some(MemberIdentity {
                size_bytes: size,
                sha256: format!("{:x}", hash.finalize()),
            });
        }
    }
    // Drain past tar's end markers to force gzip checksum validation and the
    // expanded-byte limit even when tar stops at its first zero header.
    io::copy(&mut archive.into_inner(), &mut io::sink())?;
    requested.ok_or_else(|| invalid("Firecracker tar lacks the requested executable member"))
}

fn extract_member(
    bytes: &[u8],
    expected_member: &str,
    inspected: &MemberIdentity,
    run: &RunDirectory,
) -> Result<std::path::PathBuf, Box<dyn Error>> {
    let mut output = run.create_staged_file(OUTPUT_NAME)?;
    let mut archive = archive_reader(bytes);
    let mut entries = archive.entries()?.raw(true);
    for next in entries.by_ref() {
        let mut entry = next?;
        if entry.path_bytes().as_ref() != expected_member.as_bytes() {
            continue;
        }
        let size = entry.size();
        let mut hash = Sha256::new();
        let mut copied = 0_u64;
        let mut buffer = [0_u8; 16 * 1024];
        loop {
            let count = entry.read(&mut buffer)?;
            if count == 0 {
                break;
            }
            copied = copied
                .checked_add(count as u64)
                .ok_or_else(|| invalid("Firecracker executable length overflow"))?;
            if copied > size {
                return Err(invalid(
                    "Firecracker executable exceeds its declared length",
                ));
            }
            hash.update(&buffer[..count]);
            output.write_all(&buffer[..count])?;
        }
        if copied != size {
            return Err(invalid("Firecracker executable copy is truncated"));
        }
        let identity = MemberIdentity {
            size_bytes: copied,
            sha256: format!("{:x}", hash.finalize()),
        };
        if identity.size_bytes != inspected.size_bytes || identity.sha256 != inspected.sha256 {
            return Err(invalid(
                "Firecracker executable changed between preflight and extraction",
            ));
        }
        let path = output.commit()?;
        let finalize = fs::set_permissions(&path, fs::Permissions::from_mode(0o500))
            .and_then(|()| fs::File::open(&path)?.sync_all());
        if let Err(error) = finalize {
            let _ = fs::remove_file(&path);
            return Err(error.into());
        }
        return Ok(path);
    }
    Err(invalid(
        "Firecracker tar member disappeared after preflight",
    ))
}

#[cfg(test)]
mod tests {
    use std::io::{Cursor, Read, Write};

    use flate2::Compression;
    use flate2::write::GzEncoder;
    use tar::{Builder, EntryType, Header};

    use super::{LimitedReader, hash_bytes, safe_member_path, scan_archive};

    const MEMBER: &str = "release-v1.17.0-x86_64/firecracker-v1.17.0-x86_64";

    fn elf_stub() -> Vec<u8> {
        let mut bytes = vec![0_u8; 64];
        bytes[..6].copy_from_slice(b"\x7fELF\x02\x01");
        bytes[16..18].copy_from_slice(&2_u16.to_le_bytes());
        bytes[18..20].copy_from_slice(&62_u16.to_le_bytes());
        bytes
    }

    fn archive(members: &[(&str, EntryType)]) -> Vec<u8> {
        let mut builder = Builder::new(Vec::new());
        for (path, kind) in members {
            let mut header = Header::new_gnu();
            header.set_entry_type(*kind);
            let bytes = if *kind == EntryType::Regular {
                elf_stub()
            } else {
                Vec::new()
            };
            header.set_size(bytes.len() as u64);
            header.set_mode(0o755);
            if *kind == EntryType::Symlink {
                header.set_link_name("other-file").unwrap();
            }
            header.set_cksum();
            builder
                .append_data(&mut header, path, bytes.as_slice())
                .unwrap();
        }
        let tar = builder.into_inner().unwrap();
        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(&tar).unwrap();
        encoder.finish().unwrap()
    }

    #[test]
    fn exact_regular_elf_is_observed_without_unpacking_other_members() {
        let compressed = archive(&[
            ("release-v1.17.0-x86_64", EntryType::Directory),
            (MEMBER, EntryType::Regular),
        ]);
        let identity = scan_archive(&compressed, MEMBER).unwrap();
        assert_eq!(identity.size_bytes, 64);
        assert_eq!(identity.sha256, hash_bytes(&elf_stub()));
    }

    #[test]
    fn duplicate_target_and_link_members_are_rejected() {
        let duplicate = archive(&[(MEMBER, EntryType::Regular), (MEMBER, EntryType::Regular)]);
        assert!(
            scan_archive(&duplicate, MEMBER)
                .unwrap_err()
                .to_string()
                .contains("repeats")
        );
        let linked = archive(&[(MEMBER, EntryType::Symlink)]);
        assert!(
            scan_archive(&linked, MEMBER)
                .unwrap_err()
                .to_string()
                .contains("link")
        );
        let pax = archive(&[
            ("PaxHeader", EntryType::XHeader),
            (MEMBER, EntryType::Regular),
        ]);
        assert!(
            scan_archive(&pax, MEMBER)
                .unwrap_err()
                .to_string()
                .contains("extension")
        );
    }

    #[test]
    fn traversal_paths_and_expansion_past_limit_are_rejected() {
        assert!(safe_member_path(b"../firecracker", false).is_err());
        assert!(safe_member_path(b"/absolute/firecracker", false).is_err());
        assert!(safe_member_path(b"release/./firecracker", false).is_err());
        let mut bounded = LimitedReader {
            inner: Cursor::new([1_u8, 2]),
            remaining: 1,
        };
        let mut output = Vec::new();
        assert!(bounded.read_to_end(&mut output).is_err());
    }
}
