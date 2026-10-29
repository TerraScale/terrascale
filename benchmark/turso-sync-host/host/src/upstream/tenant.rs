use std::collections::BTreeMap;
use std::fs::{self, File};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard};

use anyhow::{anyhow, bail, Result};
use bytes::Bytes;
use prost::Message;
use roaring::RoaringBitmap;
use serde::Serialize;
use tracing::warn;
use turso_core::{CheckpointMode, Connection, DatabaseOpts, OpenFlags};
use turso_sync_engine::server_proto::{
    PageData, PageSetRawEncodingProto, PageUpdatesEncodingReq, PullUpdatesApplyMode,
    PullUpdatesProtocol, PullUpdatesReqProtoBody, PullUpdatesRespProtoBody, PullUpdatesStreamKind,
};

use super::revision::{self, Live, Manifest, Plan, Revision, CHECKPOINT_FRAMES};
use super::{
    current_snapshot_db_size_pages, encode_length_delimited, open_db_handle, DbHandle,
    OpenConfig, TursoSyncServer, PAGE_SIZE, WAL_FRAME_HEADER_SIZE,
};

pub struct Reply {
    pub status: u16,
    pub content_type: &'static str,
    pub body: Vec<u8>,
    pub pull: Option<String>,
}

impl Reply {
    pub fn text(status: u16, body: impl Into<String>) -> Self {
        Self {
            status,
            content_type: "text/plain",
            body: body.into().into_bytes(),
            pull: None,
        }
    }

