use std::future::Future;
use std::sync::{Arc, RwLock};

use bytes::Bytes;
use futures_util::StreamExt;
use object_store::path::Path;
use object_store::{
    Error as ProviderError, GetOptions, GetResult, MultipartUpload, ObjectStore, ObjectStoreExt,
    PutMode, PutOptions, PutPayload, UpdateVersion,
};
use sha2::{Digest, Sha256};
use tb_core::{
    AdmissionError, Attribution, ItemReservation, MemoryBudget, MemoryCategory, Reservation,
    process_memory_budget,
};
use tokio::io::{AsyncRead, AsyncReadExt};
use tokio::sync::{OwnedSemaphorePermit, Semaphore};
use tokio::time::timeout;

use crate::{
    ContentDigest, ObjectCapability, ObjectError, ObjectGuarantee, ObjectKey, ObjectLimits,
    ObjectReference, ProviderCapabilities, TransferOwner, VerifiedBlock, VerifiedObject,
};

const CREATE_MARKER: &[u8] = b"TB-IMMUTABLE-RESERVATION-v1";
const MAX_KEY_RESERVATION_ATTEMPTS: usize = 8;

/// Bounded adapter over a Rust `object_store` implementation.
///
/// Pass the process-wide core `MemoryBudget` in production. The generic
/// constructor also permits independent budgets for isolated tests.
#[derive(Clone)]
pub struct ObjectClient {
    store: Arc<dyn ObjectStore>,
    budget: MemoryBudget,
    limits: ObjectLimits,
    request_slots: Arc<Semaphore>,
    upload_slots: Arc<Semaphore>,
    capabilities: Arc<RwLock<ProviderCapabilities>>,
}

impl ObjectClient {
    /// Wrap a concrete `object_store` provider with explicit byte and request limits.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidLimits`] if the configured limits are invalid.
    pub fn new<S>(
        store: Arc<S>,
        budget: MemoryBudget,
        limits: ObjectLimits,
    ) -> Result<Self, ObjectError>
    where
        S: ObjectStore,
    {
        let store: Arc<dyn ObjectStore> = store;
        Self::new_dyn(store, budget, limits)
    }

    /// Wrap a process-global memory budget and a concrete provider.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::ProcessMemoryBudgetUninitialized`] before startup
    /// initialization, or [`ObjectError::InvalidLimits`] for invalid limits.
    pub fn with_process_budget<S>(store: Arc<S>, limits: ObjectLimits) -> Result<Self, ObjectError>
    where
        S: ObjectStore,
    {
        let budget = process_memory_budget()
            .cloned()
            .ok_or(ObjectError::ProcessMemoryBudgetUninitialized)?;
        Self::new(store, budget, limits)
    }

    /// Wrap an already-erased `object_store` provider.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidLimits`] if the configured limits are invalid.
    pub fn new_dyn(
        store: Arc<dyn ObjectStore>,
        budget: MemoryBudget,
        limits: ObjectLimits,
    ) -> Result<Self, ObjectError> {
        limits.validate()?;
        Ok(Self {
            store,
            budget,
            request_slots: Arc::new(Semaphore::new(limits.max_active_requests)),
            upload_slots: Arc::new(Semaphore::new(limits.max_upload_sessions)),
            capabilities: Arc::new(RwLock::new(ProviderCapabilities::default())),
            limits,
        })
    }

    /// Return the latest live-probed provider capability report.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::CapabilityReportPoisoned`] if the report lock is poisoned.
    pub fn capabilities(&self) -> Result<ProviderCapabilities, ObjectError> {
        self.capabilities
            .read()
            .map(|report| *report)
            .map_err(|_| ObjectError::CapabilityReportPoisoned)
    }

    /// Exercise range, multipart-abort, create-only, and stale-update behavior.
    ///
    /// Capabilities start false and become true only after the exact operation
    /// succeeds against this provider. Authentication, timeout, and unrelated
    /// provider failures are returned to the caller instead of being hidden as
    /// an unsupported capability.
    ///
    /// # Errors
    ///
    /// Returns an admission, provider, timeout, or capability-report error if a probe
    /// cannot complete or does not produce a conclusive supported/unsupported result.
    pub async fn probe_capabilities(
        &self,
        owner: TransferOwner,
    ) -> Result<ProviderCapabilities, ObjectError> {
        let report = ProviderCapabilities {
            conditional_create: self.probe_conditional_create(owner).await?,
            range_reads: self.probe_range_read(owner).await?,
            multipart_abort: self.probe_multipart_abort().await?,
            conditional_manifest_update: self.probe_conditional_update(owner).await?,
        };
        let mut current = self
            .capabilities
            .write()
            .map_err(|_| ObjectError::CapabilityReportPoisoned)?;
        *current = report;
        Ok(report)
    }

    /// Upload a bounded stream under a fresh, conditionally reserved key.
    ///
    /// The method consumes exactly `expected_length` bytes, rejects trailing
    /// bytes, verifies the caller-provided SHA-256 before completion, then reads
    /// the complete object back in bounded ranges before returning its manifest
    /// reference. Every attempt uses a fresh key; an uncertain completion never
    /// reuses or overwrites that key.
    ///
    /// # Errors
    ///
    /// Returns admission, provider, input-length, digest, timeout, or cleanup errors.
    pub async fn put_immutable<R>(
        &self,
        reader: &mut R,
        expected_length: u64,
        expected_sha256: ContentDigest,
        owner: TransferOwner,
    ) -> Result<VerifiedObject, ObjectError>
    where
        R: AsyncRead + Unpin,
    {
        let mut upload = self
            .begin_upload(expected_length, expected_sha256, owner)
            .await?;
        upload.transfer_from(reader).await?;
        upload.finish().await
    }

