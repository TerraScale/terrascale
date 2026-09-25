use std::error::Error;
use std::fmt;
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::Path;

use sha2::{Digest, Sha256};

use crate::{
    CommandReceipt, CommandRunner, CommandSpec, ContainerMount, NetworkMode, PodmanError,
    PodmanExecutor, RunDirectory, SignatureReceipt, ToolError, ToolPin, VerifiedLocalImage,
};

#[derive(Debug)]
pub enum SignatureError {
    Io(io::Error),
    Invalid(String),
    Podman(PodmanError),
    Tool(ToolError),
}

impl fmt::Display for SignatureError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "signature verification I/O failed: {error}"),
            Self::Invalid(error) => write!(formatter, "signed input rejected: {error}"),
            Self::Podman(error) => write!(formatter, "pinned signature container failed: {error}"),
            Self::Tool(error) => write!(formatter, "signature verifier failed: {error}"),
        }
    }
}

impl Error for SignatureError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::Podman(error) => Some(error),
            Self::Tool(error) => Some(error),
            Self::Invalid(_) => None,
        }
    }
}

impl From<io::Error> for SignatureError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<ToolError> for SignatureError {
    fn from(error: ToolError) -> Self {
        Self::Tool(error)
    }
}

impl From<PodmanError> for SignatureError {
    fn from(error: PodmanError) -> Self {
        Self::Podman(error)
    }
}

#[derive(Clone, Debug)]
pub struct VerifiedSignature {
    receipt: SignatureReceipt,
    command: CommandReceipt,
}

impl VerifiedSignature {
    #[must_use]
    pub fn receipt(&self) -> &SignatureReceipt {
        &self.receipt
    }

    #[must_use]
    pub fn command_receipt(&self) -> &CommandReceipt {
        &self.command
    }

    #[must_use]
    pub fn into_parts(self) -> (SignatureReceipt, CommandReceipt) {
        (self.receipt, self.command)
    }

    pub(crate) fn from_podman_command(receipt: SignatureReceipt, command: CommandReceipt) -> Self {
        Self { receipt, command }
    }
}

/// Exact inputs required for one `OpenPGP` signature check.
#[derive(Clone, Copy, Debug)]
pub struct SignatureRequest<'a> {
    pub keyring_path: &'a Path,
    pub keyring_source_path: &'a str,
    pub keyring_sha256: &'a str,
    pub signed_path: &'a Path,
    pub signed_sha256: &'a str,
    pub detached_signature: Option<(&'a Path, &'a str)>,
    pub expected_fingerprint: &'a str,
    pub command_id: &'a str,
}

/// Verify a clearsigned file such as Ubuntu's signed `InRelease` with the
/// exact pinned keyring and gpgv executable. The signed input and keyring must
/// already live in the private run directory and match their expected hashes.
///
/// # Errors
///
/// Returns an error unless the pinned verifier reports exactly one valid
/// signature by the expected full primary fingerprint.
pub fn verify_clearsigned_file(
    runner: &CommandRunner,
    gpgv: &ToolPin,
    request: SignatureRequest<'_>,
    run: &RunDirectory,
) -> Result<VerifiedSignature, SignatureError> {
    if request.detached_signature.is_some() {
        return Err(SignatureError::Invalid(
            "clearsigned input must not include a detached signature file".to_owned(),
        ));
    }
    verify_signature(runner, gpgv, request, run)
}

/// Verify a detached `OpenPGP` signature such as Canonical's `SHA256SUMS.gpg`.
/// A successful status line must contain the expected full primary-key
/// fingerprint; a short key ID is never accepted as identity.
///
/// # Errors
///
/// Returns an error unless the pinned verifier reports exactly one valid
/// signature by the expected full primary fingerprint.
pub fn verify_detached_signature(
    runner: &CommandRunner,
    gpgv: &ToolPin,
    request: SignatureRequest<'_>,
    run: &RunDirectory,
) -> Result<VerifiedSignature, SignatureError> {
    if request.detached_signature.is_none() {
        return Err(SignatureError::Invalid(
            "detached signature path and digest are required".to_owned(),
        ));
    }
    verify_signature(runner, gpgv, request, run)
}

