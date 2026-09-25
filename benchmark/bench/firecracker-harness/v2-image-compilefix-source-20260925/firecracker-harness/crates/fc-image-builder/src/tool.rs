use std::collections::HashSet;
use std::error::Error;
use std::fmt;
use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::thread;
use std::time::Instant;

use sha2::{Digest, Sha256};

use crate::{CommandReceipt, RunDirectory, RunDirectoryError, StagedFile};

const MAX_COMMAND_LOG_BYTES: u64 = 512 * 1024 * 1024;
#[derive(Clone, Debug)]
pub struct ToolPin {
    pub id: String,
    pub path: PathBuf,
    pub sha256: String,
}

#[derive(Clone, Debug)]
pub struct CommandSpec {
    pub id: String,
    pub tool: ToolPin,
    pub args: Vec<String>,
    pub working_directory: PathBuf,
    pub environment: Vec<(String, String)>,
}

#[derive(Debug)]
pub enum ToolError {
    Io(io::Error),
    Invalid(String),
    RunDirectory(RunDirectoryError),
    ReaderThread,
}

impl fmt::Display for ToolError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "external command I/O failed: {error}"),
            Self::Invalid(error) => write!(formatter, "external command rejected: {error}"),
            Self::RunDirectory(error) => write!(formatter, "external command logs unsafe: {error}"),
            Self::ReaderThread => formatter.write_str("external command log reader failed"),
        }
    }
}

impl Error for ToolError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::RunDirectory(error) => Some(error),
            Self::Invalid(_) | Self::ReaderThread => None,
        }
    }
}

impl From<io::Error> for ToolError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<RunDirectoryError> for ToolError {
    fn from(error: RunDirectoryError) -> Self {
        Self::RunDirectory(error)
    }
}

/// Runs one explicitly pinned executable with no shell and a cleared ambient
/// environment. Both output streams are drained concurrently into run-owned
/// files so verbose builds do not accumulate in memory or deadlock on pipes.
#[derive(Clone, Debug)]
pub struct CommandRunner {
    base_path: String,
    source_date_epoch: u64,
}

impl CommandRunner {
    #[must_use]
    pub fn new(base_path: String, source_date_epoch: u64) -> Self {
        Self {
            base_path,
            source_date_epoch,
        }
    }

