use super::*;

struct BoundarySource {
    store: Arc<FileExportStore>,
    job: String,
    trigger: AtomicBool,
    stop: Option<Arc<AtomicBool>>,
}
impl ExportSource for BoundarySource {
    fn snapshot(&self, _: &str) -> Result<ExportSnapshot, ExportSourceError> {
        Ok(ExportSnapshot {
            boundary: 2,
            captured_at_millis: 123,
            history_start: 0,
            database_id: "database-id".into(),
            schemas: Vec::new(),
            indexes: Vec::new(),
            branches: Vec::new(),
            estimated_records: Some(2),
        })
    }
    fn page(
        &self,
        _: &str,
        _: &ExportSnapshot,
        phase: ExportPhase,
        after: Option<&[u8]>,
        _: usize,
    ) -> Result<ExportPage, ExportSourceError> {
        if phase != ExportPhase::Documents {
            return Ok(ExportPage { records: Vec::new(), next_cursor: None, done: true });
        }
        if self.trigger.swap(false, Ordering::AcqRel) {
            if let Some(stop) = &self.stop {
                stop.store(true, Ordering::Release);
            } else {
                assert!(self.store.request_cancel(&self.job).unwrap());
            }
        }
        let first = after.is_none();
        Ok(ExportPage {
            records: vec![ExportRecord::Document {
                collection: "docs".into(),
                id: if first { "first" } else { "second" }.into(),
                version: 1,
                canonical_json: br#"{"n":1}"#.to_vec(),
            }],
            next_cursor: first.then(|| vec![1]),
            done: !first,
        })
    }
}

struct Fixture {
    root: PathBuf,
    store: Arc<FileExportStore>,
    service: ExportService,
    request: ExportRequest,
    job: String,
}
fn fixture(stop: bool) -> Fixture {
    let root = std::env::temp_dir()
        .join(format!("export-cancel-{}", terrabase::identity::DatabaseUlid::generate()));
    let store = Arc::new(FileExportStore::open(root.join("jobs")).unwrap());
    let destination =
        ExportDestination::File { root: root.join("artifacts"), region: RegionCode::GRU };
    let request = ExportRequest {
        database: "db".into(),
        destination,
        format: crate::export_formats::TransferFormat::Jsonl,
        scope: crate::export_formats::ExportScope::All,
        placement: PlacementLock::single(RegionCode::GRU),
        idempotency_key: Some("original-operation".into()),
    };
    let key = [9; 32];
    let job = job_id(&request, &key);
    let stop_requested = Arc::new(AtomicBool::new(false));
    let source = Arc::new(BoundarySource {
        store: Arc::clone(&store),
        job: job.clone(),
        trigger: AtomicBool::new(true),
        stop: stop.then(|| Arc::clone(&stop_requested)),
    });
    let snapshot = source.snapshot("db").unwrap();
    let artifacts =
        crate::export_local::local_artifacts(&root.join("artifacts"), "database-id", "tenant")
            .unwrap();
    let mut service =
        ExportService::new(Some(source), Some(store.clone()), Some(artifacts), key).unwrap();
    service.stop_requested = stop_requested;
    store
        .create(&ExportJobState {
            job_id: job.clone(),
            database: "db".into(),
            destination: request.destination.clone(),
            placement: vec![RegionCode::GRU],
            snapshot,
            phase: ExportPhase::Documents,
            source_cursor: None,
            records: 0,
            pages: 0,
            status: JobStatus::Accepted,
            resume_cursor: service.cursor(&job, ExportPhase::Documents, None),
            manifest: None,
            error: None,
        })
        .unwrap();
    Fixture { root, store, service, request, job }
}

