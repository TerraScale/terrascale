use std::collections::{BTreeMap, BTreeSet};
use std::error::Error;
use std::fs::{self, File};
use std::io::Read;
use std::path::{Component, Path};

use serde::Deserialize;
use sha2::{Digest, Sha256};
use tar::{Archive, Entry};

const MAX_ARCHIVE_BYTES: u64 = 64 * 1024 * 1024 * 1024;
const MAX_ARCHIVE_ENTRIES: usize = 262_144;
const MAX_JSON_BLOB_BYTES: u64 = 8 * 1024 * 1024;
const MAX_CAPTURED_BLOB_BYTES: u64 = 64 * 1024 * 1024;
const MAX_INDEX_DESCRIPTORS: usize = 256;

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct OciArchiveIdentity {
    pub(crate) archive_size_bytes: u64,
    pub(crate) archive_sha256: String,
    pub(crate) manifest_digest: String,
    pub(crate) config_digest: String,
    pub(crate) platform: String,
    pub(crate) reference_name: Option<String>,
}

/// Inspect an OCI archive and return the byte and descriptor identities for
/// its one image matching `expected_platform`. This is used while qualifying
/// a newly built image, before the observed values are copied into a recipe.
///
/// # Errors
///
/// Returns an error if the archive is not a bounded regular file, contains
/// unsupported tar extensions or entries, or does not contain exactly one
/// self-contained image for the requested platform.
pub(crate) fn inspect_oci_archive(
    path: &Path,
    expected_platform: &str,
) -> Result<OciArchiveIdentity, Box<dyn Error>> {
    if !matches!(expected_platform, "linux/amd64" | "linux/arm64") {
        return Err(invalid("OCI archive platform is unsupported"));
    }
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() == 0
        || metadata.len() > MAX_ARCHIVE_BYTES
    {
        return Err(invalid("OCI archive must be a bounded regular file"));
    }
    let archive_size_bytes = metadata.len();
    let archive_sha256 = sha256_file(path)?;
    let contents = read_archive_contents(path)?;
    validate_layout(&contents.layout)?;
    let index: OciIndex = serde_json::from_slice(&contents.index)?;
    if index.schema_version != 2 || index.manifests.is_empty() {
        return Err(invalid(
            "OCI archive index schema or manifest list is invalid",
        ));
    }
    let selected = find_platform_manifest(
        &index,
        &contents.blobs,
        expected_platform,
        0,
        &mut BTreeSet::new(),
    )?
    .ok_or_else(|| invalid("OCI archive has no image for the requested platform"))?;
    validate_image_manifest(
        &selected,
        &contents.blobs,
        &selected.manifest_digest,
        &selected.config_digest,
        expected_platform,
    )?;
    Ok(OciArchiveIdentity {
        archive_size_bytes,
        archive_sha256,
        manifest_digest: selected.manifest_digest,
        config_digest: selected.config_digest,
        platform: expected_platform.to_owned(),
        reference_name: selected.reference_name,
    })
}

