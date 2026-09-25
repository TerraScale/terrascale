//! Real-provider object transfer checks for the separately scheduled Slow suite.
//!
//! The test requires a run-unique, pre-provisioned RustFS bucket because
//! `object_store` does not expose bucket creation or deletion. The Slow suite
//! fails clearly when its four fixture settings are missing; it never skips or
//! substitutes an in-memory provider.

use std::sync::Arc;
use std::time::Duration;

use object_store::aws::AmazonS3Builder;
use object_store::path::Path;
use object_store::{ObjectStore, ObjectStoreExt};
use tb_core::{BudgetConfig, MemoryBudget, TenantId, WorkerId};
use tb_format::{
    FormatLimits, RemoteBlockDigest, RemoteDescriptorInput, decode_remote_descriptor_sidecar,
    encode_remote_descriptor_sidecar_into,
};
use tb_object::{ContentDigest, ObjectClient, ObjectLimits, TransferOwner};

const MULTIPART_PART_BYTES: usize = 5 * 1024 * 1024;
const SEGMENT_BYTES: usize = MULTIPART_PART_BYTES + 1024 * 1024;
const BLOCK_OFFSET: usize = 2 * 1024 * 1024 + 37;
const BLOCK_BYTES: usize = 4096;

struct RustFsFixture {
    endpoint: String,
    bucket: String,
    access_key: String,
    secret_key: String,
}

impl RustFsFixture {
    fn from_environment() -> Self {
        Self {
            endpoint: std::env::var("TERRABASE_RUSTFS_TEST_ENDPOINT")
                .expect("RustFS Slow fixture endpoint is required"),
            bucket: std::env::var("TERRABASE_RUSTFS_TEST_UNIQUE_BUCKET")
                .expect("a run-unique RustFS Slow fixture bucket is required"),
            access_key: std::env::var("TERRABASE_RUSTFS_TEST_ACCESS_KEY")
                .expect("RustFS Slow fixture access key is required"),
            secret_key: std::env::var("TERRABASE_RUSTFS_TEST_SECRET_KEY")
                .expect("RustFS Slow fixture secret key is required"),
        }
    }

    fn object_store(&self) -> Result<Arc<impl ObjectStore>, object_store::Error> {
        let store = AmazonS3Builder::new()
            .with_endpoint(&self.endpoint)
            .with_bucket_name(&self.bucket)
            .with_region("us-east-1")
            .with_access_key_id(&self.access_key)
            .with_secret_access_key(&self.secret_key)
            .with_allow_http(self.endpoint.starts_with("http://"))
            .with_virtual_hosted_style_request(false)
            .build()?;
        Ok(Arc::new(store))
    }
}

fn transfer_owner() -> TransferOwner {
    TransferOwner {
        tenant: TenantId::from_bytes([0x71; 16]),
        worker: WorkerId(71),
    }
}

fn object_limits() -> ObjectLimits {
    ObjectLimits {
        max_object_bytes: 32 * 1024 * 1024,
        upload_part_bytes: MULTIPART_PART_BYTES,
        max_range_bytes: 1024 * 1024,
        max_active_requests: 4,
        max_upload_sessions: 2,
        request_timeout: Duration::from_secs(30),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn rustfs_proves_strict_range_and_abort_after_one_uploaded_part() {
    let fixture = RustFsFixture::from_environment();
    let store = fixture
        .object_store()
        .expect("connect to the configured RustFS test provider");
    let memory = MemoryBudget::new(
        BudgetConfig::new(64 * 1024 * 1024, 0).expect("valid object-transfer test budget"),
    );
    let client = ObjectClient::new(Arc::clone(&store), memory, object_limits())
        .expect("configure bounded RustFS object client");
    let owner = transfer_owner();

    let capabilities = client
        .probe_capabilities(owner)
        .await
        .expect("probe RustFS operations used by the object client");
    assert!(
        capabilities.conditional_create,
        "RustFS must demonstrate conditional immutable-key creation"
    );
    assert!(
        capabilities.range_reads,
        "RustFS must demonstrate range reads before verified block access"
    );
    assert!(
        capabilities.multipart_abort,
        "RustFS must demonstrate multipart abort before upload admission"
    );

    let mut segment = vec![0x35; SEGMENT_BYTES];
    for (offset, byte) in segment[BLOCK_OFFSET..BLOCK_OFFSET + BLOCK_BYTES]
        .iter_mut()
        .enumerate()
    {
        *byte = (offset % 251) as u8;
    }
    let directory_offset = (SEGMENT_BYTES - 64) as u64;
    segment[directory_offset as usize..].fill(0x62);
    let block = RemoteBlockDigest {
        ordinal: 0,
        offset: BLOCK_OFFSET as u64,
        len: BLOCK_BYTES as u32,
        sha256: *ContentDigest::hash(&segment[BLOCK_OFFSET..BLOCK_OFFSET + BLOCK_BYTES]).as_bytes(),
    };
    let blocks = [block];
    let input = RemoteDescriptorInput {
        segment_len: segment.len() as u64,
        segment_sha256: *ContentDigest::hash(&segment).as_bytes(),
        directory_offset,
        directory_len: 64,
        directory_sha256: *ContentDigest::hash(&segment[directory_offset as usize..]).as_bytes(),
        blocks: &blocks,
    };
    let mut sidecar_bytes = Vec::new();
    let sidecar_digest =
        encode_remote_descriptor_sidecar_into(&mut sidecar_bytes, &input, &FormatLimits::default())
            .expect("encode authenticated remote block descriptor");
    let sidecar = decode_remote_descriptor_sidecar(
        &sidecar_bytes,
        sidecar_digest,
        segment.len() as u64,
        *ContentDigest::hash(&segment).as_bytes(),
        &FormatLimits::default(),
    )
    .expect("validate authenticated remote block descriptor");

    let mut source = &segment[..];
    let uploaded = client
        .put_immutable(
            &mut source,
            segment.len() as u64,
            ContentDigest::hash(&segment),
            owner,
        )
        .await
        .expect("multipart upload and full-object readback verification");
    let key_path = Path::from(uploaded.key().as_str());
    let block = client
        .get_verified_range(&uploaded.as_reference(), &sidecar, 0, owner)
        .await
        .expect("fetch authenticated strict subrange from longer object");
    assert_eq!(
        block.as_bytes().as_ref(),
        &segment[BLOCK_OFFSET..BLOCK_OFFSET + BLOCK_BYTES]
    );
    drop(block);
    store
        .delete(&key_path)
        .await
        .expect("remove successful test object");
    assert!(store.head(&key_path).await.is_err());

    // The adapter must have submitted one complete non-final S3 part before
    // reaching EOF in the second part and aborting the unreferenced upload.
    let incomplete_length = (2 * MULTIPART_PART_BYTES) as u64;
    let mut upload = client
        .begin_upload(
            incomplete_length,
            ContentDigest::hash(b"deliberately incomplete multipart body"),
            owner,
        )
        .await
        .expect("start multipart upload after capability probe");
    let incomplete_key = Path::from(upload.key().as_str());
    let partial_part = vec![0x47; MULTIPART_PART_BYTES];
    let mut partial_input = &partial_part[..];
    assert!(matches!(
        upload.transfer_from(&mut partial_input).await,
        Err(tb_object::ObjectError::InputLengthMismatch {
            expected,
            actual
        }) if expected == incomplete_length && actual == MULTIPART_PART_BYTES as u64
    ));
    assert!(
        store.head(&incomplete_key).await.is_err(),
        "failed upload must remove its reservation object after multipart abort"
    );
}
