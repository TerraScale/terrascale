use std::error::Error;
use std::fmt;
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::Path;

use sha2::{Digest, Sha256};

use crate::signature::VerifiedSignature;
use crate::{
    CommandReceipt, CommandRunner, CommandSpec, ContainerMount, NetworkMode, PodmanError,
    PodmanExecutor, RunDirectory, ToolError, ToolPin, VerifiedLocalImage,
};

const APT_FINGERPRINT: &str = "F6ECB3762474EDA9D21B7022871920D1991BC93C";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PackageIndexPin {
    pub snapshot_url: String,
    pub suite: String,
    pub component: String,
    pub architecture: String,
    pub trusted_keyring_sha256: String,
    pub inrelease_sha256: String,
    pub packages_index_sha256: String,
    pub packages_index_size: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VerifiedPackageIndex {
    suite: String,
    component: String,
    architecture: String,
    inrelease_url: String,
    inrelease_sha256: String,
    packages_index_url: String,
    packages_index_sha256: String,
    packages_index_size: u64,
}

impl VerifiedPackageIndex {
    #[must_use]
    pub fn suite(&self) -> &str {
        &self.suite
    }

    #[must_use]
    pub fn component(&self) -> &str {
        &self.component
    }

    #[must_use]
    pub fn architecture(&self) -> &str {
        &self.architecture
    }

    #[must_use]
    pub fn inrelease_url(&self) -> &str {
        &self.inrelease_url
    }

    #[must_use]
    pub fn inrelease_sha256(&self) -> &str {
        &self.inrelease_sha256
    }

    #[must_use]
    pub fn packages_index_url(&self) -> &str {
        &self.packages_index_url
    }

    #[must_use]
    pub fn packages_index_sha256(&self) -> &str {
        &self.packages_index_sha256
    }

    #[must_use]
    pub const fn packages_index_size(&self) -> u64 {
        self.packages_index_size
    }
}

#[derive(Debug)]
pub enum AptIndexError {
    Io(io::Error),
    Invalid(String),
    Tool(ToolError),
    Podman(PodmanError),
}

impl fmt::Display for AptIndexError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "apt index I/O failed: {error}"),
            Self::Invalid(error) => write!(formatter, "signed apt index rejected: {error}"),
            Self::Tool(error) => write!(formatter, "apt index tool failed: {error}"),
            Self::Podman(error) => write!(formatter, "apt index Podman command failed: {error}"),
        }
    }
}

impl Error for AptIndexError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::Tool(error) => Some(error),
            Self::Podman(error) => Some(error),
            Self::Invalid(_) => None,
        }
    }
}

impl From<io::Error> for AptIndexError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<ToolError> for AptIndexError {
    fn from(error: ToolError) -> Self {
        Self::Tool(error)
    }
}

impl From<PodmanError> for AptIndexError {
    fn from(error: PodmanError) -> Self {
        Self::Podman(error)
    }
}

#[derive(Clone, Debug)]
pub struct VerifiedDecompressedIndex {
    index: VerifiedPackageIndex,
    path: std::path::PathBuf,
    command: CommandReceipt,
}

impl VerifiedDecompressedIndex {
    #[must_use]
    pub fn index(&self) -> &VerifiedPackageIndex {
        &self.index
    }

    #[must_use]
    pub fn command_receipt(&self) -> &CommandReceipt {
        &self.command
    }

    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    #[must_use]
    pub fn into_parts(self) -> (VerifiedPackageIndex, std::path::PathBuf, CommandReceipt) {
        (self.index, self.path, self.command)
    }
}

