//! Read-only cgroup-v2 evidence for planning guest process tests.

use std::fs;
use std::os::unix::fs::MetadataExt;
use std::path::{Path, PathBuf};

use serde::Serialize;

#[derive(Serialize)]
struct Reading<T> {
    value: Option<T>,
    error: Option<String>,
}

impl<T> Reading<T> {
    fn ok(value: T) -> Self {
        Self {
            value: Some(value),
            error: None,
        }
    }

    fn error(error: impl Into<String>) -> Self {
        Self {
            value: None,
            error: Some(error.into()),
        }
    }
}

#[derive(Serialize)]
struct DirectoryInfo {
    uid: u32,
    gid: u32,
    mode_octal: String,
}

#[derive(Serialize)]
struct MountInfo {
    raw_entry: String,
    root: String,
    mount_point: String,
    mount_options: String,
    super_options: String,
    read_write: bool,
}

#[derive(Serialize)]
struct CgroupFiles {
    directory: Reading<DirectoryInfo>,
    controllers: Reading<String>,
    subtree_control: Reading<String>,
    memory_current: Reading<String>,
    memory_max: Reading<String>,
    cgroup_type: Reading<String>,
    cgroup_procs: Reading<String>,
}

#[derive(Serialize)]
struct ObservedPrerequisites {
    mount_read_write: Option<bool>,
    memory_controller_available_to_self: Option<bool>,
    memory_enabled_for_children: Option<bool>,
    self_has_processes: Option<bool>,
    child_creation_proven: bool,
}

#[derive(Serialize)]
struct Report {
    schema_version: u32,
    read_only_probe: bool,
    proc_self_cgroup: Reading<String>,
    cgroup_v2_line: Reading<String>,
    cgroup2_mount: Reading<MountInfo>,
    self_cgroup_path: Reading<String>,
    root: Reading<CgroupFiles>,
    self_cgroup: Reading<CgroupFiles>,
    observed_prerequisites: ObservedPrerequisites,
}

fn read(path: &Path) -> Reading<String> {
    match fs::read_to_string(path) {
        Ok(value) => Reading::ok(value.trim_end_matches('\n').to_owned()),
        Err(error) => Reading::error(format!("{}: {error}", path.display())),
    }
}

fn read_raw(path: &Path) -> Reading<String> {
    match fs::read_to_string(path) {
        Ok(value) => Reading::ok(value),
        Err(error) => Reading::error(format!("{}: {error}", path.display())),
    }
}

fn directory(path: &Path) -> Reading<DirectoryInfo> {
    match fs::metadata(path) {
        Ok(meta) => Reading::ok(DirectoryInfo {
            uid: meta.uid(),
            gid: meta.gid(),
            mode_octal: format!("{:04o}", meta.mode() & 0o7777),
        }),
        Err(error) => Reading::error(format!("{}: {error}", path.display())),
    }
}

fn files(path: &Path) -> CgroupFiles {
    CgroupFiles {
        directory: directory(path),
        controllers: read(&path.join("cgroup.controllers")),
        subtree_control: read(&path.join("cgroup.subtree_control")),
        memory_current: read(&path.join("memory.current")),
        memory_max: read(&path.join("memory.max")),
        cgroup_type: read(&path.join("cgroup.type")),
        cgroup_procs: read(&path.join("cgroup.procs")),
    }
}

fn decode_mount_path(encoded: &str) -> Result<String, String> {
    let bytes = encoded.as_bytes();
    let mut decoded = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'\\' {
            let digits = bytes
                .get(index + 1..index + 4)
                .ok_or("truncated mountinfo escape")?;
            if !digits.iter().all(|digit| (b'0'..=b'7').contains(digit)) {
                return Err("invalid mountinfo escape".to_owned());
            }
            let byte = u16::from(digits[0] - b'0') * 64
                + u16::from(digits[1] - b'0') * 8
                + u16::from(digits[2] - b'0');
            decoded.push(u8::try_from(byte).map_err(|error| error.to_string())?);
            index += 4;
        } else {
            decoded.push(bytes[index]);
            index += 1;
        }
    }
    String::from_utf8(decoded).map_err(|error| error.to_string())
}

fn parse_mount(line: &str) -> Result<MountInfo, String> {
    let (left, right) = line
        .split_once(" - ")
        .ok_or("mountinfo entry has no separator")?;
    let fields: Vec<_> = left.split_whitespace().collect();
    let right_fields: Vec<_> = right.split_whitespace().collect();
    if fields.len() < 6 || right_fields.len() < 3 || right_fields[0] != "cgroup2" {
        return Err("invalid cgroup2 mountinfo entry".to_owned());
    }
    let mount_options = fields[5].to_owned();
    let super_options = right_fields[2].to_owned();
    let read_write = mount_options.split(',').any(|option| option == "rw")
        && super_options.split(',').any(|option| option == "rw");
    Ok(MountInfo {
        raw_entry: line.to_owned(),
        root: decode_mount_path(fields[3])?,
        mount_point: decode_mount_path(fields[4])?,
        mount_options,
        super_options,
        read_write,
    })
}

