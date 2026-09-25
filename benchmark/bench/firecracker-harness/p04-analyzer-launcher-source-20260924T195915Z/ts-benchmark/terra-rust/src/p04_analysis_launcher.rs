//! Stage a sealed flat P04 campaign for the frozen analyzer inside one guest.
//!
//! The controller stages this launcher as its primary program and the analyzer
//! ELF and campaign bundle as two supplemental payloads. Inputs stay outside
//! the guest output archive; only the analyzer reports and this receipt leave
//! the VM.

use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::{Cursor, Read, Seek, Write};
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode, ExitStatus};

use anyhow::{Context, Result, ensure};
use clap::Parser;
use flate2::bufread::GzDecoder;
use serde::Serialize;
use sha2::{Digest, Sha256};
use tar::Archive;

const BUNDLE_PATH: &str = "/root/fc-harness-payloads/p04_a2_bundle";
const ANALYZER_PATH: &str = "/root/fc-harness-payloads/p04_analyzer";
const DATA_ROOT: &str = "/root/fc-bench-data";
const OUTPUT_ROOT: &str = "/root/fc-harness-output";
const MAX_COMPRESSED_BYTES: u64 = 128 << 20;
const MAX_TAR_BYTES: u64 = 512 << 20;
const MAX_TAR_RECORDS: usize = 256;
const MAX_CAMPAIGN_BYTES: u64 = 1 << 20;

#[derive(Parser)]
#[command(about = "Run the frozen P04 analyzer against a bounded guest campaign bundle")]
struct Args {
    /// Flat campaign directory name, for example p04-repeat-20260924-a2.
    #[arg(long)]
    campaign_dir: String,
    /// SHA-256 of the exact compressed bundle staged by the controller.
    #[arg(long)]
    bundle_sha256: String,
    /// SHA-256 of the exact analyzer ELF staged by the controller.
    #[arg(long)]
    analyzer_sha256: String,
    /// SHA-256 of the unchanged campaign.json inside the bundle.
    #[arg(long)]
    campaign_sha256: String,
}

struct Paths {
    bundle: PathBuf,
    analyzer: PathBuf,
    data_root: PathBuf,
    output_root: PathBuf,
}

impl Paths {
    fn guest() -> Self {
        Self {
            bundle: BUNDLE_PATH.into(),
            analyzer: ANALYZER_PATH.into(),
            data_root: DATA_ROOT.into(),
            output_root: OUTPUT_ROOT.into(),
        }
    }
}

#[derive(Debug, Serialize)]
struct InputHash {
    name: String,
    sha256: String,
    size_bytes: u64,
}

#[derive(Serialize)]
struct Receipt {
    schema_version: u32,
    status: &'static str,
    campaign_dir: String,
    launcher_sha256: String,
    analyzer_sha256: String,
    bundle_sha256: String,
    campaign_sha256: String,
    input_files: Vec<InputHash>,
    analyzer_argv: Vec<String>,
    analyzer_working_directory: String,
    analyzer_exit_code: Option<i32>,
    error: Option<String>,
}

fn main() -> ExitCode {
    let args = Args::parse();
    let paths = Paths::guest();
    let mut receipt = Receipt {
        schema_version: 1,
        status: "failed",
        campaign_dir: args.campaign_dir.clone(),
        launcher_sha256: hash_file(&std::env::current_exe().unwrap_or_default())
            .unwrap_or_default(),
        analyzer_sha256: args.analyzer_sha256.clone(),
        bundle_sha256: args.bundle_sha256.clone(),
        campaign_sha256: args.campaign_sha256.clone(),
        input_files: Vec::new(),
        analyzer_argv: Vec::new(),
        analyzer_working_directory: paths.output_root.display().to_string(),
        analyzer_exit_code: None,
        error: None,
    };
    let code = match launch(&args, &paths, &mut receipt) {
        Ok(status) => {
            receipt.analyzer_exit_code = status.code();
            if status.success() {
                receipt.status = "passed";
                0
            } else {
                receipt.error = Some(format!("analyzer exited with {status}"));
                status
                    .code()
                    .filter(|code| (1..=255).contains(code))
                    .unwrap_or(1)
            }
        }
        Err(error) => {
            receipt.error = Some(format!("{error:#}"));
            eprintln!("{error:#}");
            1
        }
    };
    if let Err(error) = write_receipt(&paths.output_root, &receipt) {
        eprintln!("could not write launcher receipt: {error:#}");
        return ExitCode::FAILURE;
    }
    ExitCode::from(u8::try_from(code).unwrap_or(1))
}