/// Verify a signature with `gpgv` inside the pinned Ubuntu image. The host
/// process invokes only the recipe-pinned Podman executable; the initial
/// keyring and verifier identities are checked against copied bytes from the
/// content-addressed image before verification.
///
/// # Errors
///
/// Returns an error unless all mounted inputs match their digests and the
/// pinned in-image verifier reports exactly one valid signature by the
/// expected full primary fingerprint.
pub fn verify_signature_in_podman(
    podman: &PodmanExecutor<'_>,
    image: &str,
    request: SignatureRequest<'_>,
    gpgv_path: &str,
    gpgv_sha256: &str,
    run: &RunDirectory,
) -> Result<VerifiedSignature, SignatureError> {
    verify_signature_in_image(
        podman,
        SignatureImage::Pinned(image),
        request,
        gpgv_path,
        gpgv_sha256,
        run,
    )
}

/// Verify a signature with the already loaded and inspected derived builder.
/// Podman execution is forced to use the verified private-store image with
/// `--pull=never`.
pub(crate) fn verify_signature_in_verified_local(
    podman: &PodmanExecutor<'_>,
    image: &VerifiedLocalImage,
    request: SignatureRequest<'_>,
    gpgv_path: &str,
    gpgv_sha256: &str,
    run: &RunDirectory,
) -> Result<VerifiedSignature, SignatureError> {
    verify_signature_in_image(
        podman,
        SignatureImage::VerifiedLocal(image),
        request,
        gpgv_path,
        gpgv_sha256,
        run,
    )
}