/// Match one downloaded Packages index byte-for-byte against the SHA-256
/// section of a separately `gpgv`-verified `InRelease` file.
///
/// # Errors
///
/// Returns an error when the signature receipt, pinned hashes, signed index
/// metadata, or downloaded file bytes do not match the requested snapshot.
pub fn verify_package_index(
    pin: &PackageIndexPin,
    inrelease_path: &Path,
    packages_index_path: &Path,
    inrelease_signature: &VerifiedSignature,
) -> Result<VerifiedPackageIndex, AptIndexError> {
    validate_pin(pin)?;
    let signature = inrelease_signature.receipt();
    if signature.status != "validsig"
        || signature.signer_primary_fingerprint != APT_FINGERPRINT
        || signature.keyring_sha256 != pin.trusted_keyring_sha256
        || !signature
            .signature_sha256
            .eq_ignore_ascii_case(&pin.inrelease_sha256)
    {
        return Err(AptIndexError::Invalid(
            "InRelease lacks a matching gpgv receipt from the pinned archive key".to_owned(),
        ));
    }
    let inrelease_url = format!(
        "{}/dists/{}/InRelease",
        pin.snapshot_url.trim_end_matches('/'),
        pin.suite
    );
    let packages_index_url = format!(
        "{}/dists/{}/{}/binary-{}/Packages.xz",
        pin.snapshot_url.trim_end_matches('/'),
        pin.suite,
        pin.component,
        pin.architecture
    );
    if !sha256_file(inrelease_path)?.eq_ignore_ascii_case(&pin.inrelease_sha256) {
        return Err(AptIndexError::Invalid(
            "InRelease SHA-256 differs from the locked candidate".to_owned(),
        ));
    }
    let payload = clearsigned_payload(&fs::read_to_string(inrelease_path)?)?;
    // Snapshot InRelease names indexes as `COMPONENT/binary-ARCH/Packages.xz`.
    let index_path = format!("{}/binary-{}/Packages.xz", pin.component, pin.architecture);
    let (signed_sha256, signed_size) = sha256_entry(&payload, &index_path)?;
    if !signed_sha256.eq_ignore_ascii_case(&pin.packages_index_sha256)
        || signed_size != pin.packages_index_size
    {
        return Err(AptIndexError::Invalid(format!(
            "signed InRelease entry differs for {index_path}"
        )));
    }
    let metadata = fs::symlink_metadata(packages_index_path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(AptIndexError::Invalid(
            "Packages index must be a regular non-symlink file".to_owned(),
        ));
    }
    if metadata.len() != signed_size
        || !sha256_file(packages_index_path)?.eq_ignore_ascii_case(&signed_sha256)
    {
        return Err(AptIndexError::Invalid(
            "downloaded Packages index does not match the signed size and SHA-256".to_owned(),
        ));
    }
    Ok(VerifiedPackageIndex {
        suite: pin.suite.clone(),
        component: pin.component.clone(),
        architecture: pin.architecture.clone(),
        inrelease_url,
        inrelease_sha256: pin.inrelease_sha256.to_ascii_lowercase(),
        packages_index_url,
        packages_index_sha256: signed_sha256.to_ascii_lowercase(),
        packages_index_size: signed_size,
    })
}