    /// Start an upload session so the caller can explicitly abort it if needed.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::ObjectTooLarge`] for oversized input,
    /// [`ObjectError::CapabilityUnproven`] when required provider behavior has not
    /// been demonstrated, or an admission/provider error during reservation.
    pub async fn begin_upload(
        &self,
        expected_length: u64,
        expected_sha256: ContentDigest,
        owner: TransferOwner,
    ) -> Result<UploadSession, ObjectError> {
        if expected_length > self.limits.max_object_bytes {
            return Err(ObjectError::ObjectTooLarge {
                actual: expected_length,
                maximum: self.limits.max_object_bytes,
            });
        }
        let capabilities = self.capabilities()?;
        if !capabilities.conditional_create {
            return Err(ObjectError::CapabilityUnproven(
                ObjectCapability::ConditionalCreate,
            ));
        }
        if expected_length > 0 && !capabilities.multipart_abort {
            return Err(ObjectError::CapabilityUnproven(
                ObjectCapability::MultipartAbort,
            ));
        }

        let upload_permit = self
            .upload_slots
            .clone()
            .try_acquire_owned()
            .map_err(|_| ObjectError::Busy)?;
        let owner_attribution = attribution(owner);
        let item_permit = self.budget.try_reserve_items(owner_attribution, 1)?;
        let key = self.reserve_unique_key(owner).await?;
        let path = key.as_path();

        let multipart = if expected_length == 0 {
            None
        } else {
            match self
                .request(
                    "start multipart upload",
                    self.store
                        .put_multipart_opts(&path, object_store::PutMultipartOptions::default()),
                )
                .await
            {
                Ok(upload) => Some(upload),
                Err(error) => {
                    // The marker is unreferenced and its key will never be reused.
                    // A provider that accepted Create but lost the multipart
                    // response may also retain an MPU until its lifecycle sweep.
                    let cleanup = self
                        .request("remove unreferenced reservation", self.store.delete(&path))
                        .await;
                    let cause = match error {
                        ObjectError::Provider(provider) if is_unsupported(&provider) => {
                            ObjectError::MultipartUnsupported
                        }
                        other => other,
                    };
                    return Err(with_cleanup(cause, cleanup));
                }
            }
        };

        Ok(UploadSession {
            client: self.clone(),
            key,
            expected_length,
            expected_sha256,
            owner,
            hasher: Sha256::new(),
            received: 0,
            multipart,
            upload_permit: Some(upload_permit),
            item_permit: Some(item_permit),
            state: UploadState::Receiving,
        })
    }

    /// Abort an incomplete upload session and remove its unreferenced key marker.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidUploadState`] if the session belongs to another
    /// client or is already committing, and provider/timeout errors if cleanup fails.
    pub async fn abort_upload(&self, mut upload: UploadSession) -> Result<(), ObjectError> {
        if !Arc::ptr_eq(&self.request_slots, &upload.client.request_slots) {
            return Err(ObjectError::InvalidUploadState(
                "upload session belongs to a different object client",
            ));
        }
        upload.abort_inner().await
    }

    /// Fetch one block by ordinal from a sidecar already authenticated against
    /// the selected manifest. The sidecar's segment identity is checked again
    /// against the supplied object reference before any provider request.
    ///
    /// # Errors
    ///
    /// Returns an error if range reads are unproven, the sidecar identity or block
    /// ordinal is invalid, the range exceeds limits, memory admission fails, the
    /// provider returns unexpected bytes, or block verification detects corruption.
    pub async fn get_verified_range(
        &self,
        object: &ObjectReference,
        sidecar: &tb_format::RemoteDescriptorSidecar<'_>,
        ordinal: u32,
        owner: TransferOwner,
    ) -> Result<VerifiedBlock, ObjectError> {
        if !self.capabilities()?.range_reads {
            return Err(ObjectError::CapabilityUnproven(ObjectCapability::RangeRead));
        }
        if object.length() > self.limits.max_object_bytes {
            return Err(ObjectError::ObjectTooLarge {
                actual: object.length(),
                maximum: self.limits.max_object_bytes,
            });
        }
        if sidecar.segment_len() != object.length()
            || sidecar.segment_sha256() != *object.sha256().as_bytes()
        {
            return Err(ObjectError::InvalidManifestReference(
                "remote sidecar is bound to a different segment identity",
            ));
        }
        let descriptor = sidecar
            .block(ordinal)
            .ok_or(ObjectError::InvalidSidecarBlock { ordinal })?;
        let start = descriptor.offset();
        let end =
            start
                .checked_add(u64::from(descriptor.len()))
                .ok_or(ObjectError::InvalidRange {
                    start,
                    end: u64::MAX,
                    object_length: object.length(),
                })?;
        if start >= end || end > object.length() {
            return Err(ObjectError::InvalidRange {
                start,
                end,
                object_length: object.length(),
            });
        }
        if usize::try_from(descriptor.len()).map_err(|_| ObjectError::RangeTooLarge {
            actual: u64::from(descriptor.len()),
            maximum: self.limits.max_range_bytes,
        })? > self.limits.max_range_bytes
        {
            return Err(ObjectError::RangeTooLarge {
                actual: u64::from(descriptor.len()),
                maximum: self.limits.max_range_bytes,
            });
        }

        let (bytes, reservation) = self
            .read_range(object.key(), object.length(), start..end, owner)
            .await?;
        let actual = ContentDigest::hash(&bytes);
        if actual.as_bytes() != &descriptor.sha256() {
            return Err(ObjectError::BlockCorrupt { offset: start, end });
        }
        Ok(VerifiedBlock::new(bytes, reservation))
    }

    /// Reject remote authority publication until both provider CAS and a
    /// verifiable fenced-ownership authority are integrated.
    ///
    /// # Errors
    ///
    /// Always returns [`ObjectError::UnsupportedGuarantee`] after the conditional
    /// update capability is probed, because fenced manifest authority is not yet
    /// integrated.
    // Keep the publication API awaitable while this fail-closed stub performs
    // only a local capability check; the integrated path will await provider I/O.
    #[allow(clippy::unused_async, clippy::unused_async_trait_impl)]
    pub async fn publish_manifest(
        &self,
        _expected_generation: u64,
        _fencing_certificate: &[u8],
        _verified_references: &[VerifiedObject],
        _next_manifest: &[u8],
    ) -> Result<u64, ObjectError> {
        if !self.capabilities()?.conditional_manifest_update {
            return Err(ObjectError::CapabilityUnproven(
                ObjectCapability::ConditionalManifestUpdate,
            ));
        }
        Err(ObjectError::UnsupportedGuarantee(
            ObjectGuarantee::FencedConditionalManifestPublication,
        ))
    }

