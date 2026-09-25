use std::fmt::Write as _;
use std::fs;
use std::io::{self, Read, Write};
use std::net::{Ipv4Addr, SocketAddrV4, TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::thread;
use std::time::{Duration, Instant};

const PROCESS_MEMORY_LIMIT_BYTES: &str = "134217728";
const PROCESS_MEMORY_HEADROOM_BYTES: &str = "16777216";
const STARTUP_TIMEOUT: Duration = Duration::from_secs(10);
const REQUEST_TIMEOUT: Duration = Duration::from_secs(2);
const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(12);

static NEXT_TEST_DIRECTORY: AtomicUsize = AtomicUsize::new(0);

#[test]
fn packaged_server_serves_loopback_admin_and_drains_on_sigterm() {
    let directory = TestDirectory::new();
    let executable = server_binary();
    let data_dir = directory.path().join("shard");
    let port = available_loopback_port();
    let mut server = RunningServer::spawn(&executable, &data_dir, port);

    wait_until_ready(&mut server);

    let health = exchange(port, b"GET /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n")
        .expect("loopback health request");
    assert!(health.starts_with("HTTP/1.1 200"), "{health}");

    let readiness = exchange(port, b"GET /readyz HTTP/1.1\r\nHost: localhost\r\n\r\n")
        .expect("loopback readiness request");
    assert!(readiness.starts_with("HTTP/1.1 200"), "{readiness}");

    let metrics = exchange(port, b"GET /metrics HTTP/1.1\r\nHost: localhost\r\n\r\n")
        .expect("loopback metrics request");
    assert!(metrics.starts_with("HTTP/1.1 200"), "{metrics}");
    assert!(metrics.contains("terrabase_server_ready 1"), "{metrics}");

    let body = exchange(
        port,
        b"GET /healthz HTTP/1.1\r\nHost: localhost\r\nContent-Length: 4\r\n\r\ndata",
    )
    .expect("body rejection response");
    assert!(body.starts_with("HTTP/1.1 413"), "{body}");

    let mut too_many_headers = String::from("GET /readyz HTTP/1.1\r\nHost: localhost\r\n");
    for index in 0..40 {
        write!(&mut too_many_headers, "X-Test-{index}: value\r\n").expect("write test header");
    }
    too_many_headers.push_str("\r\n");
    let too_many_headers =
        exchange(port, too_many_headers.as_bytes()).expect("header count rejection response");
    assert!(
        too_many_headers.starts_with("HTTP/1.1 431"),
        "{too_many_headers}"
    );

    let oversized_header = format!(
        "GET /readyz HTTP/1.1\r\nHost: localhost\r\nX-Fill: {}\r\n\r\n",
        "x".repeat(20 * 1024)
    );
    let oversized_header =
        exchange(port, oversized_header.as_bytes()).expect("header byte limit response");
    assert!(
        oversized_header.starts_with("HTTP/1.1 431"),
        "{oversized_header}"
    );

    let wrong_method = exchange(port, b"HEAD /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n")
        .expect("method rejection response");
    assert!(wrong_method.starts_with("HTTP/1.1 405"), "{wrong_method}");

    let no_document_endpoint = exchange(
        port,
        b"GET /v1/documents HTTP/1.1\r\nHost: localhost\r\n\r\n",
    )
    .expect("document route rejection response");
    assert!(
        no_document_endpoint.starts_with("HTTP/1.1 404"),
        "{no_document_endpoint}"
    );

    let mut incomplete_header = TcpStream::connect(SocketAddrV4::new(Ipv4Addr::LOCALHOST, port))
        .expect("open a partial-header connection");
    incomplete_header
        .write_all(b"GET /healthz HTTP/1.1\r\nHost:")
        .expect("send an incomplete request header");
    let shutdown_started = Instant::now();
    let status = server.terminate_and_wait();
    drop(incomplete_header);
    assert!(
        shutdown_started.elapsed() < SHUTDOWN_TIMEOUT,
        "shutdown exceeded its deadline"
    );
    assert!(status.success(), "server did not exit cleanly: {status}");
    assert!(!server.is_ready(), "readiness must fail after shutdown");
    assert!(
        data_dir.is_dir(),
        "local shard should be created on success"
    );
}

#[test]
fn invalid_memory_arguments_exit_before_creating_the_data_directory() {
    let directory = TestDirectory::new();
    let executable = server_binary();
    let data_dir = directory.path().join("must-not-be-created");
    let output = Command::new(executable)
        .arg("--data-dir")
        .arg(&data_dir)
        .arg("--memory-limit-bytes")
        .arg("1024")
        .arg("--memory-headroom-bytes")
        .arg("128")
        .arg("--port")
        .arg(available_loopback_port().to_string())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .output()
        .expect("run server with invalid memory settings");

    assert!(!output.status.success());
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("usable memory budget"), "{stderr}");
    assert!(
        !data_dir.exists(),
        "validation must happen before disk access"
    );
}