    /// Execute one locked program with bounded output logs in the private run.
    ///
    /// # Errors
    ///
    /// Returns an error if the executable pin, arguments, working directory,
    /// log files, or process I/O are invalid. A nonzero process status is
    /// recorded in the receipt and is handled by the caller.
    #[allow(clippy::too_many_lines)] // Process setup, stream draining, and receipt assembly form one safety boundary.
    pub fn run(&self, spec: &CommandSpec, run: &RunDirectory) -> Result<CommandReceipt, ToolError> {
        validate_spec(spec)?;
        if spec.tool.id.is_empty() || !is_sha256(&spec.tool.sha256) {
            return Err(ToolError::Invalid(
                "tool lock must have an id and full SHA-256".to_owned(),
            ));
        }
        let tool_metadata = fs::symlink_metadata(&spec.tool.path)?;
        if tool_metadata.file_type().is_symlink() || !tool_metadata.is_file() {
            return Err(ToolError::Invalid(
                "pinned executable must be a regular non-symlink file".to_owned(),
            ));
        }
        let tool_path = fs::canonicalize(&spec.tool.path)?;
        let program_sha256 = sha256_file(&tool_path)?;
        if !program_sha256.eq_ignore_ascii_case(&spec.tool.sha256) {
            return Err(ToolError::Invalid(format!(
                "tool hash mismatch for {}: expected {}, received {program_sha256}",
                spec.tool.id, spec.tool.sha256
            )));
        }

        let run_root = fs::canonicalize(run.path())?;
        let home = run_root.join("home");
        let temp = run_root.join("tmp");
        ensure_run_directory(&home)?;
        ensure_run_directory(&temp)?;
        let workdir_metadata = fs::symlink_metadata(&spec.working_directory)?;
        if workdir_metadata.file_type().is_symlink() || !workdir_metadata.is_dir() {
            return Err(ToolError::Invalid(
                "command working directory must be a regular directory".to_owned(),
            ));
        }
        let working_directory = fs::canonicalize(&spec.working_directory)?;
        if !working_directory.starts_with(&run_root) {
            return Err(ToolError::Invalid(
                "command working directory must remain inside its private run".to_owned(),
            ));
        }

        let stdout_name = format!("{}.stdout.log", spec.id);
        let stderr_name = format!("{}.stderr.log", spec.id);
        let stdout = run.create_staged_file(&stdout_name)?;
        let stderr = run.create_staged_file(&stderr_name)?;
        let mut command = Command::new(&tool_path);
        command
            .args(&spec.args)
            .current_dir(&working_directory)
            .env_clear()
            .env("PATH", &self.base_path)
            .env("HOME", &home)
            .env("TMPDIR", &temp)
            .env("USER", "builder")
            .env("LOGNAME", "builder")
            .env("HOSTNAME", "fc-image-builder")
            .env("LANG", "C.UTF-8")
            .env("LC_ALL", "C.UTF-8")
            .env("TZ", "UTC")
            .env("SOURCE_DATE_EPOCH", self.source_date_epoch.to_string())
            .env(
                "KBUILD_BUILD_TIMESTAMP",
                utc_timestamp(self.source_date_epoch),
            )
            .env("KBUILD_BUILD_USER", "builder")
            .env("KBUILD_BUILD_HOST", "terrabase")
            .env("GIT_CONFIG_NOSYSTEM", "1")
            .env("GIT_CONFIG_GLOBAL", "/dev/null")
            .envs(
                spec.environment
                    .iter()
                    .map(|(key, value)| (key.as_str(), value.as_str())),
            )
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        let started = Instant::now();
        let mut child = command.spawn()?;
        let stdout_pipe = child
            .stdout
            .take()
            .ok_or_else(|| ToolError::Invalid("stdout pipe was not created".to_owned()))?;
        let stderr_pipe = child
            .stderr
            .take()
            .ok_or_else(|| ToolError::Invalid("stderr pipe was not created".to_owned()))?;
        let stdout_thread = thread::spawn(move || copy_log(stdout_pipe, stdout));
        let stderr_thread = thread::spawn(move || copy_log(stderr_pipe, stderr));
        let status = child.wait()?;
        let stdout_receipt = stdout_thread
            .join()
            .map_err(|_| ToolError::ReaderThread)??;
        let stderr_receipt = stderr_thread
            .join()
            .map_err(|_| ToolError::ReaderThread)??;
        let elapsed_ms = started.elapsed().as_millis();
        let status_code = status.code().unwrap_or(-1);

        Ok(CommandReceipt {
            program: tool_path.to_string_lossy().into_owned(),
            program_sha256,
            args: spec.args.clone(),
            status_code,
            stdout_path: stdout_receipt.path,
            stdout_sha256: stdout_receipt.sha256,
            stderr_path: stderr_receipt.path,
            stderr_sha256: stderr_receipt.sha256,
            elapsed_ms,
        })
    }
}

#[derive(Debug)]
struct LogReceipt {
    path: String,
    sha256: String,
}

fn copy_log<R: Read>(mut source: R, mut destination: StagedFile) -> Result<LogReceipt, ToolError> {
    let mut digest = Sha256::new();
    let mut buffer = vec![0_u8; 64 * 1024].into_boxed_slice();
    let mut write_error = None;
    let mut total_bytes = 0_u64;
    loop {
        let read = source.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        total_bytes = total_bytes
            .checked_add(
                u64::try_from(read)
                    .map_err(|_| ToolError::Invalid("command log length exceeds u64".to_owned()))?,
            )
            .ok_or_else(|| ToolError::Invalid("command log length overflows".to_owned()))?;
        digest.update(&buffer[..read]);
        if write_error.is_none() {
            if total_bytes > MAX_COMMAND_LOG_BYTES {
                write_error = Some(io::Error::other(
                    "command output exceeded the 512 MiB log limit",
                ));
            } else if let Err(error) = destination.write_all(&buffer[..read]) {
                write_error = Some(error);
            }
        }
    }
    if let Some(error) = write_error {
        return Err(ToolError::Io(error));
    }
    let path = destination.commit()?;
    Ok(LogReceipt {
        path: path
            .file_name()
            .ok_or_else(|| ToolError::Invalid("command log has no filename".to_owned()))?
            .to_string_lossy()
            .into_owned(),
        sha256: digest_hex(&digest.finalize()),
    })
}