/// Decompress one SHA256-verified Packages.xz with the pinned `xz` executable.
/// The returned text path and command receipt cannot be constructed by callers.
///
/// # Errors
///
/// Returns an error when the tool pin, compressed input, command result, or
/// decompressed output fails validation.
pub fn decompress_package_index(
    runner: &CommandRunner,
    xz: &ToolPin,
    index: VerifiedPackageIndex,
    packages_xz_path: &Path,
    command_id: &str,
    run: &RunDirectory,
) -> Result<VerifiedDecompressedIndex, AptIndexError> {
    if xz.id != "xz" {
        return Err(AptIndexError::Invalid(
            "Packages index decompressor lock must identify xz".to_owned(),
        ));
    }
    let metadata = fs::symlink_metadata(packages_xz_path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() != index.packages_index_size
        || !sha256_file(packages_xz_path)?.eq_ignore_ascii_case(&index.packages_index_sha256)
    {
        return Err(AptIndexError::Invalid(
            "compressed Packages index changed after signature verification".to_owned(),
        ));
    }
    let run_root = fs::canonicalize(run.path())?;
    let source = fs::canonicalize(packages_xz_path)?;
    if !source.starts_with(&run_root) {
        return Err(AptIndexError::Invalid(
            "compressed Packages index is outside the private run directory".to_owned(),
        ));
    }
    let command = runner.run(
        &CommandSpec {
            id: command_id.to_owned(),
            tool: xz.clone(),
            args: vec![
                "--decompress".to_owned(),
                "--stdout".to_owned(),
                "--no-warn".to_owned(),
                source.to_string_lossy().into_owned(),
            ],
            working_directory: run_root.clone(),
            environment: Vec::new(),
        },
        run,
    )?;
    if command.status_code != 0 {
        return Err(AptIndexError::Invalid(format!(
            "pinned xz exited with status {}",
            command.status_code
        )));
    }
    let output = run_root.join(&command.stdout_path);
    let output_metadata = fs::symlink_metadata(&output)?;
    if output_metadata.file_type().is_symlink()
        || !output_metadata.is_file()
        || output_metadata.len() > 512 * 1024 * 1024
        || !sha256_file(&output)?.eq_ignore_ascii_case(&command.stdout_sha256)
    {
        return Err(AptIndexError::Invalid(
            "decompressed Packages index log is invalid or exceeds its limit".to_owned(),
        ));
    }
    Ok(VerifiedDecompressedIndex {
        index,
        path: output,
        command,
    })
}

/// Decompress a verified Packages index with the xz executable inside the
/// recipe-pinned Ubuntu builder image, keeping all package solving tools in
/// the Noble environment.
///
/// # Errors
///
/// Returns an error if the compressed index changed, the tool/image identity
/// is invalid, Podman fails, or the output is missing or oversized.
pub fn decompress_package_index_in_podman(
    podman: &PodmanExecutor<'_>,
    image: &str,
    xz_path: &str,
    xz_sha256: &str,
    index: VerifiedPackageIndex,
    packages_xz_path: &Path,
    command_id: &str,
    run: &RunDirectory,
) -> Result<VerifiedDecompressedIndex, AptIndexError> {
    decompress_package_index_in_image(
        podman,
        PackageIndexImage::Pinned(image),
        PackageIndexDecompressor::Xz {
            path: xz_path,
            sha256: xz_sha256,
        },
        index,
        packages_xz_path,
        command_id,
        run,
    )
}

/// Decompress a verified Packages index with `apt-helper cat-file` inside the
/// pinned Ubuntu bootstrap image, preserving the `.xz` suffix used by APT's
/// extension-aware file reader.
///
/// # Errors
///
/// Returns an error if the compressed index changed, the helper/image identity
/// is invalid, Podman fails, or the output is missing or oversized.
pub(crate) fn decompress_package_index_with_apt_helper_in_podman(
    podman: &PodmanExecutor<'_>,
    image: &str,
    apt_helper_path: &str,
    apt_helper_sha256: &str,
    index: VerifiedPackageIndex,
    packages_xz_path: &Path,
    command_id: &str,
    run: &RunDirectory,
) -> Result<VerifiedDecompressedIndex, AptIndexError> {
    if apt_helper_path != crate::recipe::EXPECTED_INITIAL_APT_HELPER_PATH
        || !apt_helper_sha256
            .eq_ignore_ascii_case(crate::recipe::EXPECTED_INITIAL_APT_HELPER_SHA256)
    {
        return Err(AptIndexError::Invalid(
            "apt-helper differs from its reviewed Ubuntu-child pin".to_owned(),
        ));
    }
    decompress_package_index_in_image(
        podman,
        PackageIndexImage::Pinned(image),
        PackageIndexDecompressor::AptHelper {
            path: apt_helper_path,
            sha256: apt_helper_sha256,
        },
        index,
        packages_xz_path,
        command_id,
        run,
    )
}

pub(crate) fn decompress_package_index_with_verified_local(
    podman: &PodmanExecutor<'_>,
    image: &VerifiedLocalImage,
    xz_path: &str,
    xz_sha256: &str,
    index: VerifiedPackageIndex,
    packages_xz_path: &Path,
    command_id: &str,
    run: &RunDirectory,
) -> Result<VerifiedDecompressedIndex, AptIndexError> {
    decompress_package_index_in_image(
        podman,
        PackageIndexImage::VerifiedLocal(image),
        PackageIndexDecompressor::Xz {
            path: xz_path,
            sha256: xz_sha256,
        },
        index,
        packages_xz_path,
        command_id,
        run,
    )
}

enum PackageIndexImage<'a> {
    Pinned(&'a str),
    VerifiedLocal(&'a VerifiedLocalImage),
}

#[derive(Clone, Copy)]
enum PackageIndexDecompressor<'a> {
    Xz { path: &'a str, sha256: &'a str },
    AptHelper { path: &'a str, sha256: &'a str },
}

impl<'a> PackageIndexDecompressor<'a> {
    fn path(self) -> &'a str {
        match self {
            Self::Xz { path, .. } | Self::AptHelper { path, .. } => path,
        }
    }

    fn sha256(self) -> &'a str {
        match self {
            Self::Xz { sha256, .. } | Self::AptHelper { sha256, .. } => sha256,
        }
    }

    fn name(self) -> &'static str {
        match self {
            Self::Xz { .. } => "xz",
            Self::AptHelper { .. } => "apt-helper",
        }
    }

    fn arguments(self, input_path: &str) -> Vec<String> {
        match self {
            Self::Xz { .. } => vec![
                "--decompress".to_owned(),
                "--stdout".to_owned(),
                "--no-warn".to_owned(),
                input_path.to_owned(),
            ],
            Self::AptHelper { .. } => {
                vec!["cat-file".to_owned(), input_path.to_owned()]
            }
        }
    }
}