enum SignatureImage<'a> {
    Pinned(&'a str),
    VerifiedLocal(&'a VerifiedLocalImage),
}

fn verify_signature_in_image(
    podman: &PodmanExecutor<'_>,
    image: SignatureImage<'_>,
    request: SignatureRequest<'_>,
    gpgv_path: &str,
    gpgv_sha256: &str,
    run: &RunDirectory,
) -> Result<VerifiedSignature, SignatureError> {
    if !gpgv_path.starts_with('/') || !is_sha256(gpgv_sha256) {
        return Err(SignatureError::Invalid(
            "in-image gpgv path or executable hash is invalid".to_owned(),
        ));
    }
    validate_digest(request.keyring_sha256, "trusted keyring")?;
    validate_digest(request.signed_sha256, "signed source")?;
    validate_fingerprint(request.expected_fingerprint)?;
    if request.keyring_source_path.is_empty() || request.keyring_source_path.contains('\0') {
        return Err(SignatureError::Invalid(
            "trusted keyring source path must be nonempty and contain no NUL".to_owned(),
        ));
    }
    let run_root = fs::canonicalize(run.path())?;
    let keyring = verify_input_file(
        request.keyring_path,
        &run_root,
        request.keyring_sha256,
        "keyring",
    )?;
    let signed = verify_input_file(
        request.signed_path,
        &run_root,
        request.signed_sha256,
        "signed source",
    )?;
    let signature = match request.detached_signature {
        Some((path, expected)) => {
            validate_digest(expected, "detached signature")?;
            Some(verify_input_file(
                path,
                &run_root,
                expected,
                "detached signature",
            )?)
        }
        None => None,
    };
    let run_relative = |path: &Path| -> Result<String, SignatureError> {
        let relative = path.strip_prefix(&run_root).map_err(|_| {
            SignatureError::Invalid("signature input escaped its private run".to_owned())
        })?;
        Ok(format!(
            "/work/{}",
            relative.to_string_lossy().replace('\\', "/")
        ))
    };
    let mut args = vec![
        "--status-fd=1".to_owned(),
        "--keyring".to_owned(),
        run_relative(&keyring)?,
    ];
    if let Some(signature) = &signature {
        args.push(run_relative(signature)?);
    }
    args.push(run_relative(&signed)?);
    let mounts = [ContainerMount {
        host_path: run_root.clone(),
        container_path: "/work".to_owned(),
        read_only: true,
    }];
    let command_id = format!("gpgv_{}", request.command_id);
    let command = match image {
        SignatureImage::Pinned(reference) => podman.run_container(
            &command_id,
            reference,
            NetworkMode::None,
            gpgv_path,
            &mounts,
            &args,
        )?,
        SignatureImage::VerifiedLocal(reference) => podman.run_verified_container(
            &command_id,
            reference,
            NetworkMode::None,
            gpgv_path,
            &mounts,
            &args,
        )?,
    };
    if command.status_code != 0 {
        return Err(SignatureError::Invalid(format!(
            "in-image gpgv exited with status {}",
            command.status_code
        )));
    }
    let status_path = run_root.join(&command.stdout_path);
    let status = fs::read_to_string(&status_path)?;
    validate_gpg_status(&status, request.expected_fingerprint)?;
    let signature_hash = request
        .detached_signature
        .map_or(request.signed_sha256, |(_, hash)| hash);
    Ok(VerifiedSignature::from_podman_command(
        SignatureReceipt {
            signature_sha256: signature_hash.to_owned(),
            keyring_path: request.keyring_source_path.to_owned(),
            keyring_sha256: request.keyring_sha256.to_ascii_lowercase(),
            signer_primary_fingerprint: request.expected_fingerprint.to_ascii_uppercase(),
            verifier: gpgv_path.to_owned(),
            verifier_sha256: gpgv_sha256.to_ascii_lowercase(),
            status: "validsig".to_owned(),
        },
        command,
    ))
}

fn verify_signature(
    runner: &CommandRunner,
    gpgv: &ToolPin,
    request: SignatureRequest<'_>,
    run: &RunDirectory,
) -> Result<VerifiedSignature, SignatureError> {
    if gpgv.id != "gpgv" {
        return Err(SignatureError::Invalid(
            "signature tool lock must identify the gpgv executable".to_owned(),
        ));
    }
    validate_digest(request.keyring_sha256, "trusted keyring")?;
    if request.keyring_source_path.is_empty() || request.keyring_source_path.contains('\0') {
        return Err(SignatureError::Invalid(
            "trusted keyring source path must be nonempty and contain no NUL".to_owned(),
        ));
    }
    validate_digest(request.signed_sha256, "signed source")?;
    validate_fingerprint(request.expected_fingerprint)?;
    let run_root = fs::canonicalize(run.path())?;
    let keyring = verify_input_file(
        request.keyring_path,
        &run_root,
        request.keyring_sha256,
        "keyring",
    )?;
    let signed = verify_input_file(
        request.signed_path,
        &run_root,
        request.signed_sha256,
        "signed source",
    )?;
    let signature = match request.detached_signature {
        Some((path, expected)) => {
            validate_digest(expected, "detached signature")?;
            Some(verify_input_file(
                path,
                &run_root,
                expected,
                "detached signature",
            )?)
        }
        None => None,
    };

    let mut args = vec![
        "--status-fd=1".to_owned(),
        "--keyring".to_owned(),
        keyring.to_string_lossy().into_owned(),
    ];
    if let Some(signature) = &signature {
        args.push(signature.to_string_lossy().into_owned());
    }
    args.push(signed.to_string_lossy().into_owned());
    let command = runner.run(
        &CommandSpec {
            id: request.command_id.to_owned(),
            tool: gpgv.clone(),
            args,
            working_directory: run_root.clone(),
            environment: Vec::new(),
        },
        run,
    )?;
    if command.status_code != 0 {
        return Err(SignatureError::Invalid(format!(
            "gpgv exited with status {}",
            command.status_code
        )));
    }
    let status_path = run_root.join(&command.stdout_path);
    let status = fs::read_to_string(&status_path)?;
    validate_gpg_status(&status, request.expected_fingerprint)?;
    let signature_hash = request
        .detached_signature
        .map_or(request.signed_sha256, |(_, hash)| hash);
    Ok(VerifiedSignature {
        receipt: SignatureReceipt {
            signature_sha256: signature_hash.to_owned(),
            keyring_path: request.keyring_source_path.to_owned(),
            keyring_sha256: request.keyring_sha256.to_owned(),
            signer_primary_fingerprint: request.expected_fingerprint.to_ascii_uppercase(),
            verifier: gpgv.id.clone(),
            verifier_sha256: gpgv.sha256.to_ascii_lowercase(),
            status: "validsig".to_owned(),
        },
        command,
    })
}

fn verify_input_file(
    path: &Path,
    run_root: &Path,
    expected_sha256: &str,
    label: &str,
) -> Result<std::path::PathBuf, SignatureError> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(SignatureError::Invalid(format!(
            "{label} must be a regular non-symlink file"
        )));
    }
    let canonical = fs::canonicalize(path)?;
    if !canonical.starts_with(run_root) {
        return Err(SignatureError::Invalid(format!(
            "{label} is outside the private run directory"
        )));
    }
    let actual = sha256_file(&canonical)?;
    if !actual.eq_ignore_ascii_case(expected_sha256) {
        return Err(SignatureError::Invalid(format!(
            "{label} SHA-256 mismatch: expected {expected_sha256}, received {actual}"
        )));
    }
    Ok(canonical)
}