    async fn reserve_unique_key(&self, owner: TransferOwner) -> Result<ObjectKey, ObjectError> {
        for _ in 0..MAX_KEY_RESERVATION_ATTEMPTS {
            let key = ObjectKey::fresh();
            let path = key.as_path();
            let _bytes = self.reserve_bytes(owner, CREATE_MARKER.len())?;
            let result = self
                .request(
                    "reserve immutable object key",
                    self.store.put_opts(
                        &path,
                        PutPayload::from(Bytes::from_static(CREATE_MARKER)),
                        PutOptions {
                            mode: PutMode::Create,
                            ..Default::default()
                        },
                    ),
                )
                .await;
            match result {
                Ok(_) => return Ok(key),
                Err(ObjectError::Provider(error)) if is_precondition_rejection(&error) => {}
                Err(error) => {
                    // A timeout or transport error can be returned after the
                    // provider accepted the create-only reservation.
                    let cleanup = self
                        .request(
                            "clean uncertain object reservation",
                            self.store.delete(&path),
                        )
                        .await;
                    return Err(with_cleanup(error, cleanup));
                }
            }
        }
        Err(ObjectError::ObjectKeyCollisionLimit {
            attempts: MAX_KEY_RESERVATION_ATTEMPTS,
        })
    }

    async fn verify_complete_object(
        &self,
        key: &ObjectKey,
        expected_length: u64,
        expected_sha256: ContentDigest,
        owner: TransferOwner,
    ) -> Result<(), ObjectError> {
        let path = key.as_path();
        let meta = self
            .request("read object metadata", self.store.head(&path))
            .await?;
        if meta.size != expected_length {
            return Err(ObjectError::ObjectCorrupt {
                key: key.clone(),
                reason: "stored length differs from the declared length",
            });
        }

        let mut hasher = Sha256::new();
        let mut offset = 0_u64;
        while offset < expected_length {
            let max_range_bytes = u64::try_from(self.limits.max_range_bytes)
                .map_err(|_| ObjectError::InvalidLimits("maximum range size does not fit u64"))?;
            let chunk_length = (expected_length - offset).min(max_range_bytes);
            let end = offset + chunk_length;
            let (bytes, reservation) = self
                .read_range(key, expected_length, offset..end, owner)
                .await?;
            hasher.update(&bytes);
            drop(bytes);
            drop(reservation);
            offset = end;
        }

        let actual = digest_from_hasher(&hasher);
        if actual != expected_sha256 {
            return Err(ObjectError::ObjectCorrupt {
                key: key.clone(),
                reason: "stored SHA-256 differs from the uploaded bytes",
            });
        }
        Ok(())
    }

    async fn read_range(
        &self,
        key: &ObjectKey,
        object_length: u64,
        range: std::ops::Range<u64>,
        owner: TransferOwner,
    ) -> Result<(Bytes, tb_core::WorkReservation), ObjectError> {
        let range_length = range.end - range.start;
        let requested_bytes =
            usize::try_from(range_length).map_err(|_| ObjectError::RangeTooLarge {
                actual: range_length,
                maximum: self.limits.max_range_bytes,
            })?;
        let reservation = self
            .budget
            .try_reserve_work(attribution(owner), requested_bytes, 1)?;
        // object_store yields provider-owned Bytes chunks while this adapter
        // copies them into the returned contiguous Bytes allocation. Reserve
        // a second range-sized allowance before issuing GET so one compliant
        // provider range body and the output buffer are both covered. The
        // provider must honor the requested range at its transport boundary;
        // this adapter also rejects any stream body that exceeds the range.
        let _stream_reservation = self
            .budget
            .try_reserve(attribution(owner), requested_bytes)?;
        let path = key.as_path();
        let request_slot = self.try_request_slot()?;
        let response = self
            .await_provider(
                "read object range",
                self.store
                    .get_opts(&path, GetOptions::new().with_range(Some(range.clone()))),
            )
            .await?;
        if response.range != range || response.meta.size != object_length {
            return Err(ObjectError::UnexpectedRange { key: key.clone() });
        }
        let bytes = self
            .collect_bounded_range(response, requested_bytes, Some(key))
            .await?;
        drop(request_slot);
        Ok((bytes, reservation))
    }

    /// Materialize no more than the already-admitted range length from a
    /// provider stream. `GetResult::bytes()` treats its advertised range as a
    /// capacity hint, so it cannot enforce this resource boundary itself.
    async fn collect_bounded_range(
        &self,
        response: GetResult,
        expected_bytes: usize,
        key: Option<&ObjectKey>,
    ) -> Result<Bytes, ObjectError> {
        let mut stream = response.into_stream();
        timeout(self.limits.request_timeout, async {
            let mut output = Vec::new();
            output.try_reserve_exact(expected_bytes).map_err(|_| {
                ObjectError::AllocationFailed {
                    requested_bytes: expected_bytes,
                }
            })?;
            while let Some(chunk) = stream.next().await {
                let chunk = chunk?;
                if chunk.len() > expected_bytes.saturating_sub(output.len()) {
                    return Err(range_body_mismatch(key));
                }
                output.extend_from_slice(&chunk);
            }
            if output.len() != expected_bytes {
                return Err(range_body_mismatch(key));
            }
            Ok(Bytes::from(output))
        })
        .await
        .map_err(|_| ObjectError::RequestTimeout {
            operation: "read range body",
        })?
    }

    fn reserve_bytes(
        &self,
        owner: TransferOwner,
        bytes: usize,
    ) -> Result<Reservation, AdmissionError> {
        self.budget.try_reserve(attribution(owner), bytes)
    }

    fn try_request_slot(&self) -> Result<OwnedSemaphorePermit, ObjectError> {
        self.request_slots
            .clone()
            .try_acquire_owned()
            .map_err(|_| ObjectError::Busy)
    }