fn launch(args: &Args, paths: &Paths, receipt: &mut Receipt) -> Result<ExitStatus> {
    ensure!(
        safe_component(&args.campaign_dir),
        "unsafe campaign directory name"
    );
    ensure!(
        valid_sha256(&receipt.launcher_sha256),
        "launcher self-hash is unavailable"
    );
    for (name, value) in [
        ("bundle", &args.bundle_sha256),
        ("analyzer", &args.analyzer_sha256),
        ("campaign", &args.campaign_sha256),
    ] {
        ensure!(valid_sha256(value), "{name} SHA-256 must be lowercase hex");
    }
    require_directory(&paths.data_root)?;
    require_directory(&paths.output_root)?;
    require_regular(&paths.bundle)?;
    require_regular(&paths.analyzer)?;
    ensure!(
        fs::metadata(&paths.analyzer)?.permissions().mode() & 0o111 != 0,
        "analyzer payload is not executable"
    );
    ensure!(
        hash_file(&paths.analyzer)? == args.analyzer_sha256,
        "analyzer ELF SHA-256 differs from the declared hash"
    );

    let input_parent = paths.data_root.join("p04-analyzer-input");
    fs::create_dir(&input_parent).context("create fresh input parent")?;
    let input_dir = input_parent.join(&args.campaign_dir);
    fs::create_dir(&input_dir).context("create fresh campaign input directory")?;
    let compressed = read_bounded(&paths.bundle, MAX_COMPRESSED_BYTES)?;
    ensure!(
        hex_hash(&compressed) == args.bundle_sha256,
        "campaign bundle SHA-256 differs from the declared hash"
    );
    let decoded_path = input_dir.join(".launcher-decoded.tar");
    decode_gzip(&compressed, &decoded_path)?;
    let unpack_result = unpack_flat_tar(&decoded_path, &input_dir);
    fs::remove_file(&decoded_path).context("remove temporary decoded tar")?;
    receipt.input_files = unpack_result?;
    let campaign = input_dir.join("campaign.json");
    ensure!(campaign.is_file(), "campaign bundle lacks campaign.json");
    let mut campaign_bytes = Vec::new();
    File::open(&campaign)?
        .take(MAX_CAMPAIGN_BYTES + 1)
        .read_to_end(&mut campaign_bytes)?;
    ensure!(
        u64::try_from(campaign_bytes.len())? <= MAX_CAMPAIGN_BYTES,
        "campaign.json exceeds manifest size limit"
    );
    ensure!(
        hex_hash(&campaign_bytes) == args.campaign_sha256,
        "campaign.json SHA-256 differs from the sealed manifest"
    );
    let campaign_id = serde_json::from_slice::<serde_json::Value>(&campaign_bytes)
        .context("parse hash-verified campaign.json")?["campaign_id"]
        .as_str()
        .map(str::to_owned);
    ensure!(
        campaign_id.as_deref() == Some(args.campaign_dir.as_str()),
        "campaign.json campaign_id differs from requested campaign directory"
    );

    let reports = paths
        .output_root
        .join("bench/p04-campaigns")
        .join(&args.campaign_dir)
        .join("reports");
    create_fresh_report_tree(&paths.output_root, &args.campaign_dir)?;
    let relative_reports = PathBuf::from("bench/p04-campaigns")
        .join(&args.campaign_dir)
        .join("reports");
    let argv = analyzer_argv(&campaign, &relative_reports);
    receipt.analyzer_argv = argv.clone();
    ensure!(reports.is_dir(), "fresh reports directory is missing");
    analyzer_command(&paths.analyzer, &paths.output_root, &argv)
        .status()
        .context("spawn frozen P04 analyzer")
}