#[tokio::test]
async fn cancellation_keeps_committed_page_cursor_and_only_explicit_resume_restarts() {
    let f = fixture(false);
    f.service.clone_for_worker().run(&f.job).await.unwrap();
    let cancelled = f.service.status(&f.job).unwrap().unwrap();
    assert_eq!(cancelled.status, JobStatus::Cancelled);
    assert_eq!(cancelled.cancellation, ExportCancellation::Complete);
    assert_eq!((cancelled.records, cancelled.pages), (1, 1));
    assert_eq!(cancelled.phase, ExportPhase::Documents);
    assert_eq!(cancelled.created_at_millis, 123);
    let directory = f.root.join("artifacts/db").join(&f.job);
    let first = fs::read(directory.join("documents/000000000000.jsonl")).unwrap();
    assert!(!directory.join("COMMITTED").exists());
    let repeated = f.service.submit(f.request.clone()).await.unwrap();
    assert_eq!(repeated.job_id, f.job);
    assert_eq!(f.service.status(&f.job).unwrap().unwrap(), cancelled);
    assert_eq!(f.service.list_async(None, 1).await.unwrap(), vec![cancelled.clone()]);
    assert!(f.service.list_async(Some(&f.job), 1).await.unwrap().is_empty());
    assert!(f.service.list_async(None, 101).await.is_err());
    assert!(f.service.resume(&f.job, "invalid").await.is_err());
    assert!(f.store.cancel_requested(&f.job).unwrap());
    f.service.resume(&f.job, &cancelled.resume_cursor).await.unwrap();
    let completed = tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let status = f.service.status(&f.job).unwrap().unwrap();
            assert_ne!(status.status, JobStatus::Failed, "{:?}", status.error);
            if status.status == JobStatus::Complete {
                break status;
            }
            tokio::time::sleep(std::time::Duration::from_millis(1)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!((completed.records, completed.pages), (2, 2));
    assert_eq!(completed.cancellation, ExportCancellation::Unavailable);
    assert!(!f.store.cancel_requested(&f.job).unwrap());
    assert_eq!(fs::read(directory.join("documents/000000000000.jsonl")).unwrap(), first);
    assert!(directory.join("COMMITTED").exists());
    assert_eq!(f.service.cancel(&f.job).await.unwrap(), completed);
    fs::remove_dir_all(f.root).unwrap();
}

#[tokio::test]
async fn shutdown_yields_after_checkpoint_and_restart_finishes_same_operation() {
    let f = fixture(true);
    f.service.clone_for_worker().run(&f.job).await.unwrap();
    let interrupted = f.store.load(&f.job).unwrap().unwrap();
    assert_eq!(interrupted.status, JobStatus::Running);
    assert_eq!((interrupted.records, interrupted.pages), (1, 1));
    assert!(!f.store.cancel_requested(&f.job).unwrap());
    assert!(!f.root.join("artifacts/db").join(&f.job).join("COMMITTED").exists());
    f.service.stop_requested.store(false, Ordering::Release);
    f.service.clone_for_worker().run(&f.job).await.unwrap();
    assert_eq!(f.service.status(&f.job).unwrap().unwrap().status, JobStatus::Complete);
    assert_eq!(f.service.status(&f.job).unwrap().unwrap().records, 2);
    fs::remove_dir_all(f.root).unwrap();
}

#[test]
fn cancellation_flag_survives_stale_progress_writes_and_reconstruction() {
    let f = fixture(false);
    let original = f.store.load(&f.job).unwrap().unwrap();
    assert!(f.store.request_cancel(&f.job).unwrap());
    f.store.save(&original).unwrap();
    let reopened = FileExportStore::open(f.root.join("jobs")).unwrap();
    assert!(reopened.cancel_requested(&f.job).unwrap());
    assert_eq!(reopened.list(None, 1).unwrap(), vec![f.job.clone()]);
    reopened.clear_cancel(&f.job).unwrap();
    assert!(!f.store.cancel_requested(&f.job).unwrap());
    fs::remove_dir_all(f.root).unwrap();
}

struct BeforeCommitPort {
    inner: Arc<dyn ArtifactPort>,
    store: Arc<FileExportStore>,
}
struct BeforeCommitWriter {
    inner: Box<dyn ArtifactWriter>,
    store: Arc<FileExportStore>,
    job: String,
}
impl ArtifactPort for BeforeCommitPort {
    fn attestation(&self) -> Option<EncryptionAttestation> {
        self.inner.attestation()
    }
    fn open<'a>(
        &'a self,
        job: &'a str,
        database: &'a str,
        destination: &'a ExportDestination,
        placement: &'a PlacementLock,
    ) -> ExportFuture<'a, Box<dyn ArtifactWriter>> {
        Box::pin(async move {
            Ok(Box::new(BeforeCommitWriter {
                inner: self.inner.open(job, database, destination, placement).await?,
                store: Arc::clone(&self.store),
                job: job.into(),
            }) as Box<dyn ArtifactWriter>)
        })
    }
}
impl ArtifactWriter for BeforeCommitWriter {
    fn put<'a>(&'a mut self, path: &'a str, plaintext: &'a [u8]) -> ExportFuture<'a, ArtifactFact> {
        Box::pin(async move {
            let fact = self.inner.put(path, plaintext).await?;
            if path == "manifest.json" {
                self.store.request_cancel(&self.job)?;
            }
            Ok(fact)
        })
    }
    fn existing_facts<'a>(&'a mut self) -> ExportFuture<'a, Vec<ArtifactFact>> {
        Box::pin(async move { self.inner.existing_facts().await })
    }
    fn commit(self: Box<Self>, _: &ArtifactFact) -> ExportFuture<'static, ()> {
        Box::pin(async { panic!("cancelled operation must not commit") })
    }
}