    async fn request<T, F>(&self, operation: &'static str, future: F) -> Result<T, ObjectError>
    where
        F: Future<Output = object_store::Result<T>>,
    {
        let _slot = self.try_request_slot()?;
        self.await_provider(operation, future).await
    }

    async fn await_provider<T, F>(
        &self,
        operation: &'static str,
        future: F,
    ) -> Result<T, ObjectError>
    where
        F: Future<Output = object_store::Result<T>>,
    {
        timeout(self.limits.request_timeout, future)
            .await
            .map_err(|_| ObjectError::RequestTimeout { operation })?
            .map_err(ObjectError::Provider)
    }

    async fn probe_conditional_create(&self, owner: TransferOwner) -> Result<bool, ObjectError> {
        if self.request_slots.available_permits() < 2 {
            return Ok(false);
        }
        let path = probe_path("create");
        let payload_a = Bytes::from_static(b"winner-a");
        let payload_b = Bytes::from_static(b"winner-b");
        let bytes_reservation = self.reserve_bytes(owner, payload_a.len() + payload_b.len())?;
        let Ok(slots) = self.request_slots.clone().try_acquire_many_owned(2) else {
            return Ok(false);
        };
        let put_a = self.await_provider(
            "probe conditional create A",
            self.store.put_opts(
                &path,
                PutPayload::from(payload_a.clone()),
                PutOptions {
                    mode: PutMode::Create,
                    ..Default::default()
                },
            ),
        );
        let put_b = self.await_provider(
            "probe conditional create B",
            self.store.put_opts(
                &path,
                PutPayload::from(payload_b.clone()),
                PutOptions {
                    mode: PutMode::Create,
                    ..Default::default()
                },
            ),
        );
        let (a, b) = tokio::join!(put_a, put_b);
        drop(slots);
        let one_winner = matches!((&a, &b), (Ok(_), Err(ObjectError::Provider(error)))
            if is_precondition_rejection(error))
            || matches!((&a, &b), (Err(ObjectError::Provider(error)), Ok(_))
                if is_precondition_rejection(error));
        if one_winner {
            drop(bytes_reservation);
            let bytes = self.probe_read(&path, 0..8, 8, owner).await;
            let _ = self
                .request("clean create probe", self.store.delete(&path))
                .await;
            return match bytes {
                Ok((bytes, _reservation)) if bytes == payload_a || bytes == payload_b => Ok(true),
                Ok((_bytes, _reservation)) => Err(ObjectError::ConditionalProbeFailed),
                Err(ObjectError::Provider(error)) if is_unsupported(&error) => Ok(false),
                Err(error) => Err(error),
            };
        }
        drop(bytes_reservation);
        let _ = self
            .request("clean create probe", self.store.delete(&path))
            .await;
        if is_unsupported_result(&a) || is_unsupported_result(&b) {
            return Ok(false);
        }
        Err(ObjectError::ConditionalProbeFailed)
    }

    async fn probe_range_read(&self, owner: TransferOwner) -> Result<bool, ObjectError> {
        let path = probe_path("range");
        let source = Bytes::from_static(b"r");
        let _bytes = self.reserve_bytes(owner, source.len())?;
        if let Err(error) = self
            .request(
                "write range probe",
                self.store.put_opts(
                    &path,
                    PutPayload::from(source.clone()),
                    PutOptions::default(),
                ),
            )
            .await
        {
            let _ = self
                .request("clean range probe", self.store.delete(&path))
                .await;
            return Err(error);
        }
        let result = self
            .probe_read(&path, 0..1, source.len() as u64, owner)
            .await;
        let _ = self
            .request("clean range probe", self.store.delete(&path))
            .await;
        match result {
            Ok((bytes, _reservation)) => Ok(bytes.as_ref() == b"r"),
            Err(ObjectError::UnexpectedRange { .. } | ObjectError::RangeProbeFailed) => Ok(false),
            Err(ObjectError::Provider(error)) if is_unsupported(&error) => Ok(false),
            Err(error) => Err(error),
        }
    }

    async fn probe_multipart_abort(&self) -> Result<bool, ObjectError> {
        let path = probe_path("abort");
        let upload = self
            .request(
                "start multipart abort probe",
                self.store
                    .put_multipart_opts(&path, object_store::PutMultipartOptions::default()),
            )
            .await;
        let Ok(mut upload) = upload else {
            return match upload {
                Err(ObjectError::Provider(error)) if is_unsupported(&error) => Ok(false),
                Err(error) => Err(error),
                Ok(_) => unreachable!(),
            };
        };
        let result = {
            let _slot = self.try_request_slot()?;
            timeout(self.limits.request_timeout, upload.abort()).await
        };
        match result {
            Ok(Ok(())) => Ok(true),
            Ok(Err(error)) if is_unsupported(&error) => Ok(false),
            Ok(Err(error)) => Err(ObjectError::Provider(error)),
            Err(_) => Err(ObjectError::RequestTimeout {
                operation: "probe multipart abort",
            }),
        }
    }

    async fn probe_conditional_update(&self, owner: TransferOwner) -> Result<bool, ObjectError> {
        if self.request_slots.available_permits() < 2 {
            return Ok(false);
        }
        let path = probe_path("update");
        let Some(version) = self.seed_conditional_update_probe(&path, owner).await? else {
            return Ok(false);
        };
        let bytes_reservation = self.reserve_bytes(owner, 16)?;
        let Ok(slots) = self.request_slots.clone().try_acquire_many_owned(2) else {
            let _ = self
                .request("clean update probe", self.store.delete(&path))
                .await;
            return Ok(false);
        };
        let update_a = self.await_provider(
            "probe conditional update A",
            self.store.put_opts(
                &path,
                PutPayload::from(Bytes::from_static(b"update-a")),
                PutOptions {
                    mode: PutMode::Update(version.clone()),
                    ..Default::default()
                },
            ),
        );
        let update_b = self.await_provider(
            "probe conditional update B",
            self.store.put_opts(
                &path,
                PutPayload::from(Bytes::from_static(b"update-b")),
                PutOptions {
                    mode: PutMode::Update(version),
                    ..Default::default()
                },
            ),
        );
        let (a, b) = tokio::join!(update_a, update_b);
        drop(slots);
        let one_winner = matches!((&a, &b), (Ok(_), Err(ObjectError::Provider(error)))
            if is_precondition_rejection(error))
            || matches!((&a, &b), (Err(ObjectError::Provider(error)), Ok(_))
                if is_precondition_rejection(error));
        let result = if one_winner {
            drop(bytes_reservation);
            let current = self.probe_read(&path, 0..8, 8, owner).await;
            match current {
                Ok((bytes, _reservation))
                    if bytes.as_ref() == b"update-a" || bytes.as_ref() == b"update-b" =>
                {
                    Ok(true)
                }
                Ok((_bytes, _reservation)) => Err(ObjectError::ConditionalProbeFailed),
                Err(ObjectError::Provider(error)) if is_unsupported(&error) => Ok(false),
                Err(error) => Err(error),
            }
        } else if is_unsupported_result(&a) || is_unsupported_result(&b) {
            drop(bytes_reservation);
            Ok(false)
        } else {
            drop(bytes_reservation);
            Err(ObjectError::ConditionalProbeFailed)
        };
        let _ = self
            .request("clean update probe", self.store.delete(&path))
            .await;
        result
    }

    async fn seed_conditional_update_probe(
        &self,
        path: &Path,
        owner: TransferOwner,
    ) -> Result<Option<UpdateVersion>, ObjectError> {
        let _seed_reservation = self.reserve_bytes(owner, b"seed".len())?;
        let seed = self
            .request(
                "seed conditional update probe",
                self.store.put_opts(
                    path,
                    PutPayload::from(Bytes::from_static(b"seed")),
                    PutOptions {
                        mode: PutMode::Create,
                        ..Default::default()
                    },
                ),
            )
            .await;
        let seed = match seed {
            Ok(seed) => seed,
            Err(ObjectError::Provider(error)) if is_unsupported(&error) => {
                let _ = self
                    .request("clean update probe", self.store.delete(path))
                    .await;
                return Ok(None);
            }
            Err(error) => {
                let _ = self
                    .request("clean update probe", self.store.delete(path))
                    .await;
                return Err(error);
            }
        };
        if seed.e_tag.is_none() && seed.version.is_none() {
            let _ = self
                .request("clean update probe", self.store.delete(path))
                .await;
            return Ok(None);
        }
        Ok(Some(UpdateVersion::from(seed)))
    }

    async fn probe_read(
        &self,
        path: &Path,
        range: std::ops::Range<u64>,
        object_length: u64,
        owner: TransferOwner,
    ) -> Result<(Bytes, tb_core::WorkReservation), ObjectError> {
        let requested =
            usize::try_from(range.end - range.start).map_err(|_| ObjectError::RangeTooLarge {
                actual: range.end - range.start,
                maximum: self.limits.max_range_bytes,
            })?;
        let reservation = self
            .budget
            .try_reserve_work(attribution(owner), requested, 1)?;
        let _stream_reservation = self.budget.try_reserve(attribution(owner), requested)?;
        let request_slot = self.try_request_slot()?;
        let response = self
            .await_provider(
                "probe range read",
                self.store
                    .get_opts(path, GetOptions::new().with_range(Some(range.clone()))),
            )
            .await?;
        if response.range != range || response.meta.size != object_length {
            return Err(ObjectError::RangeProbeFailed);
        }
        let bytes = self
            .collect_bounded_range(response, requested, None)
            .await?;
        drop(request_slot);
        Ok((bytes, reservation))
    }
}

/// One in-flight immutable upload. Dropping an incomplete session schedules
/// best-effort abort and marker cleanup on the current Tokio runtime.
pub struct UploadSession {
    client: ObjectClient,
    key: ObjectKey,
    expected_length: u64,
    expected_sha256: ContentDigest,
    owner: TransferOwner,
    hasher: Sha256,
    received: u64,
    multipart: Option<Box<dyn MultipartUpload>>,
    upload_permit: Option<OwnedSemaphorePermit>,
    item_permit: Option<ItemReservation>,
    state: UploadState,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum UploadState {
    Receiving,
    Ready,
    Committing,
    Complete,
    Aborted,
}

impl UploadSession {
    /// Unique key reserved before multipart transfer begins.
    #[must_use]
    pub const fn key(&self) -> &ObjectKey {
        &self.key
    }