#[derive(Clone, Debug)]
struct BlobRecord {
    size: u64,
    sha256: String,
    json: Option<Vec<u8>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct OciLayout {
    image_layout_version: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct OciIndex {
    schema_version: u32,
    manifests: Vec<OciDescriptor>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct OciDescriptor {
    media_type: String,
    digest: String,
    size: u64,
    platform: Option<OciPlatform>,
    annotations: Option<BTreeMap<String, String>>,
    urls: Option<Vec<String>>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct OciPlatform {
    os: String,
    architecture: String,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct OciManifest {
    schema_version: u32,
    media_type: String,
    config: OciDescriptor,
    layers: Vec<OciDescriptor>,
    subject: Option<OciDescriptor>,
}

#[derive(Deserialize)]
struct OciImageConfig {
    os: String,
    architecture: String,
}

struct ArchiveContents {
    layout: Vec<u8>,
    index: Vec<u8>,
    blobs: BTreeMap<String, BlobRecord>,
}

/// Check a self-contained OCI image archive against recipe-pinned byte and
/// descriptor identities without extracting any archive member.
///
/// # Errors
///
/// Returns an error if the archive path, size, archive hash, OCI layout,
/// selected image platform, descriptor graph, or any referenced local blob is
/// missing or differs from its expected identity.
pub(crate) fn verify_oci_archive(
    path: &Path,
    expected_size: u64,
    expected_archive_sha256: &str,
    expected_manifest_digest: &str,
    expected_config_digest: &str,
    expected_platform: &str,
) -> Result<OciArchiveIdentity, Box<dyn Error>> {
    let archive_size_bytes = verify_archive_file(path, expected_size, expected_archive_sha256)?;
    let contents = read_archive_contents(path)?;
    validate_layout(&contents.layout)?;
    let index: OciIndex = serde_json::from_slice(&contents.index)?;
    if index.schema_version != 2 || index.manifests.is_empty() {
        return Err(invalid(
            "OCI archive index schema or manifest list is invalid",
        ));
    }
    let selected = find_expected_manifest(
        &index,
        expected_manifest_digest,
        &contents.blobs,
        0,
        &mut BTreeSet::new(),
    )?
    .ok_or_else(|| invalid("OCI archive does not contain the pinned image manifest"))?;
    validate_image_manifest(
        &selected,
        &contents.blobs,
        expected_manifest_digest,
        expected_config_digest,
        expected_platform,
    )?;
    Ok(OciArchiveIdentity {
        archive_size_bytes,
        archive_sha256: expected_archive_sha256.to_ascii_lowercase(),
        manifest_digest: expected_manifest_digest.to_ascii_lowercase(),
        config_digest: expected_config_digest.to_ascii_lowercase(),
        platform: expected_platform.to_owned(),
        reference_name: selected.reference_name,
    })
}

fn verify_archive_file(
    path: &Path,
    expected_size: u64,
    expected_sha256: &str,
) -> Result<u64, Box<dyn Error>> {
    if expected_size == 0 || expected_size > MAX_ARCHIVE_BYTES || !is_sha256(expected_sha256) {
        return Err(invalid("OCI archive size or pinned SHA-256 is invalid"));
    }
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() != expected_size {
        return Err(invalid(
            "OCI archive must be a regular file of its pinned size",
        ));
    }
    if !sha256_file(path)?.eq_ignore_ascii_case(expected_sha256) {
        return Err(invalid(
            "OCI archive SHA-256 differs from the pinned receipt",
        ));
    }
    Ok(metadata.len())
}

fn read_archive_contents(path: &Path) -> Result<ArchiveContents, Box<dyn Error>> {
    let mut archive = Archive::new(File::open(path)?);
    // `entries()` applies GNU/PAX metadata by buffering extension bodies
    // before yielding the following entry. Raw seekable iteration exposes
    // the extension header first, so unsupported metadata is rejected before
    // its potentially attacker-controlled body is read or allocated.
    let entries = archive.entries_with_seek()?.raw(true);
    let mut seen = BTreeSet::new();
    let mut layout = None;
    let mut index = None;
    let mut blobs = BTreeMap::new();
    let mut captured_blob_bytes = 0_u64;
    let mut entry_count = 0_usize;
    for entry in entries {
        let mut entry = entry?;
        entry_count = entry_count
            .checked_add(1)
            .ok_or_else(|| invalid("OCI archive entry count overflow"))?;
        if entry_count > MAX_ARCHIVE_ENTRIES {
            return Err(invalid("OCI archive has too many tar entries"));
        }
        let path_text = entry_path(&entry)?;
        if !seen.insert(path_text.clone()) {
            return Err(invalid("OCI archive repeats a tar path"));
        }
        let entry_type = entry.header().entry_type().as_byte();
        let size = entry.size();
        match entry_type {
            b'5' => validate_directory_entry(&path_text, size)?,
            b'0' => {
                let (content, digest) =
                    read_regular_entry(&mut entry, &path_text, size, &mut captured_blob_bytes)?;
                match path_text.as_str() {
                    "oci-layout" => set_unique_bytes(&mut layout, content)?,
                    "index.json" => set_unique_bytes(&mut index, content)?,
                    _ => {
                        let blob_path = blob_path_digest(&path_text)?;
                        let record = BlobRecord {
                            size,
                            sha256: digest,
                            json: content,
                        };
                        if blobs.insert(blob_path, record).is_some() {
                            return Err(invalid("OCI archive repeats a blob digest"));
                        }
                    }
                }
            }
            b'x' | b'g' | b'L' | b'K' | b'S' => {
                return Err(invalid(
                    "OCI archive contains unsupported PAX, GNU, or sparse metadata",
                ));
            }
            _ => return Err(invalid("OCI archive contains a link or special tar entry")),
        }
    }
    Ok(ArchiveContents {
        layout: layout.ok_or_else(|| invalid("OCI archive has no oci-layout file"))?,
        index: index.ok_or_else(|| invalid("OCI archive has no index.json file"))?,
        blobs,
    })
}

type RegularEntryContents = (Option<Vec<u8>>, String);

fn read_regular_entry<R: Read>(
    entry: &mut Entry<'_, R>,
    path: &str,
    size: u64,
    captured_blob_bytes: &mut u64,
) -> Result<RegularEntryContents, Box<dyn Error>> {
    if path == "oci-layout" || path == "index.json" {
        let bytes = read_bounded_bytes(entry, size, MAX_JSON_BLOB_BYTES)?;
        return Ok((Some(bytes.clone()), sha256_bytes(&bytes)));
    }
    let digest = blob_path_digest(path)?;
    let capture = size <= MAX_JSON_BLOB_BYTES
        && captured_blob_bytes
            .checked_add(size)
            .is_some_and(|total| total <= MAX_CAPTURED_BLOB_BYTES);
    let mut buffer = vec![0_u8; 64 * 1024].into_boxed_slice();
    let mut bytes = if capture {
        Some(Vec::with_capacity(usize::try_from(size)?))
    } else {
        None
    };
    let mut hasher = Sha256::new();
    let mut total = 0_u64;
    loop {
        let count = entry.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        total = total
            .checked_add(count as u64)
            .ok_or_else(|| invalid("OCI blob size overflow"))?;
        if total > size {
            return Err(invalid("OCI tar entry exceeds its header size"));
        }
        hasher.update(&buffer[..count]);
        if let Some(bytes) = &mut bytes {
            bytes.extend_from_slice(&buffer[..count]);
        }
    }
    if total != size {
        return Err(invalid("OCI tar entry length differs from its header size"));
    }
    if bytes.is_some() {
        *captured_blob_bytes = captured_blob_bytes
            .checked_add(size)
            .ok_or_else(|| invalid("captured OCI metadata byte count overflow"))?;
    }
    let observed = digest_hex(&hasher.finalize());
    if !observed.eq_ignore_ascii_case(&digest) {
        return Err(invalid("OCI blob bytes differ from their sha256 path"));
    }
    Ok((bytes, observed))
}

fn find_expected_manifest(
    index: &OciIndex,
    expected_digest: &str,
    blobs: &BTreeMap<String, BlobRecord>,
    depth: usize,
    visited: &mut BTreeSet<String>,
) -> Result<Option<SelectedManifest>, Box<dyn Error>> {
    if depth > 4 || index.manifests.len() > MAX_INDEX_DESCRIPTORS {
        return Err(invalid(
            "OCI nested index depth or descriptor count exceeds limits",
        ));
    }
    let mut selected = None;
    for descriptor in &index.manifests {
        let blob = verify_blob_descriptor(descriptor, blobs)?;
        if descriptor.digest.eq_ignore_ascii_case(expected_digest) {
            if descriptor.media_type != "application/vnd.oci.image.manifest.v1+json"
                || selected.is_some()
            {
                return Err(invalid(
                    "OCI archive has an ambiguous or unsupported image manifest",
                ));
            }
            let manifest = blob
                .json
                .as_deref()
                .ok_or_else(|| invalid("pinned OCI manifest exceeds the metadata budget"))?;
            let manifest_value: OciManifest = serde_json::from_slice(manifest)?;
            selected = Some(SelectedManifest {
                value: manifest_value.clone(),
                manifest_digest: descriptor.digest.clone(),
                config_digest: manifest_value.config.digest.clone(),
                reference_name: descriptor
                    .annotations
                    .as_ref()
                    .and_then(|annotations| annotations.get("org.opencontainers.image.ref.name"))
                    .cloned(),
                platform: descriptor.platform.clone(),
            });
        } else if descriptor.media_type == "application/vnd.oci.image.index.v1+json" {
            if !visited.insert(descriptor.digest.to_ascii_lowercase()) {
                return Err(invalid("OCI nested index graph contains a cycle"));
            }
            let nested = blob
                .json
                .as_deref()
                .ok_or_else(|| invalid("nested OCI index exceeds the metadata budget"))?;
            let nested: OciIndex = serde_json::from_slice(nested)?;
            if nested.schema_version != 2 {
                return Err(invalid("nested OCI index schema version is invalid"));
            }
            if let Some(found) =
                find_expected_manifest(&nested, expected_digest, blobs, depth + 1, visited)?
            {
                if selected.replace(found).is_some() {
                    return Err(invalid(
                        "OCI archive contains multiple pinned image manifests",
                    ));
                }
            }
        }
    }
    Ok(selected)
}

fn find_platform_manifest(
    index: &OciIndex,
    blobs: &BTreeMap<String, BlobRecord>,
    expected_platform: &str,
    depth: usize,
    visited: &mut BTreeSet<String>,
) -> Result<Option<SelectedManifest>, Box<dyn Error>> {
    if depth > 4 || index.manifests.len() > MAX_INDEX_DESCRIPTORS {
        return Err(invalid(
            "OCI nested index depth or descriptor count exceeds limits",
        ));
    }
    let mut selected = None;
    for descriptor in &index.manifests {
        let blob = verify_blob_descriptor(descriptor, blobs)?;
        if descriptor.media_type == "application/vnd.oci.image.manifest.v1+json" {
            let manifest_bytes = blob
                .json
                .as_deref()
                .ok_or_else(|| invalid("OCI image manifest exceeds the metadata budget"))?;
            let manifest: OciManifest = serde_json::from_slice(manifest_bytes)?;
            let config = verify_blob_descriptor(&manifest.config, blobs)?;
            let config_bytes = config
                .json
                .as_deref()
                .ok_or_else(|| invalid("OCI image config exceeds the metadata budget"))?;
            let config: OciImageConfig = serde_json::from_slice(config_bytes)?;
            let observed_platform = format!("{}/{}", config.os, config.architecture);
            if let Some(platform) = &descriptor.platform {
                if format!("{}/{}", platform.os, platform.architecture) != observed_platform {
                    return Err(invalid(
                        "OCI manifest descriptor platform differs from its image config",
                    ));
                }
            }
            if observed_platform == expected_platform {
                let candidate = SelectedManifest {
                    value: manifest,
                    manifest_digest: descriptor.digest.clone(),
                    config_digest: config_digest_from_manifest(manifest_bytes)?,
                    reference_name: descriptor
                        .annotations
                        .as_ref()
                        .and_then(|annotations| {
                            annotations.get("org.opencontainers.image.ref.name")
                        })
                        .cloned(),
                    platform: descriptor.platform.clone(),
                };
                validate_image_manifest(
                    &candidate,
                    blobs,
                    &candidate.manifest_digest,
                    &candidate.config_digest,
                    expected_platform,
                )?;
                if selected.replace(candidate).is_some() {
                    return Err(invalid(
                        "OCI archive contains multiple images for the requested platform",
                    ));
                }
            }
        } else if descriptor.media_type == "application/vnd.oci.image.index.v1+json" {
            if !visited.insert(descriptor.digest.to_ascii_lowercase()) {
                return Err(invalid("OCI nested index graph contains a cycle"));
            }
            let nested_bytes = blob
                .json
                .as_deref()
                .ok_or_else(|| invalid("nested OCI index exceeds the metadata budget"))?;
            let nested: OciIndex = serde_json::from_slice(nested_bytes)?;
            if nested.schema_version != 2 {
                return Err(invalid("nested OCI index schema version is invalid"));
            }
            if let Some(found) =
                find_platform_manifest(&nested, blobs, expected_platform, depth + 1, visited)?
            {
                if selected.replace(found).is_some() {
                    return Err(invalid(
                        "OCI archive contains multiple images for the requested platform",
                    ));
                }
            }
        } else {
            return Err(invalid(
                "OCI index uses an unsupported descriptor media type",
            ));
        }
    }
    Ok(selected)
}

fn validate_image_manifest(
    selected: &SelectedManifest,
    blobs: &BTreeMap<String, BlobRecord>,
    expected_manifest_digest: &str,
    expected_config_digest: &str,
    expected_platform: &str,
) -> Result<(), Box<dyn Error>> {
    let manifest = &selected.value;
    if manifest.schema_version != 2
        || manifest.media_type != "application/vnd.oci.image.manifest.v1+json"
        || manifest.subject.is_some()
        || !manifest
            .config
            .digest
            .eq_ignore_ascii_case(expected_config_digest)
    {
        return Err(invalid(
            "pinned OCI manifest schema or config descriptor is invalid",
        ));
    }
    let config = verify_blob_descriptor(&manifest.config, blobs)?;
    for layer in &manifest.layers {
        let _ = verify_blob_descriptor(layer, blobs)?;
    }
    let config_bytes = config
        .json
        .as_deref()
        .ok_or_else(|| invalid("OCI image config exceeds the metadata budget"))?;
    let config: OciImageConfig = serde_json::from_slice(config_bytes)?;
    let observed_platform = format!("{}/{}", config.os, config.architecture);
    if observed_platform != expected_platform
        || !selected
            .value
            .config
            .digest
            .eq_ignore_ascii_case(expected_config_digest)
        || expected_manifest_digest.is_empty()
    {
        return Err(invalid(
            "OCI image config platform or digest differs from the recipe",
        ));
    }
    if let Some(platform) = selected.platform.as_ref() {
        if format!("{}/{}", platform.os, platform.architecture) != expected_platform {
            return Err(invalid(
                "OCI config descriptor platform differs from the recipe",
            ));
        }
    }
    Ok(())
}

struct SelectedManifest {
    value: OciManifest,
    manifest_digest: String,
    config_digest: String,
    reference_name: Option<String>,
    platform: Option<OciPlatform>,
}

fn config_digest_from_manifest(manifest_bytes: &[u8]) -> Result<String, Box<dyn Error>> {
    let manifest: OciManifest = serde_json::from_slice(manifest_bytes)?;
    digest_hex_from_oci(&manifest.config.digest).map(|digest| format!("sha256:{digest}"))
}

fn verify_blob_descriptor<'a>(
    descriptor: &OciDescriptor,
    blobs: &'a BTreeMap<String, BlobRecord>,
) -> Result<&'a BlobRecord, Box<dyn Error>> {
    let digest = digest_hex_from_oci(&descriptor.digest)?;
    if descriptor
        .urls
        .as_ref()
        .is_some_and(|urls| !urls.is_empty())
    {
        return Err(invalid("OCI descriptor requires an external blob URL"));
    }
    let blob = blobs
        .get(&digest)
        .ok_or_else(|| invalid("OCI descriptor blob is absent from the offline archive"))?;
    if descriptor.size != blob.size || !digest.eq_ignore_ascii_case(&blob.sha256) {
        return Err(invalid("OCI descriptor size or blob SHA-256 is invalid"));
    }
    Ok(blob)
}

fn entry_path<R: Read>(entry: &Entry<'_, R>) -> Result<String, Box<dyn Error>> {
    let bytes = entry.path_bytes();
    let path = std::str::from_utf8(&bytes)?;
    let path_object = Path::new(path);
    if path.is_empty()
        || path.contains('\0')
        || path_object.is_absolute()
        || path_object
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(invalid("OCI tar path is absolute, empty, or traversing"));
    }
    Ok(path.to_owned())
}

fn validate_directory_entry(path: &str, size: u64) -> Result<(), Box<dyn Error>> {
    if size != 0 || !matches!(path, "blobs/" | "blobs/sha256/") {
        return Err(invalid(
            "OCI archive contains an unexpected directory entry",
        ));
    }
    Ok(())
}

fn blob_path_digest(path: &str) -> Result<String, Box<dyn Error>> {
    let digest = path
        .strip_prefix("blobs/sha256/")
        .ok_or_else(|| invalid("OCI archive contains an unexpected file path"))?;
    if !is_sha256(digest) {
        return Err(invalid("OCI blob path is not a sha256 digest"));
    }
    Ok(digest.to_ascii_lowercase())
}

fn read_bounded_bytes<R: Read>(
    reader: &mut R,
    size: u64,
    limit: u64,
) -> Result<Vec<u8>, Box<dyn Error>> {
    if size > limit {
        return Err(invalid("OCI metadata file exceeds its byte limit"));
    }
    let capacity = usize::try_from(size)?;
    let mut bytes = Vec::with_capacity(capacity);
    reader.take(limit + 1).read_to_end(&mut bytes)?;
    if bytes.len() != capacity {
        return Err(invalid("OCI metadata length differs from its tar header"));
    }
    Ok(bytes)
}

fn set_unique_bytes(
    destination: &mut Option<Vec<u8>>,
    value: Option<Vec<u8>>,
) -> Result<(), Box<dyn Error>> {
    if destination.is_some() {
        return Err(invalid("OCI archive repeats a required layout file"));
    }
    *destination = value;
    Ok(())
}

fn validate_layout(bytes: &[u8]) -> Result<(), Box<dyn Error>> {
    let layout: OciLayout = serde_json::from_slice(bytes)?;
    if layout.image_layout_version != "1.0.0" {
        return Err(invalid("OCI image layout version is unsupported"));
    }
    Ok(())
}

fn digest_hex_from_oci(digest: &str) -> Result<String, Box<dyn Error>> {
    let hash = digest
        .strip_prefix("sha256:")
        .ok_or_else(|| invalid("OCI descriptor algorithm is not sha256"))?;
    if !is_sha256(hash) {
        return Err(invalid("OCI descriptor SHA-256 is malformed"));
    }
    Ok(hash.to_ascii_lowercase())
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn sha256_file(path: &Path) -> Result<String, Box<dyn Error>> {
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = vec![0_u8; 128 * 1024].into_boxed_slice();
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }
    Ok(digest_hex(&digest.finalize()))
}

fn sha256_bytes(bytes: &[u8]) -> String {
    digest_hex(&Sha256::digest(bytes))
}

fn digest_hex(bytes: &[u8]) -> String {
    let mut encoded = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        write!(&mut encoded, "{byte:02x}").expect("writing into String cannot fail");
    }
    encoded
}

fn invalid(message: &str) -> Box<dyn Error> {
    std::io::Error::new(std::io::ErrorKind::InvalidData, message).into()
}