#[tokio::test]
async fn cancellation_after_manifest_staging_prevents_irreversible_commit() {
    let mut f = fixture(false);
    f.service.source = Arc::new(BoundarySource {
        store: Arc::clone(&f.store),
        job: f.job.clone(),
        trigger: AtomicBool::new(false),
        stop: None,
    });
    f.service.artifacts = Arc::new(BeforeCommitPort {
        inner: f.service.artifacts.clone(),
        store: Arc::clone(&f.store),
    });
    f.service.clone_for_worker().run(&f.job).await.unwrap();
    let status = f.service.status(&f.job).unwrap().unwrap();
    assert_eq!(status.status, JobStatus::Cancelled);
    assert_eq!((status.records, status.pages), (2, 2));
    let directory = f.root.join("artifacts/db").join(&f.job);
    assert!(directory.join("manifest.json").exists());
    assert!(!directory.join("COMMITTED").exists());
    fs::remove_dir_all(f.root).unwrap();
}

#[tokio::test]
async fn resume_refuses_a_worker_observing_cancel_without_clearing_its_flag() {
    let f = fixture(false);
    f.service.clone_for_worker().run(&f.job).await.unwrap();
    let state = f.store.load(&f.job).unwrap().unwrap();
    let worker_lease = f.store.acquire_job(&f.job).unwrap().unwrap();
    assert!(matches!(
        f.service.resume(&f.job, &state.resume_cursor).await,
        Err(ExportError::IdempotencyConflict)
    ));
    assert!(f.store.cancel_requested(&f.job).unwrap());
    assert_eq!(f.store.load(&f.job).unwrap().unwrap(), state);
    drop(worker_lease);
    let local_worker =
        ActiveJob::acquire(Arc::clone(&f.service.active_jobs), &f.job).unwrap().unwrap();
    assert!(matches!(
        f.service.resume(&f.job, &state.resume_cursor).await,
        Err(ExportError::IdempotencyConflict)
    ));
    assert!(f.store.cancel_requested(&f.job).unwrap());
    drop(local_worker);
    fs::remove_dir_all(f.root).unwrap();
}

#[tokio::test]
async fn service_cancel_stops_an_accepted_job_before_any_document_effect() {
    let f = fixture(false);
    let requested = f.service.cancel(&f.job).await.unwrap();
    assert!(matches!(
        requested.cancellation,
        ExportCancellation::Requested | ExportCancellation::Complete
    ));
    let cancelled = tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let status = f.service.status(&f.job).unwrap().unwrap();
            if status.status == JobStatus::Cancelled {
                break status;
            }
            tokio::time::sleep(std::time::Duration::from_millis(1)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!((cancelled.records, cancelled.pages), (0, 0));
    assert_eq!(cancelled.cancellation, ExportCancellation::Complete);
    assert!(!f.root.join("artifacts/db").join(&f.job).exists());
    fs::remove_dir_all(f.root).unwrap();
}