    /// Stream exactly the declared length into fixed-size, admitted parts.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidUploadState`] if the session is not receiving,
    /// or an input, admission, provider, timeout, or cleanup error if transfer fails.
    pub async fn transfer_from<R>(&mut self, reader: &mut R) -> Result<(), ObjectError>
    where
        R: AsyncRead + Unpin,
    {
        if self.state != UploadState::Receiving {
            return Err(ObjectError::InvalidUploadState(
                "transfer can run once on a receiving session",
            ));
        }
        let result = self.transfer_inner(reader).await;
        match result {
            Ok(()) => {
                self.state = UploadState::Ready;
                Ok(())
            }
            Err(cause) => Err(self.cleanup_failed_upload(cause).await),
        }
    }

    /// Complete, read back, and verify the reserved immutable object.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidUploadState`] unless transfer completed, or
    /// an error if digest verification, provider completion, readback, or cleanup fails.
    pub async fn finish(mut self) -> Result<VerifiedObject, ObjectError> {
        if self.state != UploadState::Ready {
            return Err(ObjectError::InvalidUploadState(
                "finish requires one complete input stream",
            ));
        }
        let actual = digest_from_hasher(&self.hasher);
        if actual != self.expected_sha256 {
            let cause = ObjectError::ContentDigestMismatch {
                expected: self.expected_sha256,
                actual,
            };
            return Err(self.cleanup_failed_upload(cause).await);
        }

        let request_slot = match self.client.try_request_slot() {
            Ok(slot) => slot,
            Err(cause) => return Err(self.cleanup_failed_upload(cause).await),
        };
        self.state = UploadState::Committing;
        let completion = if let Some(upload) = self.multipart.as_mut() {
            let result = timeout(self.client.limits.request_timeout, upload.complete()).await;
            match result {
                Ok(Ok(_)) => Ok(()),
                Ok(Err(error)) => Err(CompletionFailure::Provider(error)),
                Err(_) => Err(CompletionFailure::Timeout),
            }
        } else {
            let empty = Bytes::new();
            self.client
                .await_provider(
                    "write empty immutable object",
                    self.client.store.put_opts(
                        &self.key.as_path(),
                        PutPayload::from(empty),
                        PutOptions {
                            mode: PutMode::Overwrite,
                            ..Default::default()
                        },
                    ),
                )
                .await
                .map(|_| ())
                .map_err(CompletionFailure::Object)
        };
        drop(request_slot);

        // A multipart completion error or timeout can race with server-side
        // commit. The key remains unique and unreferenced until readback proves
        // its exact content; never abort or delete a possibly completed object.
        self.multipart.take();
        let completion_error = match completion {
            Ok(()) => None,
            Err(CompletionFailure::Provider(error)) => Some(error.to_string()),
            Err(CompletionFailure::Object(error)) => Some(error.to_string()),
            Err(CompletionFailure::Timeout) => Some("request timed out".to_owned()),
        };
        if let Err(verification) = self
            .client
            .verify_complete_object(
                &self.key,
                self.expected_length,
                self.expected_sha256,
                self.owner,
            )
            .await
        {
            if let Some(completion_error) = completion_error {
                self.state = UploadState::Aborted;
                return Err(ObjectError::CompletionUncertain {
                    key: self.key.clone(),
                    reason: format!("completion: {completion_error}; readback: {verification}"),
                });
            }
            return Err(verification);
        }

        self.state = UploadState::Complete;
        self.upload_permit.take();
        self.item_permit.take();
        Ok(VerifiedObject::new(
            self.key.clone(),
            self.expected_length,
            self.expected_sha256,
        ))
    }