fn analyzer_command(analyzer: &Path, output_root: &Path, argv: &[String]) -> Command {
    let mut command = Command::new(analyzer);
    command.args(argv).current_dir(output_root);
    command
}

fn analyzer_argv(campaign: &Path, reports: &Path) -> Vec<String> {
    vec![
        "analyze-p04".into(),
        "--campaign".into(),
        campaign.display().to_string(),
        "--json-out".into(),
        reports.join("analysis.json").display().to_string(),
        "--markdown-out".into(),
        reports.join("analysis.md").display().to_string(),
    ]
}

fn create_fresh_report_tree(output_root: &Path, campaign_dir: &str) -> Result<()> {
    let bench = output_root.join("bench");
    fs::create_dir(&bench).context("create fresh bench output directory")?;
    let campaigns = bench.join("p04-campaigns");
    fs::create_dir(&campaigns).context("create fresh P04 output directory")?;
    let campaign = campaigns.join(campaign_dir);
    fs::create_dir(&campaign).context("create fresh campaign report directory")?;
    fs::create_dir(campaign.join("reports")).context("create fresh reports directory")?;
    Ok(())
}

fn decode_gzip(compressed: &[u8], decoded_path: &Path) -> Result<()> {
    decode_gzip_limited(compressed, decoded_path, MAX_TAR_BYTES)
}

fn decode_gzip_limited(compressed: &[u8], decoded_path: &Path, max_bytes: u64) -> Result<()> {
    let mut decoded = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(decoded_path)?;
    let decoder = GzDecoder::new(Cursor::new(compressed));
    let mut limited = decoder.take(max_bytes + 1);
    let bytes = std::io::copy(&mut limited, &mut decoded).context("decode campaign gzip")?;
    ensure!(
        bytes <= max_bytes,
        "campaign tar exceeds decoded size limit"
    );
    ensure!(
        limited.into_inner().into_inner().position() == u64::try_from(compressed.len())?,
        "campaign gzip has trailing or concatenated data"
    );
    decoded.sync_all()?;
    Ok(())
}

fn unpack_flat_tar(decoded_path: &Path, input_dir: &Path) -> Result<Vec<InputHash>> {
    let tar_bytes = fs::metadata(decoded_path)?.len();
    ensure!(
        tar_bytes >= 1024 && tar_bytes % 512 == 0,
        "campaign tar is missing its aligned end records"
    );
    let mut archive = Archive::new(File::open(decoded_path)?);
    let mut names = HashSet::new();
    let mut files = Vec::new();
    let mut records = 0;
    let mut root_seen = false;
    for entry in archive.entries()?.raw(true) {
        records += 1;
        ensure!(records <= MAX_TAR_RECORDS, "too many campaign tar records");
        let mut entry = entry.context("read campaign tar entry")?;
        let raw = entry.path_bytes();
        if raw.as_ref() == b"./" {
            ensure!(!root_seen, "duplicate campaign tar root");
            ensure!(
                entry.header().entry_type().is_dir() && entry.size() == 0,
                "campaign tar root is not an empty directory"
            );
            root_seen = true;
            continue;
        }
        let name = flat_member_name(&raw)?;
        ensure!(names.insert(name.clone()), "duplicate campaign file {name}");
        ensure!(
            entry.header().entry_type().is_file(),
            "campaign member {name} is not a regular file"
        );
        let size = entry.size();
        ensure!(size <= MAX_TAR_BYTES, "campaign member {name} is too large");
        let path = input_dir.join(&name);
        let mut output = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)?;
        let copied = std::io::copy(&mut entry, &mut output)?;
        ensure!(copied == size, "campaign member {name} is truncated");
        output.sync_all()?;
        files.push(InputHash {
            name,
            sha256: hash_file(&path)?,
            size_bytes: copied,
        });
    }
    let mut tail = archive.into_inner();
    let consumed = tail.stream_position()?;
    ensure!(consumed < tar_bytes, "campaign tar lacks end records");
    let remaining = tar_bytes - consumed;
    ensure!(remaining >= 512, "campaign tar has incomplete end records");
    let mut left = remaining;
    let mut block = [0_u8; 64 * 1024];
    while left > 0 {
        let size = usize::try_from(left.min(block.len() as u64))?;
        tail.read_exact(&mut block[..size])?;
        ensure!(
            block[..size].iter().all(|byte| *byte == 0),
            "campaign tar has nonzero postlude"
        );
        left -= u64::try_from(size)?;
    }
    ensure!(
        names.contains("campaign.json"),
        "campaign tar lacks campaign.json"
    );
    files.sort_by(|left, right| left.name.cmp(&right.name));
    Ok(files)
}