#[test]
fn packaged_cli_help_exits_successfully_without_server_settings() {
    let output = Command::new(server_binary())
        .arg("--help")
        .output()
        .expect("run server CLI help");

    assert!(output.status.success());
    let help = String::from_utf8_lossy(&output.stdout);
    assert!(help.contains("--data-dir"), "{help}");
    assert!(help.contains("--memory-limit-bytes"), "{help}");
    assert!(help.contains("--memory-headroom-bytes"), "{help}");
    assert!(help.contains("--port"), "{help}");
}

struct RunningServer {
    child: Child,
    port: u16,
}

impl RunningServer {
    fn spawn(executable: &Path, data_dir: &Path, port: u16) -> Self {
        let child = Command::new(executable)
            .arg("--data-dir")
            .arg(data_dir)
            .arg("--memory-limit-bytes")
            .arg(PROCESS_MEMORY_LIMIT_BYTES)
            .arg("--memory-headroom-bytes")
            .arg(PROCESS_MEMORY_HEADROOM_BYTES)
            .arg("--port")
            .arg(port.to_string())
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .expect("spawn packaged TerraBase server");
        Self { child, port }
    }

    fn try_wait(&mut self) -> Option<ExitStatus> {
        self.child
            .try_wait()
            .expect("inspect server process status")
    }

    fn terminate_and_wait(&mut self) -> ExitStatus {
        let output = Command::new("sh")
            .arg("-c")
            .arg("kill -TERM \"$1\"")
            .arg("terrabase-server-process-test")
            .arg(self.child.id().to_string())
            .stdout(Stdio::null())
            .stderr(Stdio::piped())
            .output()
            .expect("send SIGTERM to packaged server");
        assert!(
            output.status.success(),
            "failed to send SIGTERM: {}",
            String::from_utf8_lossy(&output.stderr)
        );

        let deadline = Instant::now() + SHUTDOWN_TIMEOUT;
        loop {
            if let Some(status) = self.try_wait() {
                return status;
            }
            assert!(
                Instant::now() < deadline,
                "server did not drain within {SHUTDOWN_TIMEOUT:?}"
            );
            thread::sleep(Duration::from_millis(20));
        }
    }

    fn is_ready(&mut self) -> bool {
        match exchange(
            self.port,
            b"GET /readyz HTTP/1.1\r\nHost: localhost\r\n\r\n",
        ) {
            Ok(response) => response.starts_with("HTTP/1.1 200"),
            Err(_) => false,
        }
    }
}

impl Drop for RunningServer {
    fn drop(&mut self) {
        if self.child.try_wait().ok().flatten().is_none() {
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
    }
}

struct TestDirectory(PathBuf);

impl TestDirectory {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "terrabase-server-process-{}-{}",
            std::process::id(),
            NEXT_TEST_DIRECTORY.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir_all(&path).expect("create process test directory");
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn available_loopback_port() -> u16 {
    let listener = TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
        .expect("reserve an ephemeral loopback port");
    listener.local_addr().expect("listener address").port()
}

fn server_binary() -> PathBuf {
    if let Some(path) = std::env::var_os("TB_SERVER_TEST_BINARY") {
        let path = PathBuf::from(path);
        assert!(
            path.is_file(),
            "TB_SERVER_TEST_BINARY is not a file: {}",
            path.display()
        );
        return path
            .canonicalize()
            .expect("canonicalize TB_SERVER_TEST_BINARY");
    }

    let executable_name = format!("tb-server{}", std::env::consts::EXE_SUFFIX);
    let mut candidates = Vec::new();
    if let Some(directory) = std::env::current_exe()
        .expect("find process test executable")
        .parent()
    {
        candidates.push(directory.join(&executable_name));
        if let Some(parent) = directory.parent() {
            candidates.push(parent.join(&executable_name));
        }
    }
    candidates.push(PathBuf::from("/root/fc-harness-payloads/tb-server"));
    candidates.push(PathBuf::from(env!("CARGO_BIN_EXE_tb-server")));

    for candidate in candidates {
        if candidate.is_file() {
            return candidate
                .canonicalize()
                .expect("canonicalize co-packaged server binary");
        }
    }

    panic!(
        "could not find the tb-server executable; stage it as /root/fc-harness-payloads/tb-server or set TB_SERVER_TEST_BINARY"
    );
}

fn wait_until_ready(server: &mut RunningServer) {
    let deadline = Instant::now() + STARTUP_TIMEOUT;
    loop {
        if server.is_ready() {
            return;
        }
        if let Some(status) = server.try_wait() {
            panic!("server exited before readiness with status {status}");
        }
        assert!(
            Instant::now() < deadline,
            "server did not become ready within {STARTUP_TIMEOUT:?}"
        );
        thread::sleep(Duration::from_millis(20));
    }
}

fn exchange(port: u16, request: &[u8]) -> io::Result<String> {
    let address = SocketAddrV4::new(Ipv4Addr::LOCALHOST, port);
    let mut stream = TcpStream::connect_timeout(&address.into(), Duration::from_millis(250))?;
    stream.set_read_timeout(Some(REQUEST_TIMEOUT))?;
    stream.write_all(request)?;

    let mut response = Vec::new();
    stream.read_to_end(&mut response)?;
    Ok(String::from_utf8_lossy(&response).into_owned())
}