    /// Explicitly abort this session and remove its unreachable marker object.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidUploadState`] if completion has started, or
    /// a provider/timeout error if multipart abort or marker removal fails.
    pub async fn abort_upload(mut self) -> Result<(), ObjectError> {
        self.abort_inner().await
    }

    async fn transfer_inner<R>(&mut self, reader: &mut R) -> Result<(), ObjectError>
    where
        R: AsyncRead + Unpin,
    {
        if self.expected_length == 0 {
            let _reservation = self.client.reserve_bytes(self.owner, 1)?;
            let mut trailing = [0_u8; 1];
            let count = timeout(
                self.client.limits.request_timeout,
                reader.read(&mut trailing),
            )
            .await
            .map_err(|_| ObjectError::RequestTimeout {
                operation: "read upload input",
            })??;
            if count != 0 {
                return Err(ObjectError::InputLengthMismatch {
                    expected: 0,
                    actual: 1,
                });
            }
            return Ok(());
        }

        let part_bytes = self.client.limits.upload_part_bytes;
        while self.received < self.expected_length {
            let remaining = self.expected_length - self.received;
            let part_len = usize::try_from(remaining.min(part_bytes as u64)).map_err(|_| {
                ObjectError::ObjectTooLarge {
                    actual: remaining,
                    maximum: self.client.limits.max_object_bytes,
                }
            })?;
            let _reservation = self.client.reserve_bytes(self.owner, part_len)?;
            let mut buffer = Vec::new();
            buffer
                .try_reserve_exact(part_len)
                .map_err(|_| ObjectError::AllocationFailed {
                    requested_bytes: part_len,
                })?;
            buffer.resize(part_len, 0);

            let mut filled = 0;
            while filled < part_len {
                let count = timeout(
                    self.client.limits.request_timeout,
                    reader.read(&mut buffer[filled..]),
                )
                .await
                .map_err(|_| ObjectError::RequestTimeout {
                    operation: "read upload input",
                })??;
                if count == 0 {
                    return Err(ObjectError::InputLengthMismatch {
                        expected: self.expected_length,
                        actual: self.received + filled as u64,
                    });
                }
                filled += count;
            }

            self.hasher.update(&buffer);
            let upload = self
                .multipart
                .as_mut()
                .ok_or(ObjectError::InvalidUploadState(
                    "nonempty input has no multipart upload",
                ))?;
            let part = upload.put_part(PutPayload::from(Bytes::from(buffer)));
            let _request_slot = self.client.try_request_slot()?;
            timeout(self.client.limits.request_timeout, part)
                .await
                .map_err(|_| ObjectError::RequestTimeout {
                    operation: "upload multipart part",
                })??;
            self.received += part_len as u64;
        }

        let _reservation = self.client.reserve_bytes(self.owner, 1)?;
        let mut trailing = [0_u8; 1];
        let count = timeout(
            self.client.limits.request_timeout,
            reader.read(&mut trailing),
        )
        .await
        .map_err(|_| ObjectError::RequestTimeout {
            operation: "read upload input",
        })??;
        if count != 0 {
            return Err(ObjectError::InputLengthMismatch {
                expected: self.expected_length,
                actual: self.expected_length + 1,
            });
        }
        Ok(())
    }

    async fn abort_inner(&mut self) -> Result<(), ObjectError> {
        if matches!(self.state, UploadState::Complete | UploadState::Committing) {
            return Err(ObjectError::InvalidUploadState(
                "a committing or completed key cannot be deleted as an abort",
            ));
        }
        if self.state == UploadState::Aborted {
            return Ok(());
        }
        if let Some(upload) = self.multipart.as_mut() {
            let request_slot = self.client.try_request_slot()?;
            timeout(self.client.limits.request_timeout, upload.abort())
                .await
                .map_err(|_| ObjectError::RequestTimeout {
                    operation: "abort multipart upload",
                })??;
            drop(request_slot);
            self.multipart.take();
        }
        self.client
            .request(
                "remove unreferenced upload marker",
                self.client.store.delete(&self.key.as_path()),
            )
            .await?;
        self.state = UploadState::Aborted;
        self.upload_permit.take();
        self.item_permit.take();
        Ok(())
    }

