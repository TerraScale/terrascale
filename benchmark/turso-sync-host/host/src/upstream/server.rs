use std::collections::{BTreeMap, HashMap};
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use anyhow::{anyhow, bail, Context, Result};
use serde::Deserialize;
use signal_hook::consts::{SIGINT, SIGTERM};
use signal_hook::iterator::Signals;
use tracing::error;
use tracing_subscriber::EnvFilter;
use turso_core::{DatabaseOpts, OpenFlags};

use super::revision::sync_dir;
use super::tenant::{Reply, Tenant};
use super::{
    find_header_end, parse_content_length, parse_http_request, request_end, validate_db_name,
    OpenConfig, TursoSyncServer, MAX_HEADER_BYTES,
};

const MAX_BODY_BYTES: usize = 64 * 1024 * 1024;

#[derive(Deserialize)]
struct Tokens {
    admin: String,
    databases: BTreeMap<String, String>,
}

struct Request {
    method: String,
    path: String,
    authorization: Option<String>,
    body: Vec<u8>,
}

struct Server {
    base: PathBuf,
    tokens: Tokens,
    tenants: Mutex<HashMap<String, Arc<Tenant>>>,
    hrana: TursoSyncServer,
    closing: AtomicBool,
}

fn token_matches(header: Option<&str>, expected: &str) -> bool {
    let Some(given) = header.and_then(|h| h.strip_prefix("Bearer ")) else {
        return false;
    };
    let (a, b) = (given.as_bytes(), expected.as_bytes());
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

fn read_request(stream: &mut TcpStream) -> Result<Request> {
    stream.set_read_timeout(Some(Duration::from_secs(30)))?;
    let mut buffer = [0u8; 8192];
    let mut data = Vec::new();
    let header_end = loop {
        let n = stream.read(&mut buffer)?;
        if n == 0 {
            bail!("connection closed before the request headers ended");
        }
        let unscanned = data.len().saturating_sub(3);
        data.extend_from_slice(&buffer[..n]);
        if let Some(end) = find_header_end(&data, unscanned) {
            break end;
        }
        if data.len() > MAX_HEADER_BYTES {
            bail!("request headers exceed {MAX_HEADER_BYTES} bytes");
        }
    };
    let headers = String::from_utf8_lossy(&data[..header_end]).to_string();
    let length = parse_content_length(&headers).unwrap_or(0);
    if length > MAX_BODY_BYTES {
        bail!("request body exceeds {MAX_BODY_BYTES} bytes");
    }
    let total = request_end(header_end, length)?;
    while data.len() < total {
        let n = stream.read(&mut buffer)?;
        if n == 0 {
            bail!("connection closed before the request body ended");
        }
        data.extend_from_slice(&buffer[..n]);
    }
    data.truncate(total);
    let (method, path, body) = parse_http_request(&data)?;
    let authorization = headers
        .lines()
        .filter_map(|line| line.split_once(':'))
        .find(|(name, _)| name.trim().eq_ignore_ascii_case("authorization"))
        .map(|(_, value)| value.trim().to_string());
    Ok(Request {
        method,
        path,
        authorization,
        body,
    })
}

fn write_reply(stream: &mut TcpStream, reply: &Reply) -> Result<()> {
    let reason = match reply.status {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        409 => "Conflict",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Unknown",
    };
    let mut head = format!(
        "HTTP/1.1 {} {reason}\r\nContent-Type: {}\r\nContent-Length: {}\r\nConnection: close\r\n",
        reply.status,
        reply.content_type,
        reply.body.len()
    );
    if let Some(pull) = &reply.pull {
        head.push_str(&format!("x-ts-sync-pull: {pull}\r\n"));
    }
    head.push_str("\r\n");
    stream.write_all(head.as_bytes())?;
    stream.write_all(&reply.body)?;
    stream.flush()?;
    Ok(())
}

impl Server {
    fn tenant(&self, name: &str) -> Result<Arc<Tenant>> {
        let mut tenants = self.tenants.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(tenant) = tenants.get(name) {
            return Ok(tenant.clone());
        }
        let tenant = Arc::new(Tenant::open(&self.base, name)?);
        tenants.insert(name.to_string(), tenant.clone());
        Ok(tenant)
    }

    fn route(&self, req: Request) -> Result<Reply> {
        if req.method == "OPTIONS" {
            return Ok(Reply::text(204, ""));
        }
        if self.closing.load(Ordering::SeqCst) {
            return Ok(Reply::text(503, "shutting down"));
        }
        if req.method != "POST" {
            return Ok(Reply::text(404, "Not Found"));
        }
        let segments: Vec<&str> = req.path.trim_start_matches('/').split('/').collect();
        match segments.as_slice() {
            ["db", name, rest @ ..] => {
                if !validate_db_name(name) {
                    return Ok(Reply::text(400, "invalid database name"));
                }
                let Some(expected) = self.tokens.databases.get(*name) else {
                    return Ok(Reply::text(404, "Not Found"));
                };
                if !token_matches(req.authorization.as_deref(), expected) {
                    return Ok(Reply::text(401, "Unauthorized"));
                }
                let tenant = self.tenant(name)?;
                match rest {
                    ["v2", "pipeline"] => tenant.pipeline(&self.hrana, &req.body),
                    ["pull-updates"] => tenant.pull(&req.body),
                    _ => Ok(Reply::text(404, "Not Found")),
                }
            }
            ["admin", "db", name, op @ ..] => {
                if !token_matches(req.authorization.as_deref(), &self.tokens.admin) {
                    return Ok(Reply::text(401, "Unauthorized"));
                }
                if !self.tokens.databases.contains_key(*name) {
                    return Ok(Reply::text(404, "Not Found"));
                }
                let tenant = self.tenant(name)?;
                match op {
                    ["state"] => Reply::json(&tenant.snapshot()?),
                    ["checkpoint"] => {
                        tenant.checkpoint()?;
                        Reply::json(&tenant.snapshot()?)
                    }
                    ["rewrite"] => {
                        tenant.rewrite()?;
                        Reply::json(&tenant.snapshot()?)
                    }
                    ["backup", id] => Reply::json(&serde_json::json!({ "bytes": tenant.backup(id)? })),
                    ["restore", id] => {
                        tenant.restore(id)?;
                        Reply::json(&tenant.snapshot()?)
                    }
                    _ => Ok(Reply::text(404, "Not Found")),
                }
            }
            _ => Ok(Reply::text(404, "Not Found")),
        }
    }

    fn serve(&self, mut stream: TcpStream) {
        let reply = match read_request(&mut stream) {
            Ok(req) => self.route(req).unwrap_or_else(|e| {
                error!("request failed: {e:#}");
                Reply::text(500, format!("internal error: {e:#}"))
            }),
            Err(e) => Reply::text(400, format!("bad request: {e:#}")),
        };
        if let Err(e) = write_reply(&mut stream, &reply) {
            error!("writing the response failed: {e:#}");
        }
    }

    fn shutdown(&self) -> Result<()> {
        self.closing.store(true, Ordering::SeqCst);
        let tenants = self.tenants.lock().unwrap_or_else(|e| e.into_inner());
        for tenant in tenants.values() {
            tenant.close()?;
        }
        Ok(())
    }
}

fn create_dir_durably(path: &Path) -> Result<()> {
    if path.is_dir() {
        return Ok(());
    }
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    create_dir_durably(parent)?;
    std::fs::create_dir(path)?;
    sync_dir(parent)
}

fn arg(args: &[String], name: &str) -> Result<String> {
    args.iter()
        .position(|a| a == name)
        .and_then(|i| args.get(i + 1))
        .cloned()
        .ok_or_else(|| anyhow!("missing {name}"))
}

pub fn run() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::new("warn"))
        .with_writer(std::io::stderr)
        .init();
    let args: Vec<String> = std::env::args().collect();
    let listen = arg(&args, "--listen")?;
    let base = PathBuf::from(arg(&args, "--data")?);
    let tokens: Tokens = serde_json::from_slice(
        &std::fs::read(arg(&args, "--tokens")?).context("reading the token file")?,
    )?;
    create_dir_durably(&base)?;
    let base = base.canonicalize()?;
    if base.to_string_lossy().contains('\'') {
        bail!("the data directory path must not contain quotes");
    }
    if let Some(bad) = tokens.databases.keys().find(|n| !validate_db_name(n)) {
        bail!("invalid database name in the token file: {bad}");
    }
    let hrana = TursoSyncServer::new_dir(
        String::new(),
        base.clone(),
        Arc::new(AtomicUsize::new(0)),
        OpenConfig {
            vfs: None,
            flags: OpenFlags::default(),
            db_opts: DatabaseOpts::default(),
            max_open: 1,
        },
    )?;
    let server = Arc::new(Server {
        base,
        tokens,
        tenants: Mutex::new(HashMap::new()),
        hrana,
        closing: AtomicBool::new(false),
    });

    let mut signals = Signals::new([SIGTERM, SIGINT])?;
    let drain = server.clone();
    thread::spawn(move || {
        if signals.forever().next().is_some() {
            let code = match drain.shutdown() {
                Ok(()) => 0,
                Err(e) => {
                    error!("shutdown checkpoint failed: {e:#}");
                    1
                }
            };
            std::process::exit(code);
        }
    });

    let listener = TcpListener::bind(&listen)?;
    println!("listening {}", listener.local_addr()?);
    std::io::stdout().flush()?;
    for stream in listener.incoming() {
        match stream {
            Ok(stream) => {
                let server = server.clone();
                thread::spawn(move || server.serve(stream));
            }
            Err(e) => error!("accept failed: {e}"),
        }
    }
    Ok(())
}