fn decompress_package_index_in_image(
    podman: &PodmanExecutor<'_>,
    image: PackageIndexImage<'_>,
    decompressor: PackageIndexDecompressor<'_>,
    index: VerifiedPackageIndex,
    packages_xz_path: &Path,
    command_id: &str,
    run: &RunDirectory,
) -> Result<VerifiedDecompressedIndex, AptIndexError> {
    if !Path::new(decompressor.path()).is_absolute() || !is_sha256(decompressor.sha256()) {
        return Err(AptIndexError::Invalid(
            "in-image decompressor path or executable hash is invalid".to_owned(),
        ));
    }
    let metadata = fs::symlink_metadata(packages_xz_path)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() != index.packages_index_size
        || !sha256_file(packages_xz_path)?.eq_ignore_ascii_case(&index.packages_index_sha256)
    {
        return Err(AptIndexError::Invalid(
            "compressed Packages index changed after signature verification".to_owned(),
        ));
    }
    let run_root = fs::canonicalize(run.path())?;
    let source = fs::canonicalize(packages_xz_path)?;
    if !source.starts_with(&run_root) {
        return Err(AptIndexError::Invalid(
            "compressed Packages index is outside the private run directory".to_owned(),
        ));
    }
    let relative = source
        .strip_prefix(&run_root)
        .map_err(|_| {
            AptIndexError::Invalid("compressed Packages index escaped its private run".to_owned())
        })?
        .to_string_lossy()
        .replace('\\', "/");
    let mounts = [ContainerMount {
        host_path: run_root.clone(),
        container_path: "/work".to_owned(),
        read_only: true,
    }];
    let input_path = format!("/work/{relative}");
    let args = decompressor.arguments(&input_path);
    let command = match image {
        PackageIndexImage::Pinned(reference) => podman.run_container(
            command_id,
            reference,
            NetworkMode::None,
            decompressor.path(),
            &mounts,
            &args,
        )?,
        PackageIndexImage::VerifiedLocal(reference) => podman.run_verified_container(
            command_id,
            reference,
            NetworkMode::None,
            decompressor.path(),
            &mounts,
            &args,
        )?,
    };
    if command.status_code != 0 {
        return Err(AptIndexError::Invalid(format!(
            "in-image {} exited with status {}",
            decompressor.name(),
            command.status_code
        )));
    }
    let output = run_root.join(&command.stdout_path);
    let output_metadata = fs::symlink_metadata(&output)?;
    if output_metadata.file_type().is_symlink()
        || !output_metadata.is_file()
        || output_metadata.len() > 512 * 1024 * 1024
        || !sha256_file(&output)?.eq_ignore_ascii_case(&command.stdout_sha256)
    {
        return Err(AptIndexError::Invalid(
            "in-image decompressed Packages output is invalid or exceeds its limit".to_owned(),
        ));
    }
    Ok(VerifiedDecompressedIndex {
        index,
        path: output,
        command,
    })
}

fn validate_pin(pin: &PackageIndexPin) -> Result<(), AptIndexError> {
    let snapshot = reqwest::Url::parse(&pin.snapshot_url)
        .map_err(|_| AptIndexError::Invalid("snapshot URL is malformed".to_owned()))?;
    if snapshot.scheme() != "https"
        || snapshot.host_str() != Some("snapshot.ubuntu.com")
        || snapshot.username() != ""
        || snapshot.password().is_some()
        || snapshot.query().is_some()
        || snapshot.fragment().is_some()
        || !snapshot.path().starts_with("/ubuntu/")
        || !["noble", "noble-updates", "noble-security"].contains(&pin.suite.as_str())
        || !["main", "universe"].contains(&pin.component.as_str())
        || pin.architecture != "amd64"
        || !is_sha256(&pin.trusted_keyring_sha256)
        || !is_sha256(&pin.inrelease_sha256)
        || !is_sha256(&pin.packages_index_sha256)
        || pin.packages_index_size == 0
    {
        return Err(AptIndexError::Invalid(
            "package index pin escapes the locked Noble snapshot".to_owned(),
        ));
    }
    Ok(())
}