pub(crate) fn validate_gpg_status(
    status: &str,
    expected_fingerprint: &str,
) -> Result<(), SignatureError> {
    let expected = expected_fingerprint.to_ascii_uppercase();
    let mut valid_signatures = Vec::new();
    for line in status.lines() {
        let Some(payload) = line.strip_prefix("[GNUPG:] ") else {
            continue;
        };
        let fields = payload.split_ascii_whitespace().collect::<Vec<_>>();
        match fields.first().copied() {
            Some("VALIDSIG") => {
                let fingerprints = fields
                    .iter()
                    .skip(1)
                    .filter(|field| is_fingerprint(field))
                    .map(|field| field.to_ascii_uppercase())
                    .collect::<Vec<_>>();
                valid_signatures.push(fingerprints);
            }
            Some(
                "BADSIG" | "ERRSIG" | "NO_PUBKEY" | "REVKEYSIG" | "EXPKEYSIG" | "KEYEXPIRED"
                | "SIGEXPIRED" | "KEYREVOKED",
            ) => {
                return Err(SignatureError::Invalid(format!(
                    "gpgv reported a failed signature status: {}",
                    fields[0]
                )));
            }
            _ => {}
        }
    }
    if valid_signatures.len() != 1
        || !valid_signatures[0]
            .iter()
            .any(|fingerprint| fingerprint == &expected)
    {
        return Err(SignatureError::Invalid(
            "gpgv did not report exactly one valid signature by the pinned primary key".to_owned(),
        ));
    }
    Ok(())
}

fn validate_digest(value: &str, label: &str) -> Result<(), SignatureError> {
    if !is_sha256(value) {
        return Err(SignatureError::Invalid(format!(
            "{label} pin is not a full SHA-256 digest"
        )));
    }
    Ok(())
}

fn validate_fingerprint(value: &str) -> Result<(), SignatureError> {
    if !is_fingerprint(value) {
        return Err(SignatureError::Invalid(
            "OpenPGP signer pin must be a complete fingerprint".to_owned(),
        ));
    }
    Ok(())
}

fn is_fingerprint(value: &str) -> bool {
    [40, 64].contains(&value.len()) && value.bytes().all(|byte| byte.is_ascii_hexdigit())
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
    Ok(digest_hex(&digest.finalize()))
}

fn digest_hex(digest: &[u8]) -> String {
    let mut output = String::with_capacity(digest.len() * 2);
    for byte in digest {
        use fmt::Write as _;
        write!(&mut output, "{byte:02x}").expect("writing into String cannot fail");
    }
    output
}

#[cfg(test)]
mod tests {
    use super::{SignatureError, validate_gpg_status};

    const FINGERPRINT: &str = "843938DF228D22F7B3742BC0D94AA3F0EFE21092";

    #[test]
    fn gpg_status_requires_full_pinned_primary_fingerprint() {
        let valid = format!(
            "[GNUPG:] GOODSIG 0D94AA3F0EFE21092 Ubuntu CD Image\n[GNUPG:] VALIDSIG {FINGERPRINT} 2026-09-10 1 0 4 0 1 10 00\n"
        );
        assert!(validate_gpg_status(&valid, FINGERPRINT).is_ok());

        let short_key_only = "[GNUPG:] GOODSIG 0D94AA3F0EFE21092 Ubuntu CD Image\n";
        assert!(matches!(
            validate_gpg_status(short_key_only, FINGERPRINT),
            Err(SignatureError::Invalid(_))
        ));
        let wrong_signer = "[GNUPG:] VALIDSIG 0123456789ABCDEF0123456789ABCDEF01234567 2026-09-10 1 0 4 0 1 10 00\n";
        assert!(matches!(
            validate_gpg_status(wrong_signer, FINGERPRINT),
            Err(SignatureError::Invalid(_))
        ));
    }

    #[test]
    fn gpg_status_rejects_any_reported_signature_failure() {
        let mixed = format!(
            "[GNUPG:] VALIDSIG {FINGERPRINT} 2026-09-10 1 0 4 0 1 10 00\n[GNUPG:] BADSIG DEADBEEF Invalid\n"
        );
        assert!(matches!(
            validate_gpg_status(&mixed, FINGERPRINT),
            Err(SignatureError::Invalid(_))
        ));
    }
}