fn selected_mount(mountinfo: &str, cgroup_path: &Path) -> Reading<MountInfo> {
    let mut selected: Option<MountInfo> = None;
    let mut failures = Vec::new();
    for line in mountinfo
        .lines()
        .filter(|line| line.contains(" - cgroup2 "))
    {
        match parse_mount(line) {
            Ok(mount) if cgroup_path.starts_with(&mount.root) => {
                if selected
                    .as_ref()
                    .is_none_or(|current| mount.root.len() > current.root.len())
                {
                    selected = Some(mount);
                }
            }
            Ok(_) => {}
            Err(error) => failures.push(error),
        }
    }
    selected.map_or_else(
        || {
            Reading::error(format!(
                "no matching cgroup2 mount; parse errors: {failures:?}"
            ))
        },
        Reading::ok,
    )
}

fn self_path(cgroup: &str) -> Reading<String> {
    let mut paths = cgroup.lines().filter_map(|line| line.strip_prefix("0::"));
    match (paths.next(), paths.next()) {
        (Some(path), None) if Path::new(path).is_absolute() => Reading::ok(path.to_owned()),
        _ => Reading::error("expected one absolute 0:: cgroup-v2 path"),
    }
}

fn contains_controller(reading: &Reading<String>, controller: &str) -> Option<bool> {
    reading
        .value
        .as_deref()
        .map(|value| value.split_whitespace().any(|item| item == controller))
}

fn report() -> Report {
    let proc_self_cgroup = read_raw(Path::new("/proc/self/cgroup"));
    let cgroup_v2_line = proc_self_cgroup.value.as_deref().map_or_else(
        || Reading::error("/proc/self/cgroup unavailable"),
        |value| {
            value
                .lines()
                .find(|line| line.starts_with("0::"))
                .map_or_else(
                    || Reading::error("0:: entry absent"),
                    |line| Reading::ok(line.to_owned()),
                )
        },
    );
    let self_cgroup_path = cgroup_v2_line
        .value
        .as_deref()
        .map_or_else(|| Reading::error("cgroup-v2 entry unavailable"), self_path);
    let cgroup2_mount = match (
        fs::read_to_string("/proc/self/mountinfo"),
        self_cgroup_path.value.as_deref(),
    ) {
        (Ok(mountinfo), Some(path)) => selected_mount(&mountinfo, Path::new(path)),
        (Err(error), _) => Reading::error(format!("/proc/self/mountinfo: {error}")),
        (_, None) => Reading::error("self cgroup path unavailable"),
    };
    let (root, self_cgroup) = match (
        cgroup2_mount.value.as_ref(),
        self_cgroup_path.value.as_deref(),
    ) {
        (Some(mount), Some(path)) => {
            let root_path = PathBuf::from(&mount.mount_point);
            let relative = Path::new(path).strip_prefix(&mount.root);
            match relative {
                Ok(relative) => {
                    let self_dir = root_path.join(relative);
                    (
                        Reading::ok(files(&root_path)),
                        Reading::ok(files(&self_dir)),
                    )
                }
                Err(error) => (
                    Reading::error(format!("mount root does not contain self path: {error}")),
                    Reading::error(format!("mount root does not contain self path: {error}")),
                ),
            }
        }
        _ => (
            Reading::error("cgroup2 mount or self path unavailable"),
            Reading::error("cgroup2 mount or self path unavailable"),
        ),
    };
    let self_files: Option<&CgroupFiles> = self_cgroup.value.as_ref();
    let observed_prerequisites = ObservedPrerequisites {
        mount_read_write: cgroup2_mount.value.as_ref().map(|mount| mount.read_write),
        memory_controller_available_to_self: self_files
            .and_then(|files| contains_controller(&files.controllers, "memory")),
        memory_enabled_for_children: self_files
            .and_then(|files| contains_controller(&files.subtree_control, "memory")),
        self_has_processes: self_files.and_then(|files| {
            files
                .cgroup_procs
                .value
                .as_deref()
                .map(|value| !value.is_empty())
        }),
        child_creation_proven: false,
    };
    Report {
        schema_version: 1,
        read_only_probe: true,
        proc_self_cgroup,
        cgroup_v2_line,
        cgroup2_mount,
        self_cgroup_path,
        root,
        self_cgroup,
        observed_prerequisites,
    }
}

fn main() {
    match serde_json::to_string_pretty(&report()) {
        Ok(json) => println!("{json}"),
        Err(error) => {
            eprintln!("cgroup probe: failed to serialize report: {error}");
            std::process::exit(1);
        }
    }
}