    async fn cleanup_failed_upload(&mut self, cause: ObjectError) -> ObjectError {
        with_cleanup(cause, self.abort_inner().await)
    }
}

impl Drop for UploadSession {
    fn drop(&mut self) {
        if self.state != UploadState::Receiving && self.state != UploadState::Ready {
            return;
        }
        let Some(runtime) = tokio::runtime::Handle::try_current().ok() else {
            // S3-compatible providers need an abort-incomplete-multipart
            // lifecycle rule for process shutdown or drop outside a runtime.
            return;
        };
        let client = self.client.clone();
        let key = self.key.clone();
        let upload = self.multipart.take();
        let upload_permit = self.upload_permit.take();
        let item_permit = self.item_permit.take();
        runtime.spawn(async move {
            if let Some(mut upload) = upload
                && let Ok(Ok(_slot)) = timeout(
                    client.limits.request_timeout,
                    client.request_slots.clone().acquire_owned(),
                )
                .await
            {
                let _ = timeout(client.limits.request_timeout, upload.abort()).await;
            }
            if let Ok(Ok(_slot)) = timeout(
                client.limits.request_timeout,
                client.request_slots.clone().acquire_owned(),
            )
            .await
            {
                let _ = client
                    .await_provider(
                        "remove dropped upload marker",
                        client.store.delete(&key.as_path()),
                    )
                    .await;
            }
            drop(item_permit);
            drop(upload_permit);
        });
    }
}

#[derive(Debug)]
enum CompletionFailure {
    Provider(ProviderError),
    Object(ObjectError),
    Timeout,
}

fn attribution(owner: TransferOwner) -> Attribution {
    Attribution {
        tenant: owner.tenant,
        worker: owner.worker,
        category: MemoryCategory::Transfer,
    }
}

fn digest_from_hasher(hasher: &Sha256) -> ContentDigest {
    let result = hasher.clone().finalize();
    let mut digest = [0_u8; 32];
    digest.copy_from_slice(&result);
    ContentDigest::from_bytes(digest)
}

fn probe_path(operation: &str) -> Path {
    let id = tb_core::DocumentId::new();
    Path::from(format!("tb-object-probes/v1/{operation}/{id}"))
}

fn is_precondition_rejection(error: &ProviderError) -> bool {
    matches!(
        error,
        ProviderError::AlreadyExists { .. } | ProviderError::Precondition { .. }
    )
}

fn is_unsupported(error: &ProviderError) -> bool {
    matches!(
        error,
        ProviderError::NotImplemented { .. } | ProviderError::NotSupported { .. }
    )
}

fn is_unsupported_result<T>(result: &Result<T, ObjectError>) -> bool {
    matches!(result, Err(ObjectError::Provider(error)) if is_unsupported(error))
}

fn range_body_mismatch(key: Option<&ObjectKey>) -> ObjectError {
    key.map_or(ObjectError::RangeProbeFailed, |key| {
        ObjectError::UnexpectedRange { key: key.clone() }
    })
}

fn with_cleanup(cause: ObjectError, cleanup: Result<(), ObjectError>) -> ObjectError {
    match cleanup {
        Ok(()) => cause,
        Err(ObjectError::Provider(cleanup)) => ObjectError::UploadCleanup {
            cause: Box::new(cause),
            cleanup,
        },
        Err(cleanup) => ObjectError::UploadCleanupUncertain {
            cause: Box::new(cause),
            cleanup: cleanup.to_string(),
        },
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use bytes::Bytes;
    use futures_util::{StreamExt, stream};
    use object_store::{
        GetOptions, GetResultPayload, ObjectStore, ObjectStoreExt, memory::InMemory,
    };
    use tb_core::{BudgetConfig, MemoryBudget, TenantId, WorkerId};
    use tb_format::{
        FormatLimits, RemoteBlockDigest, RemoteDescriptorInput, decode_remote_descriptor_sidecar,
        encode_remote_descriptor_sidecar_into,
    };

    use super::*;

    const MULTIPART_PART_BYTES: usize = 5 * 1024 * 1024;

    fn owner() -> TransferOwner {
        TransferOwner {
            tenant: TenantId::from_bytes([1; 16]),
            worker: WorkerId(1),
        }
    }

    fn test_limits() -> ObjectLimits {
        ObjectLimits {
            max_object_bytes: 16 * 1024 * 1024,
            upload_part_bytes: MULTIPART_PART_BYTES,
            max_range_bytes: 64 * 1024,
            max_active_requests: 4,
            max_upload_sessions: 2,
            request_timeout: Duration::from_secs(2),
        }
    }

    fn client(store: &Arc<InMemory>, limits: ObjectLimits) -> ObjectClient {
        let budget = MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).unwrap());
        let client = ObjectClient::new(Arc::clone(store), budget, limits).unwrap();
        *client.capabilities.write().unwrap() = ProviderCapabilities {
            conditional_create: true,
            range_reads: true,
            multipart_abort: true,
            conditional_manifest_update: true,
        };
        client
    }

    fn one_block_segment() -> Vec<u8> {
        let mut segment = vec![0_u8; 81];
        segment[36..56].copy_from_slice(b"authenticated-range!");
        segment[56] = b'D';
        segment
    }

    fn encoded_sidecar(segment: &[u8]) -> (Vec<u8>, [u8; 32]) {
        let segment_sha256 = ContentDigest::hash(segment);
        let blocks = [RemoteBlockDigest {
            ordinal: 0,
            offset: 36,
            len: 20,
            sha256: *ContentDigest::hash(&segment[36..56]).as_bytes(),
        }];
        let input = RemoteDescriptorInput {
            segment_len: segment.len() as u64,
            segment_sha256: *segment_sha256.as_bytes(),
            directory_offset: 56,
            directory_len: 1,
            directory_sha256: *ContentDigest::hash(&segment[56..57]).as_bytes(),
            blocks: &blocks,
        };
        let mut bytes = Vec::new();
        let sidecar_sha256 =
            encode_remote_descriptor_sidecar_into(&mut bytes, &input, &FormatLimits::default())
                .unwrap();
        (bytes, sidecar_sha256)
    }