fn flat_member_name(raw: &[u8]) -> Result<String> {
    let raw = raw.strip_prefix(b"./").unwrap_or(raw);
    let name = std::str::from_utf8(raw).context("campaign filename is not UTF-8")?;
    ensure!(
        safe_component(name),
        "unsafe or nested campaign filename {name:?}"
    );
    Ok(name.to_owned())
}

fn safe_component(name: &str) -> bool {
    !name.is_empty()
        && name != "."
        && name != ".."
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_'))
        && !name.starts_with('.')
}

fn valid_sha256(hash: &str) -> bool {
    hash.len() == 64
        && hash
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn require_directory(path: &Path) -> Result<()> {
    ensure!(
        fs::symlink_metadata(path)?.file_type().is_dir(),
        "{} is not a real directory",
        path.display()
    );
    Ok(())
}

fn require_regular(path: &Path) -> Result<()> {
    ensure!(
        fs::symlink_metadata(path)?.file_type().is_file(),
        "{} is not a regular file",
        path.display()
    );
    Ok(())
}

fn read_bounded(path: &Path, max: u64) -> Result<Vec<u8>> {
    ensure!(
        fs::metadata(path)?.len() <= max,
        "bundle exceeds compressed size limit"
    );
    let mut bytes = Vec::new();
    File::open(path)?.take(max + 1).read_to_end(&mut bytes)?;
    ensure!(
        u64::try_from(bytes.len())? <= max,
        "bundle grew past compressed size limit"
    );
    Ok(bytes)
}

fn hash_file(path: &Path) -> Result<String> {
    let mut file = File::open(path)?;
    let mut hash = Sha256::new();
    let mut block = [0_u8; 64 * 1024];
    loop {
        let count = file.read(&mut block)?;
        if count == 0 {
            break;
        }
        hash.update(&block[..count]);
    }
    Ok(format!("{:x}", hash.finalize()))
}

fn hex_hash(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn write_receipt(output_root: &Path, receipt: &Receipt) -> Result<()> {
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(output_root.join("p04-launcher-receipt.json"))?;
    serde_json::to_writer_pretty(&mut file, receipt)?;
    file.write_all(b"\n")?;
    file.sync_all()?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicU64, Ordering};

    use flate2::Compression;
    use flate2::write::GzEncoder;
    use tar::{Builder, EntryType, Header};

    use super::*;

    static NEXT_TEST: AtomicU64 = AtomicU64::new(0);

    struct TestDir(PathBuf);

    impl TestDir {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "p04-analysis-launcher-{}-{}",
                std::process::id(),
                NEXT_TEST.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir(&path).unwrap();
            Self(path)
        }
    }

    impl Drop for TestDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn bundle(files: &[(&str, &[u8])]) -> Vec<u8> {
        let encoder = GzEncoder::new(Vec::new(), Compression::default());
        let mut tar = Builder::new(encoder);
        for (name, content) in files {
            let mut header = Header::new_ustar();
            header.set_mode(0o644);
            header.set_size(content.len() as u64);
            header.set_cksum();
            tar.append_data(&mut header, *name, *content).unwrap();
        }
        tar.into_inner().unwrap().finish().unwrap()
    }

    fn receipt(args: &Args) -> Receipt {
        Receipt {
            schema_version: 1,
            status: "failed",
            campaign_dir: args.campaign_dir.clone(),
            launcher_sha256: hex_hash(b"test launcher"),
            analyzer_sha256: args.analyzer_sha256.clone(),
            bundle_sha256: args.bundle_sha256.clone(),
            campaign_sha256: args.campaign_sha256.clone(),
            input_files: Vec::new(),
            analyzer_argv: Vec::new(),
            analyzer_working_directory: String::new(),
            analyzer_exit_code: None,
            error: None,
        }
    }

    #[test]
    fn rejects_oversized_and_trailing_gzip() {
        let temp = TestDir::new();
        let bytes = bundle(&[("campaign.json", b"{}")]);
        let source = temp.0.join("bundle");
        fs::write(&source, b"12345").unwrap();
        assert!(
            read_bounded(&source, 4)
                .unwrap_err()
                .to_string()
                .contains("compressed size limit")
        );
        assert!(
            decode_gzip_limited(&bytes, &temp.0.join("small.tar"), 32)
                .unwrap_err()
                .to_string()
                .contains("decoded size limit")
        );
        let mut trailing = bytes;
        trailing.extend_from_slice(b"trailing");
        assert!(
            decode_gzip(&trailing, &temp.0.join("trailing.tar"))
                .unwrap_err()
                .to_string()
                .contains("trailing or concatenated")
        );
    }

    #[test]
    fn rejects_aliases_links_and_nonzero_tar_postlude() {
        let temp = TestDir::new();
        let decoded = temp.0.join("decoded.tar");
        let input = temp.0.join("input");
        fs::create_dir(&input).unwrap();

        let bytes = bundle(&[("campaign.json", b"{}"), ("./campaign.json", b"{}")]);
        decode_gzip(&bytes, &decoded).unwrap();
        assert!(
            unpack_flat_tar(&decoded, &input)
                .unwrap_err()
                .to_string()
                .contains("duplicate campaign file")
        );
        fs::remove_file(&decoded).unwrap();
        fs::remove_dir_all(&input).unwrap();
        fs::create_dir(&input).unwrap();

        let bytes = bundle(&[("nested/campaign.json", b"{}")]);
        decode_gzip(&bytes, &decoded).unwrap();
        assert!(
            unpack_flat_tar(&decoded, &input)
                .unwrap_err()
                .to_string()
                .contains("unsafe or nested")
        );
        fs::remove_file(&decoded).unwrap();
        fs::remove_dir_all(&input).unwrap();
        fs::create_dir(&input).unwrap();

        let encoder = GzEncoder::new(Vec::new(), Compression::default());
        let mut tar = Builder::new(encoder);
        let mut header = Header::new_ustar();
        header.set_path("campaign.json").unwrap();
        header.set_entry_type(EntryType::Symlink);
        header.set_link_name("other").unwrap();
        header.set_size(0);
        header.set_cksum();
        tar.append(&header, std::io::empty()).unwrap();
        let bytes = tar.into_inner().unwrap().finish().unwrap();
        decode_gzip(&bytes, &decoded).unwrap();
        assert!(
            unpack_flat_tar(&decoded, &input)
                .unwrap_err()
                .to_string()
                .contains("not a regular file")
        );
        fs::remove_file(&decoded).unwrap();
        fs::remove_dir_all(&input).unwrap();
        fs::create_dir(&input).unwrap();

        let bytes = bundle(&[("campaign.json", b"{}")]);
        decode_gzip(&bytes, &decoded).unwrap();
        let mut raw = fs::read(&decoded).unwrap();
        *raw.last_mut().unwrap() = 1;
        fs::write(&decoded, raw).unwrap();
        assert!(
            unpack_flat_tar(&decoded, &input)
                .unwrap_err()
                .to_string()
                .contains("nonzero postlude")
        );
    }

    #[test]
    fn binds_campaign_bytes_and_propagates_child_failure() {
        let temp = TestDir::new();
        let data_root = temp.0.join("data");
        let output_root = temp.0.join("output");
        fs::create_dir(&data_root).unwrap();
        fs::create_dir(&output_root).unwrap();
        let campaign = b"{\"campaign_id\":\"p04-repeat-20260924-a2\"}";
        let bytes = bundle(&[("campaign.json", campaign), ("evidence.json", b"{}")]);
        let bundle_path = temp.0.join("bundle.gz");
        fs::write(&bundle_path, &bytes).unwrap();
        let analyzer = PathBuf::from("/usr/bin/false");
        let paths = Paths {
            bundle: bundle_path,
            analyzer: analyzer.clone(),
            data_root,
            output_root: output_root.clone(),
        };
        let mut args = Args {
            campaign_dir: "p04-repeat-20260924-a2".into(),
            bundle_sha256: hex_hash(&bytes),
            analyzer_sha256: hash_file(&analyzer).unwrap(),
            campaign_sha256: hex_hash(b"wrong campaign"),
        };
        assert!(
            launch(&args, &paths, &mut receipt(&args))
                .unwrap_err()
                .to_string()
                .contains("campaign.json SHA-256")
        );
        let staged = paths
            .data_root
            .join("p04-analyzer-input/p04-repeat-20260924-a2/campaign.json");
        assert_eq!(fs::read(staged).unwrap(), campaign);

        fs::remove_dir_all(paths.data_root.join("p04-analyzer-input")).unwrap();
        args.campaign_sha256 = hex_hash(campaign);
        let mut evidence = receipt(&args);
        let status = launch(&args, &paths, &mut evidence).unwrap();
        assert_eq!(status.code(), Some(1));
        assert_eq!(evidence.input_files.len(), 2);
        assert_eq!(evidence.analyzer_argv[0], "analyze-p04");
        assert!(evidence.analyzer_argv[2].ends_with("/campaign.json"));
        assert_eq!(
            evidence.analyzer_argv[4],
            "bench/p04-campaigns/p04-repeat-20260924-a2/reports/analysis.json"
        );
        assert!(
            output_root
                .join("bench/p04-campaigns/p04-repeat-20260924-a2/reports")
                .is_dir()
        );
        let child = analyzer_command(&analyzer, &output_root, &evidence.analyzer_argv);
        assert_eq!(child.get_current_dir(), Some(output_root.as_path()));
        assert_eq!(child.get_args().count(), 7);
    }

    #[test]
    fn rejects_campaign_identity_mismatch_before_child_or_reports() {
        let temp = TestDir::new();
        let data_root = temp.0.join("data");
        let output_root = temp.0.join("output");
        fs::create_dir(&data_root).unwrap();
        fs::create_dir(&output_root).unwrap();
        let campaign = b"{\"campaign_id\":\"p04-repeat-20260924-a1\"}";
        let bytes = bundle(&[("campaign.json", campaign)]);
        let bundle_path = temp.0.join("bundle.gz");
        fs::write(&bundle_path, &bytes).unwrap();
        let analyzer = PathBuf::from("/usr/bin/false");
        let paths = Paths {
            bundle: bundle_path,
            analyzer: analyzer.clone(),
            data_root,
            output_root: output_root.clone(),
        };
        let args = Args {
            campaign_dir: "p04-repeat-20260924-a2".into(),
            bundle_sha256: hex_hash(&bytes),
            analyzer_sha256: hash_file(&analyzer).unwrap(),
            campaign_sha256: hex_hash(campaign),
        };
        let mut evidence = receipt(&args);
        assert!(
            launch(&args, &paths, &mut evidence)
                .unwrap_err()
                .to_string()
                .contains("campaign_id differs")
        );
        assert!(evidence.analyzer_argv.is_empty());
        assert!(evidence.analyzer_exit_code.is_none());
        assert!(!output_root.join("bench").exists());
        write_receipt(&output_root, &evidence).unwrap();
        let written: serde_json::Value = serde_json::from_slice(
            &fs::read(output_root.join("p04-launcher-receipt.json")).unwrap(),
        )
        .unwrap();
        assert_eq!(written["status"], "failed");
    }
}