fn clearsigned_payload(input: &str) -> Result<String, AptIndexError> {
    let mut lines = input.lines();
    if lines.next() != Some("-----BEGIN PGP SIGNED MESSAGE-----") {
        return Err(AptIndexError::Invalid(
            "InRelease is not an OpenPGP clearsigned message".to_owned(),
        ));
    }
    let mut found_separator = false;
    for line in lines.by_ref() {
        if line.is_empty() {
            found_separator = true;
            break;
        }
    }
    if !found_separator {
        return Err(AptIndexError::Invalid(
            "InRelease signed-message headers are incomplete".to_owned(),
        ));
    }
    let mut payload = String::new();
    let mut found_signature = false;
    for line in lines {
        if line == "-----BEGIN PGP SIGNATURE-----" {
            found_signature = true;
            break;
        }
        let unescaped = line.strip_prefix("- ").unwrap_or(line);
        payload.push_str(unescaped);
        payload.push('\n');
    }
    if !found_signature {
        return Err(AptIndexError::Invalid(
            "InRelease has no detached signature block".to_owned(),
        ));
    }
    Ok(payload)
}

fn sha256_entry(payload: &str, expected_path: &str) -> Result<(String, u64), AptIndexError> {
    let lines = payload.lines().collect::<Vec<_>>();
    let Some(section_start) = lines.iter().position(|line| *line == "SHA256:") else {
        return Err(AptIndexError::Invalid(
            "signed InRelease has no SHA256 section".to_owned(),
        ));
    };
    for line in lines.iter().skip(section_start + 1) {
        if line.is_empty() || !line.starts_with(' ') {
            break;
        }
        let fields = line.split_ascii_whitespace().collect::<Vec<_>>();
        if fields.len() != 3 {
            return Err(AptIndexError::Invalid(
                "signed InRelease SHA256 entry is malformed".to_owned(),
            ));
        }
        if fields[2] == expected_path {
            let size = fields[1].parse::<u64>().map_err(|_| {
                AptIndexError::Invalid("signed Packages index size is malformed".to_owned())
            })?;
            if !is_sha256(fields[0]) || size == 0 {
                return Err(AptIndexError::Invalid(
                    "signed Packages index identity is invalid".to_owned(),
                ));
            }
            return Ok((fields[0].to_ascii_lowercase(), size));
        }
    }
    Err(AptIndexError::Invalid(format!(
        "signed InRelease omits Packages index {expected_path}"
    )))
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn sha256_file(path: &Path) -> Result<String, io::Error> {
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = vec![0_u8; 64 * 1024].into_boxed_slice();
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    let mut text = String::with_capacity(64);
    for byte in digest.finalize() {
        use fmt::Write as _;
        write!(&mut text, "{byte:02x}").expect("writing into String cannot fail");
    }
    Ok(text)
}

#[cfg(test)]
mod tests {
    use super::{AptIndexError, PackageIndexDecompressor, clearsigned_payload, sha256_entry};

    const INRELEASE: &str = "-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA512\n\nOrigin: Ubuntu\nSHA256:\n abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd 1234 main/binary-amd64/Packages.xz\n 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef 99 universe/binary-amd64/Packages.xz\nSHA1:\n 1111111111111111111111111111111111111111 99 universe/binary-amd64/Packages.xz\n-----BEGIN PGP SIGNATURE-----\n(signature omitted)\n";

    #[test]
    fn signed_index_parser_reads_only_the_sha256_section() {
        let payload = clearsigned_payload(INRELEASE).expect("signed payload parses");
        assert_eq!(
            sha256_entry(&payload, "universe/binary-amd64/Packages.xz").expect("entry exists"),
            (
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef".to_owned(),
                99
            )
        );
    }

    #[test]
    fn signed_index_parser_rejects_missing_or_malformed_entries() {
        let payload = clearsigned_payload(INRELEASE).expect("signed payload parses");
        assert!(matches!(
            sha256_entry(&payload, "missing/Packages.xz"),
            Err(AptIndexError::Invalid(_))
        ));
        assert!(matches!(
            clearsigned_payload("Origin: unsigned\n"),
            Err(AptIndexError::Invalid(_))
        ));
    }

    #[test]
    fn apt_helper_cat_file_receives_the_verified_xz_extension() {
        let decompressor = PackageIndexDecompressor::AptHelper {
            path: "/usr/lib/apt/apt-helper",
            sha256: "91aa654ba3f5b68b784dd5ce94d625080fb6f7bdded88885120fd979936bcf8b",
        };
        assert_eq!(
            decompressor
                .arguments("/work/verified-apt-snapshot/dists/noble/main/binary-amd64/Packages.xz"),
            [
                "cat-file",
                "/work/verified-apt-snapshot/dists/noble/main/binary-amd64/Packages.xz"
            ]
        );
    }
}