    fn decode_sidecar<'a>(
        bytes: &'a [u8],
        sidecar_sha256: [u8; 32],
        segment: &[u8],
    ) -> tb_format::RemoteDescriptorSidecar<'a> {
        decode_remote_descriptor_sidecar(
            bytes,
            sidecar_sha256,
            segment.len() as u64,
            *ContentDigest::hash(segment).as_bytes(),
            &FormatLimits::default(),
        )
        .unwrap()
    }

    #[test]
    fn default_limits_allow_the_declared_object_ceiling() {
        ObjectLimits::default().validate().unwrap();
    }

    #[tokio::test]
    async fn conditional_capability_probe_demonstrates_provider_behavior() {
        let store = Arc::new(InMemory::new());
        let client = client(&store, test_limits());

        let report = client.probe_capabilities(owner()).await.unwrap();

        assert!(report.conditional_create);
        assert!(report.range_reads);
        assert!(report.multipart_abort);
        assert!(report.conditional_manifest_update);
    }

    #[tokio::test]
    async fn authenticated_range_rejects_corrupt_bytes_and_wrong_segment() {
        let store = Arc::new(InMemory::new());
        let client = client(&store, test_limits());
        let segment = one_block_segment();
        let (sidecar_bytes, sidecar_sha256) = encoded_sidecar(&segment);
        let sidecar = decode_sidecar(&sidecar_bytes, sidecar_sha256, &segment);
        let key = ObjectKey::fresh();
        let reference = ObjectReference::from_manifest(
            key.clone(),
            segment.len() as u64,
            ContentDigest::hash(&segment),
        )
        .unwrap();
        store
            .put(&key.as_path(), Bytes::from(segment.clone()).into())
            .await
            .unwrap();
        let block = client
            .get_verified_range(&reference, &sidecar, 0, owner())
            .await
            .unwrap();
        assert_eq!(block.as_bytes().as_ref(), &segment[36..56]);
        drop(block);

        let mut corrupted = segment.clone();
        corrupted[40] ^= 0xff;
        store
            .put(&key.as_path(), Bytes::from(corrupted).into())
            .await
            .unwrap();
        assert!(matches!(
            client
                .get_verified_range(&reference, &sidecar, 0, owner())
                .await,
            Err(ObjectError::BlockCorrupt { .. })
        ));

        let other_reference = ObjectReference::from_manifest(
            key,
            segment.len() as u64,
            ContentDigest::hash(b"different complete segment"),
        )
        .unwrap();
        assert!(matches!(
            client
                .get_verified_range(&other_reference, &sidecar, 0, owner())
                .await,
            Err(ObjectError::InvalidManifestReference(_))
        ));
    }

    #[tokio::test]
    async fn range_limit_and_missing_ordinal_fail_before_fetch() {
        let store = Arc::new(InMemory::new());
        let mut limits = test_limits();
        limits.max_range_bytes = 19;
        let client = client(&store, limits);
        let segment = one_block_segment();
        let (sidecar_bytes, sidecar_sha256) = encoded_sidecar(&segment);
        let sidecar = decode_sidecar(&sidecar_bytes, sidecar_sha256, &segment);
        let key = ObjectKey::fresh();
        let reference = ObjectReference::from_manifest(
            key,
            segment.len() as u64,
            ContentDigest::hash(&segment),
        )
        .unwrap();

        assert!(matches!(
            client
                .get_verified_range(&reference, &sidecar, 0, owner())
                .await,
            Err(ObjectError::RangeTooLarge { actual: 20, .. })
        ));
        assert!(matches!(
            client
                .get_verified_range(&reference, &sidecar, 1, owner())
                .await,
            Err(ObjectError::InvalidSidecarBlock { ordinal: 1 })
        ));
        assert!(store.head(&reference.key().as_path()).await.is_err());
    }

    #[tokio::test]
    async fn range_read_admits_provider_and_output_buffers_before_get() {
        let store = Arc::new(InMemory::new());
        let budget = MemoryBudget::new(BudgetConfig::new(39, 0).unwrap());
        let client = ObjectClient::new(Arc::clone(&store), budget.clone(), test_limits()).unwrap();
        *client.capabilities.write().unwrap() = ProviderCapabilities {
            range_reads: true,
            ..ProviderCapabilities::default()
        };
        let segment = one_block_segment();
        let (sidecar_bytes, sidecar_sha256) = encoded_sidecar(&segment);
        let sidecar = decode_sidecar(&sidecar_bytes, sidecar_sha256, &segment);
        let reference = ObjectReference::from_manifest(
            ObjectKey::fresh(),
            segment.len() as u64,
            ContentDigest::hash(&segment),
        )
        .unwrap();

        assert!(matches!(
            client
                .get_verified_range(&reference, &sidecar, 0, owner())
                .await,
            Err(ObjectError::Admission(_))
        ));
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert!(store.head(&reference.key().as_path()).await.is_err());
    }

    #[tokio::test]
    async fn oversized_provider_range_body_is_rejected_without_growing_output() {
        let store = Arc::new(InMemory::new());
        let client = client(&store, test_limits());
        let key = ObjectKey::fresh();
        store
            .put(&key.as_path(), Bytes::from_static(b"x").into())
            .await
            .unwrap();
        let mut response = store
            .get_opts(&key.as_path(), GetOptions::new().with_range(Some(0..1)))
            .await
            .unwrap();
        response.payload = GetResultPayload::Stream(
            stream::iter([Ok::<_, ProviderError>(Bytes::from(vec![7; 1024 * 1024]))]).boxed(),
        );

        assert!(matches!(
            client.collect_bounded_range(response, 1, Some(&key)).await,
            Err(ObjectError::UnexpectedRange { .. })
        ));
    }

    #[tokio::test]
    async fn failed_upload_aborts_multipart_and_removes_its_reserved_key() {
        let store = Arc::new(InMemory::new());
        let client = client(&store, test_limits());
        let data = vec![3; MULTIPART_PART_BYTES - 1];
        let mut upload = client
            .begin_upload(
                MULTIPART_PART_BYTES as u64,
                ContentDigest::hash(&data),
                owner(),
            )
            .await
            .unwrap();
        let key = upload.key().clone();
        let mut input = &data[..];

        assert!(matches!(
            upload.transfer_from(&mut input).await,
            Err(ObjectError::InputLengthMismatch { .. })
        ));
        assert!(store.head(&key.as_path()).await.is_err());
        assert_eq!(client.budget.snapshot().reserved_items, 0);
    }

    #[tokio::test]
    async fn successful_multipart_upload_is_read_back_before_becoming_verified() {
        let store = Arc::new(InMemory::new());
        let mut limits = test_limits();
        limits.max_range_bytes = 1024 * 1024;
        let client = client(&store, limits);
        let data = vec![9; MULTIPART_PART_BYTES];
        let mut input = &data[..];

        let verified = client
            .put_immutable(
                &mut input,
                data.len() as u64,
                ContentDigest::hash(&data),
                owner(),
            )
            .await
            .unwrap();

        assert_eq!(verified.length(), data.len() as u64);
        assert_eq!(verified.sha256(), ContentDigest::hash(&data));
        let metadata = store.head(&verified.key().as_path()).await.unwrap();
        assert_eq!(metadata.size, data.len() as u64);
        assert_eq!(client.budget.snapshot().reserved_items, 0);
    }

    #[tokio::test]
    async fn checksum_mismatch_aborts_upload_without_publishing_a_reference() {
        let store = Arc::new(InMemory::new());
        let client = client(&store, test_limits());
        let data = vec![5; MULTIPART_PART_BYTES];
        let mut upload = client
            .begin_upload(
                MULTIPART_PART_BYTES as u64,
                ContentDigest::hash(b"wrong content"),
                owner(),
            )
            .await
            .unwrap();
        let key = upload.key().clone();
        let mut input = &data[..];
        upload.transfer_from(&mut input).await.unwrap();

        assert!(matches!(
            upload.finish().await,
            Err(ObjectError::ContentDigestMismatch { .. })
        ));
        assert!(store.head(&key.as_path()).await.is_err());
        assert_eq!(client.budget.snapshot().reserved_items, 0);
    }
}