fn validate_spec(spec: &CommandSpec) -> Result<(), ToolError> {
    if !is_safe_id(&spec.id) {
        return Err(ToolError::Invalid(
            "command id must be filename-safe ASCII".to_owned(),
        ));
    }
    if spec.args.iter().any(|argument| argument.contains('\0')) {
        return Err(ToolError::Invalid(
            "command arguments cannot contain NUL".to_owned(),
        ));
    }
    let mut names = HashSet::new();
    for (name, value) in &spec.environment {
        if name.is_empty()
            || name.contains('=')
            || name.contains('\0')
            || value.contains('\0')
            || !names.insert(name)
            || matches!(
                name.as_str(),
                "PATH"
                    | "HOME"
                    | "LANG"
                    | "LC_ALL"
                    | "TZ"
                    | "SOURCE_DATE_EPOCH"
                    | "HTTP_PROXY"
                    | "HTTPS_PROXY"
                    | "ALL_PROXY"
                    | "http_proxy"
                    | "https_proxy"
                    | "all_proxy"
                    | "NO_PROXY"
                    | "no_proxy"
                    | "LD_PRELOAD"
                    | "LD_LIBRARY_PATH"
                    | "LD_AUDIT"
                    | "BASH_ENV"
                    | "ENV"
                    | "PYTHONPATH"
                    | "PYTHONHOME"
                    | "NODE_OPTIONS"
                    | "RUBYOPT"
                    | "PERL5OPT"
                    | "KBUILD_BUILD_TIMESTAMP"
                    | "KBUILD_BUILD_USER"
                    | "KBUILD_BUILD_HOST"
            )
        {
            return Err(ToolError::Invalid(
                "command environment contains an invalid or ambient override".to_owned(),
            ));
        }
    }
    Ok(())
}

fn ensure_run_directory(path: &Path) -> Result<(), ToolError> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_dir() => Err(
            ToolError::Invalid("command private directory is not a regular directory".to_owned()),
        ),
        Ok(_) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            fs::create_dir(path)?;
            Ok(())
        }
        Err(error) => Err(ToolError::Io(error)),
    }
}

fn utc_timestamp(epoch: u64) -> String {
    let seconds = epoch % 86_400;
    let days = i64::try_from(epoch / 86_400).unwrap_or(i64::MAX);
    let shifted = days + 719_468;
    let era = if shifted >= 0 {
        shifted / 146_097
    } else {
        (shifted - 146_096) / 146_097
    };
    let day_of_era = shifted - era * 146_097;
    let year_of_era =
        (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let mut year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_prime = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_prime + 2) / 5 + 1;
    let month = month_prime + if month_prime < 10 { 3 } else { -9 };
    year += i64::from(month <= 2);
    let hour = seconds / 3_600;
    let minute = seconds % 3_600 / 60;
    let second = seconds % 60;
    format!("{year:04}-{month:02}-{day:02} {hour:02}:{minute:02}:{second:02} UTC")
}

fn is_safe_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 140
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
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
    let mut text = String::with_capacity(digest.len() * 2);
    for byte in digest {
        use fmt::Write as _;
        write!(&mut text, "{byte:02x}").expect("writing into String cannot fail");
    }
    text
}

#[cfg(test)]
mod tests {
    use super::{CommandSpec, ToolError, ToolPin, utc_timestamp, validate_spec};
    use std::path::PathBuf;

    fn spec() -> CommandSpec {
        CommandSpec {
            id: "kernel_config".to_owned(),
            tool: ToolPin {
                id: "make".to_owned(),
                path: PathBuf::from("/pinned/bin/make"),
                sha256: "a".repeat(64),
            },
            args: vec!["olddefconfig".to_owned()],
            working_directory: PathBuf::from("/run/work"),
            environment: vec![("KBUILD_BUILD_HOST".to_owned(), "builder".to_owned())],
        }
    }

    #[test]
    fn command_spec_accepts_explicit_build_environment() {
        assert!(validate_spec(&spec()).is_ok());
    }

    #[test]
    fn kernel_timestamp_is_derived_from_source_date_epoch() {
        assert_eq!(utc_timestamp(1_760_348_006), "2025-10-13 09:33:26 UTC");
        assert_eq!(utc_timestamp(0), "1970-01-01 00:00:00 UTC");
    }

    #[test]
    fn command_spec_rejects_shell_and_ambient_environment_inputs() {
        let mut unsafe_id = spec();
        unsafe_id.id = "../escape".to_owned();
        assert!(matches!(
            validate_spec(&unsafe_id),
            Err(ToolError::Invalid(_))
        ));

        let mut proxy = spec();
        proxy.environment = vec![("HTTPS_PROXY".to_owned(), "http://proxy.invalid".to_owned())];
        assert!(matches!(validate_spec(&proxy), Err(ToolError::Invalid(_))));

        let mut nul_argument = spec();
        nul_argument.args = vec!["bad\0argument".to_owned()];
        assert!(matches!(
            validate_spec(&nul_argument),
            Err(ToolError::Invalid(_))
        ));
    }
}