    pub fn json(value: &impl Serialize) -> Result<Self> {
        Ok(Self {
            status: 200,
            content_type: "application/json",
            body: serde_json::to_vec(value)?,
            pull: None,
        })
    }
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct Stats {
    pub pulls_incremental: u64,
    pub pulls_empty: u64,
    pub pulls_full: u64,
    pub full_reasons: BTreeMap<String, u64>,
    pub pulls_pinned: u64,
    pub pulls_expired: u64,
    pub pages_sent: u64,
    pub checkpoints: u64,
    pub detected_checkpoints: u64,
    pub rewrites: u64,
    pub restores: u64,
    pub max_wal_bytes: u64,
    pub max_manifest_bytes: u64,
    pub max_sealed_generations: usize,
    pub max_horizon_buckets: usize,
}

#[derive(Serialize)]
pub struct Snapshot {
    pub incarnation: String,
    pub generation: u64,
    pub sealed_generations: usize,
    pub horizon_buckets: usize,
    pub horizon_first_generation: Option<u64>,
    pub wal_frames: u64,
    pub wal_bytes: u64,
    pub manifest_bytes: u64,
    pub db_pages: u64,
    pub stats: Stats,
}

struct State {
    handle: Option<Arc<DbHandle>>,
    manifest: Manifest,
    live: Live,
    stats: Stats,
}

pub struct Tenant {
    dir: PathBuf,
    state: Mutex<State>,
}

fn open_config() -> OpenConfig {
    OpenConfig {
        vfs: None,
        flags: OpenFlags::default(),
        db_opts: DatabaseOpts::default(),
        max_open: 1,
    }
}

fn index_frames(conn: &Connection, live: &mut Live, upto: u64) -> Result<()> {
    let mut frame = vec![0u8; WAL_FRAME_HEADER_SIZE + PAGE_SIZE];
    for frame_no in live.max_frame + 1..=upto {
        let info = conn.wal_get_frame(frame_no, &mut frame)?;
        live.pages.insert(info.page_no, frame_no);
    }
    live.max_frame = live.max_frame.max(upto);
    Ok(())
}

fn read_page(conn: &Connection, page_no: u32, watermark: u64) -> Result<Vec<u8>> {
    let mut page = vec![0u8; PAGE_SIZE];
    if !conn.try_wal_watermark_read_page(page_no, &mut page, Some(watermark))? {
        bail!("page {page_no} is missing at frame {watermark}");
    }
    Ok(page)
}

fn encode_pull(revision: String, db_size: u64, pages: Vec<(u32, Vec<u8>)>) -> Vec<u8> {
    let header = PullUpdatesRespProtoBody {
        server_revision: revision,
        db_size,
        raw_encoding: Some(PageSetRawEncodingProto {}),
        zstd_encoding: None,
        stream_kind: PullUpdatesStreamKind::Pages as i32,
        apply_mode: PullUpdatesApplyMode::Incremental as i32,
        mvcc_log: None,
        protocol: PullUpdatesProtocol::Pages as i32,
    };
    let mut body = Vec::new();
    encode_length_delimited(&mut body, &header.encode_to_vec());
    for (page_no, page) in pages {
        let message = PageData {
            page_id: u64::from(page_no - 1),
            encoded_page: Bytes::from(page),
        };
        encode_length_delimited(&mut body, &message.encode_to_vec());
    }
    body
}

fn file_len(path: &Path) -> u64 {
    fs::metadata(path).map(|m| m.len()).unwrap_or(0)
}

fn copy_durably(from: &Path, to: &Path) -> Result<()> {
    fs::copy(from, to)?;
    File::open(to)?.sync_all()?;
    Ok(())
}

impl Tenant {
    pub fn open(base: &Path, name: &str) -> Result<Self> {
        let dir = base.join(name);
        fs::create_dir_all(dir.join("backups"))?;
        let manifest = match Manifest::load(&dir) {
            Ok(Some(manifest)) => manifest,
            Ok(None) => Manifest::new_incarnation(),
            Err(e) => {
                warn!("{name}: unreadable sync manifest, starting a new incarnation: {e:#}");
                Manifest::new_incarnation()
            }
        };
        let tenant = Self {
            dir,
            state: Mutex::new(State {
                handle: None,
                manifest,
                live: Live::default(),
                stats: Stats::default(),
            }),
        };
        {
            let mut st = tenant.lock();
            tenant.attach(&mut st)?;
        }
        Ok(tenant)
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn data(&self) -> PathBuf {
        self.dir.join("data")
    }

    fn wal(&self) -> PathBuf {
        self.dir.join("data-wal")
    }

    fn backup_path(&self, id: &str) -> Result<PathBuf> {
        if id.is_empty() || id.len() > 64 || !id.bytes().all(|b| b.is_ascii_alphanumeric()) {
            bail!("invalid backup id");
        }
        let path = self.dir.join("backups").join(format!("{id}.db"));
        if path.to_string_lossy().contains('\'') {
            bail!("data directory paths must not contain quotes");
        }
        Ok(path)
    }

    fn store(&self, st: &mut State) -> Result<()> {
        let bytes = st.manifest.store(&self.dir)?;
        st.stats.max_manifest_bytes = st.stats.max_manifest_bytes.max(bytes);
        st.stats.max_sealed_generations =
            st.stats.max_sealed_generations.max(st.manifest.sealed.len());
        st.stats.max_horizon_buckets = st.stats.max_horizon_buckets.max(st.manifest.horizon.len());
        Ok(())
    }

    fn handle(st: &State) -> Result<Arc<DbHandle>> {
        st.handle
            .clone()
            .ok_or_else(|| anyhow!("database is closed"))
    }

    fn attach(&self, st: &mut State) -> Result<()> {
        let handle = open_db_handle(&self.data(), &open_config())?;
        let salt = revision::read_wal_salt(&self.wal())?;
        {
            let conn = handle.conn.lock().unwrap();
            let state = conn.wal_state()?;
            st.live = Live {
                checkpoint_seq: state.checkpoint_seq_no,
                ..Live::default()
            };
            if st.manifest.wal_salt.is_some() && st.manifest.wal_salt != salt {
                warn!(
                    "{}: WAL changed outside the coordinator; next pulls resend every page",
                    self.dir.display()
                );
                st.live.full = true;
                st.stats.detected_checkpoints += 1;
            }
            index_frames(&conn, &mut st.live, state.max_frame)?;
        }
        st.handle = Some(handle);
        self.checkpoint_locked(st)?;
        self.store(st)
    }

    fn detach(&self, st: &mut State) -> Result<()> {
        if let Some(handle) = st.handle.take() {
            let conn = handle.conn.lock().unwrap();
            conn.close()?;
        }
        Ok(())
    }

    fn refresh(&self, st: &mut State, conn: &Connection) -> Result<()> {
        let state = conn.wal_state()?;
        let mut dirty = false;
        if state.checkpoint_seq_no != st.live.checkpoint_seq || state.max_frame < st.live.max_frame
        {
            warn!(
                "{}: WAL restarted outside the coordinator; sealing generation {} as full",
                self.dir.display(),
                st.manifest.generation
            );
            st.stats.detected_checkpoints += 1;
            st.live.full = true;
            st.manifest.seal(&st.live);
            st.live = Live {
                checkpoint_seq: state.checkpoint_seq_no,
                ..Live::default()
            };
            dirty = true;
        }
        index_frames(conn, &mut st.live, state.max_frame)?;
        if st.live.max_frame > 0 && st.manifest.wal_salt.is_none() {
            st.manifest.wal_salt = revision::read_wal_salt(&self.wal())?;
            dirty = true;
        }
        if dirty {
            self.store(st)?;
        }
        st.stats.max_wal_bytes = st.stats.max_wal_bytes.max(file_len(&self.wal()));
        Ok(())
    }

    fn checkpoint_locked(&self, st: &mut State) -> Result<()> {
        let handle = Self::handle(st)?;
        let conn = handle.conn.lock().unwrap();
        if !conn.get_auto_commit() {
            bail!("a transaction is open");
        }
        self.refresh(st, &conn)?;
        if st.live.max_frame == 0 && !st.live.full {
            return Ok(());
        }
        let end = st.live.max_frame;
        st.manifest.seal(&st.live);
        self.store(st)?;
        if end > 0 {
            conn.checkpoint(CheckpointMode::Truncate {
                upper_bound_inclusive: Some(end),
            })?;
        }
        let after = conn.wal_state()?;
        if after.max_frame != 0 {
            bail!("checkpoint left {} WAL frames", after.max_frame);
        }
        st.live = Live {
            checkpoint_seq: after.checkpoint_seq_no,
            ..Live::default()
        };
        st.stats.checkpoints += 1;
        Ok(())
    }

    pub fn pipeline(&self, hrana: &TursoSyncServer, body: &[u8]) -> Result<Reply> {
        let mut st = self.lock();
        let handle = Self::handle(&st)?;
        let result = hrana.handle_pipeline(&handle, body);
        let autocommit = {
            let conn = handle.conn.lock().unwrap();
            let autocommit = conn.get_auto_commit();
            if autocommit {
                self.refresh(&mut st, &conn)?;
            }
            autocommit
        };
        if autocommit && st.live.max_frame >= CHECKPOINT_FRAMES {
            self.checkpoint_locked(&mut st)?;
        }
        let response = result?;
        Ok(Reply {
            status: response.status,
            content_type: "application/json",
            body: response.body,
            pull: None,
        })
    }

    pub fn pull(&self, body: &[u8]) -> Result<Reply> {
        let req = <PullUpdatesReqProtoBody as Message>::decode(body)?;
        if !matches!(
            PageUpdatesEncodingReq::try_from(req.encoding),
            Ok(PageUpdatesEncodingReq::Raw)
        ) {
            return Ok(Reply::text(400, "only raw page encoding is served"));
        }
        if matches!(
            PullUpdatesStreamKind::try_from(req.stream_kind),
            Ok(PullUpdatesStreamKind::MvccLogicalLog)
        ) {
            return Ok(Reply::text(400, "logical pulls are not served"));
        }
        if !req.server_query_selector.is_empty() {
            return Ok(Reply::text(400, "query selectors are not served"));
        }
        let selector = if req.server_pages_selector.is_empty() {
            None
        } else {
            Some(RoaringBitmap::deserialize_from(&req.server_pages_selector[..])?)
        };
        let selected = |page_no: &u32| selector.as_ref().is_none_or(|s| s.contains(*page_no - 1));

        let mut st = self.lock();
        let handle = Self::handle(&st)?;
        let conn = handle.conn.lock().unwrap();
        if !conn.get_auto_commit() {
            return Ok(Reply::text(503, "a transaction is open, retry"));
        }
        self.refresh(&mut st, &conn)?;

        if !req.server_revision.is_empty() {
            let pinned = Revision::parse(&req.server_revision).filter(|r| {
                r.incarnation == st.manifest.incarnation
                    && r.generation == st.manifest.generation
                    && r.frame <= st.live.max_frame
            });
            let Some(pinned) = pinned else {
                st.stats.pulls_expired += 1;
                let mut reply = Reply::text(
                    409,
                    "revision_expired: the pinned revision is no longer served; bootstrap again",
                );
                reply.pull = Some("expired".to_string());
                return Ok(reply);
            };
            let db_size = current_snapshot_db_size_pages(&conn, pinned.frame)?;
            let mut pages = Vec::new();
            for page_no in (1..=db_size as u32).filter(selected) {
                pages.push((page_no, read_page(&conn, page_no, pinned.frame)?));
            }
            st.stats.pulls_pinned += 1;
            st.stats.pages_sent += pages.len() as u64;
            return Ok(Reply {
                status: 200,
                content_type: "application/protobuf",
                pull: Some(format!("pinned;pages={};db_size={db_size}", pages.len())),
                body: encode_pull(pinned.encode(), db_size, pages),
            });
        }

        let watermark = st.live.max_frame;
        let db_size = current_snapshot_db_size_pages(&conn, watermark)?;
        let plan = revision::plan(&st.manifest, &st.live, &req.client_revision);
        let candidates: Vec<u32> = match &plan {
            Plan::Full(_) => (1..=db_size as u32).collect(),
            Plan::Pages(set) => set.iter().copied().filter(|p| u64::from(*p) <= db_size).collect(),
        };
        let mut pages = Vec::new();
        for page_no in candidates.into_iter().filter(selected) {
            pages.push((page_no, read_page(&conn, page_no, watermark)?));
        }
        let kind = match &plan {
            Plan::Full(reason) => {
                st.stats.pulls_full += 1;
                *st.stats
                    .full_reasons
                    .entry(reason.as_str().to_string())
                    .or_default() += 1;
                format!("full;{}", reason.as_str())
            }
            Plan::Pages(_) if pages.is_empty() => {
                st.stats.pulls_empty += 1;
                "empty".to_string()
            }
            Plan::Pages(_) => {
                st.stats.pulls_incremental += 1;
                "incremental".to_string()
            }
        };
        st.stats.pages_sent += pages.len() as u64;
        let revision = st.manifest.current(watermark).encode();
        Ok(Reply {
            status: 200,
            content_type: "application/protobuf",
            pull: Some(format!("{kind};pages={};db_size={db_size}", pages.len())),
            body: encode_pull(revision, db_size, pages),
        })
    }

    pub fn checkpoint(&self) -> Result<()> {
        let mut st = self.lock();
        self.checkpoint_locked(&mut st)
    }

    pub fn backup(&self, id: &str) -> Result<u64> {
        let path = self.backup_path(id)?;
        if path.exists() {
            bail!("backup {id} already exists");
        }
        let st = self.lock();
        let handle = Self::handle(&st)?;
        let conn = handle.conn.lock().unwrap();
        conn.execute(format!("VACUUM INTO '{}'", path.display()))?;
        File::open(&path)?.sync_all()?;
        revision::sync_dir(&self.dir.join("backups"))?;
        Ok(file_len(&path))
    }

    pub fn rewrite(&self) -> Result<()> {
        let mut st = self.lock();
        self.checkpoint_locked(&mut st)?;
        let staged = self.dir.join("data.rewrite");
        let _ = fs::remove_file(&staged);
        {
            let handle = Self::handle(&st)?;
            let conn = handle.conn.lock().unwrap();
            conn.execute(format!("VACUUM INTO '{}'", staged.display()))?;
        }
        File::open(&staged)?.sync_all()?;
        st.manifest.seal_full();
        self.store(&mut st)?;
        self.swap(&mut st, &staged)?;
        st.stats.rewrites += 1;
        Ok(())
    }

    pub fn restore(&self, id: &str) -> Result<()> {
        let source = self.backup_path(id)?;
        if !source.exists() {
            bail!("backup {id} does not exist");
        }
        let mut st = self.lock();
        let staged = self.dir.join("data.restore");
        copy_durably(&source, &staged)?;
        self.checkpoint_locked(&mut st)?;
        st.manifest = Manifest::new_incarnation();
        self.store(&mut st)?;
        self.swap(&mut st, &staged)?;
        st.stats.restores += 1;
        Ok(())
    }

    fn swap(&self, st: &mut State, staged: &Path) -> Result<()> {
        self.detach(st)?;
        match fs::remove_file(self.wal()) {
            Ok(()) => {}
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.into()),
        }
        fs::rename(staged, self.data())?;
        revision::sync_dir(&self.dir)?;
        self.attach(st)
    }

    pub fn close(&self) -> Result<()> {
        let mut st = self.lock();
        if st.handle.is_some() {
            self.checkpoint_locked(&mut st)?;
            self.detach(&mut st)?;
        }
        Ok(())
    }

    pub fn snapshot(&self) -> Result<Snapshot> {
        let mut st = self.lock();
        let handle = Self::handle(&st)?;
        let conn = handle.conn.lock().unwrap();
        self.refresh(&mut st, &conn)?;
        let db_pages = current_snapshot_db_size_pages(&conn, st.live.max_frame)?;
        Ok(Snapshot {
            incarnation: st.manifest.incarnation.clone(),
            generation: st.manifest.generation,
            sealed_generations: st.manifest.sealed.len(),
            horizon_buckets: st.manifest.horizon.len(),
            horizon_first_generation: st.manifest.horizon.first().map(|b| b.first_generation),
            wal_frames: st.live.max_frame,
            wal_bytes: file_len(&self.wal()),
            manifest_bytes: file_len(&Manifest::path(&self.dir)),
            db_pages,
            stats: st.stats.clone(),
        })
    }
}
