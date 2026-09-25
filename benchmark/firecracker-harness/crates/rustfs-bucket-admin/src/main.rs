//! Run-owned bucket lifecycle inside the client Firecracker guest.
use std::env;
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::Path;
use std::time::{Duration, Instant};

use aws_sdk_s3::config::{BehaviorVersion, Credentials, Region, retry::RetryConfig};
use aws_sdk_s3::error::SdkError;
use aws_sdk_s3::operation::head_bucket::HeadBucketError;
use aws_sdk_s3::{Client, Config};
use serde::{Deserialize, Serialize};

const OWNER_PATH: &str = "/root/fc-bench-data/rustfs-bucket-owner.json";
const OWNER_OUTPUT: &str = "/root/fc-harness-output/bucket-ownership.json";
const MAX_PAGES: usize = 1_000;
const MAX_ITEMS: usize = 100_000;
type AdminResult<T> = Result<T, &'static str>;

#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct OwnerReceipt {
    schema_version: u32,
    endpoint: String,
    bucket: String,
    state: String,
}

struct Inputs {
    endpoint: String,
    bucket: String,
    access_key: String,
    secret_key: String,
}

#[tokio::main(flavor = "multi_thread", worker_threads = 2)]
async fn main() {
    match tokio::time::timeout(Duration::from_secs(110), run()).await {
        Ok(Ok(())) => {}
        Ok(Err(category)) => {
            eprintln!("rustfs-bucket-admin: {category}");
            std::process::exit(1);
        }
        Err(_) => {
            eprintln!("rustfs-bucket-admin: bounded operation deadline exceeded");
            std::process::exit(1);
        }
    }
}

async fn run() -> AdminResult<()> {
    let mut args = env::args().skip(1);
    let operation = args.next().ok_or("expected create or delete")?;
    if args.next().is_some() || !["create", "delete"].contains(&operation.as_str()) {
        return Err("expected exactly one create or delete operation");
    }
    let inputs = read_inputs()?;
    let credentials = Credentials::new(
        inputs.access_key.clone(),
        inputs.secret_key.clone(),
        None,
        None,
        "private-vsock-fixture",
    );
    let config = Config::builder()
        .behavior_version(BehaviorVersion::latest())
        .region(Region::new("us-east-1"))
        .credentials_provider(credentials)
        .endpoint_url(inputs.endpoint.clone())
        .force_path_style(true)
        .retry_config(RetryConfig::standard().with_max_attempts(1))
        .build();
    let client = Client::from_conf(config);
    match operation.as_str() {
        "create" => create_bucket(&client, &inputs).await?,
        "delete" => delete_bucket(&client, &inputs).await?,
        _ => return Err("unsupported operation"),
    }
    println!("{{\"schema_version\":1,\"operation\":\"{operation}\",\"status\":\"passed\"}}");
    Ok(())
}

fn read_inputs() -> AdminResult<Inputs> {
    let get = |name: &str| {
        env::var(name)
            .ok()
            .filter(|value| !value.is_empty())
            .ok_or("missing private fixture environment")
    };
    let endpoint = get("TERRABASE_RUSTFS_TEST_ENDPOINT")?;
    let bucket = get("TERRABASE_RUSTFS_TEST_UNIQUE_BUCKET")?;
    let access_key = get("TERRABASE_RUSTFS_TEST_ACCESS_KEY")?;
    let secret_key = get("TERRABASE_RUSTFS_TEST_SECRET_KEY")?;
    let port = endpoint
        .strip_prefix("http://10.78.0.2:")
        .ok_or("endpoint is outside the private service peer")?;
    if port.parse::<u16>().ok().filter(|port| *port != 0).is_none()
        || bucket.len() != 37
        || !bucket.starts_with("tbfc-")
        || !bucket[5..]
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase())
    {
        return Err("fixture endpoint or unique bucket is invalid");
    }
    Ok(Inputs {
        endpoint,
        bucket,
        access_key,
        secret_key,
    })
}

async fn create_bucket(client: &Client, inputs: &Inputs) -> AdminResult<()> {
    if Path::new(OWNER_PATH).exists() {
        return Err("bucket owner receipt already exists");
    }
    // Prove absence first: us-east-1 CreateBucket may succeed for an existing owned bucket.
    let deadline = Instant::now() + Duration::from_secs(60);
    loop {
        match client.head_bucket().bucket(&inputs.bucket).send().await {
            Ok(_) => return Err("bucket name collision"),
            Err(error)
                if error
                    .as_service_error()
                    .is_some_and(HeadBucketError::is_not_found) =>
            {
                break;
            }
            Err(error) if transient_head_error(&error) && Instant::now() < deadline => {
                tokio::time::sleep(Duration::from_millis(500)).await;
            }
            Err(_) => return Err("authenticated bucket absence check failed"),
        }
    }
    let mut receipt = OwnerReceipt {
        schema_version: 1,
        endpoint: inputs.endpoint.clone(),
        bucket: inputs.bucket.clone(),
        state: "create-intent".to_owned(),
    };
    write_new_receipt(&receipt)?;
    // Never retry an ambiguous mutation; a lost success response cannot prove ownership.
    client
        .create_bucket()
        .bucket(&inputs.bucket)
        .send()
        .await
        .map_err(|_| "authenticated bucket creation failed or is ambiguous")?;
    receipt.state = "owned".to_owned();
    write_receipt(&receipt)?;
    Ok(())
}

fn transient_head_error(error: &SdkError<HeadBucketError>) -> bool {
    matches!(
        error,
        SdkError::TimeoutError(_) | SdkError::DispatchFailure(_)
    ) || error
        .raw_response()
        .is_some_and(|response| response.status().as_u16() >= 500)
}

async fn delete_bucket(client: &Client, inputs: &Inputs) -> AdminResult<()> {
    let mut receipt = read_receipt()?;
    if receipt.schema_version != 1
        || receipt.state != "owned"
        || receipt.endpoint != inputs.endpoint
        || receipt.bucket != inputs.bucket
    {
        return Err("bucket deletion refused: unknown or mismatched owner");
    }
    abort_all_multipart_uploads(client, &inputs.bucket).await?;
    delete_all_objects(client, &inputs.bucket).await?;
    client
        .delete_bucket()
        .bucket(&inputs.bucket)
        .send()
        .await
        .map_err(|_| "owned bucket deletion failed")?;
    receipt.state = "cleaned".to_owned();
    write_receipt(&receipt)?;
    Ok(())
}

async fn abort_all_multipart_uploads(client: &Client, bucket: &str) -> AdminResult<()> {
    let mut key_marker: Option<String> = None;
    let mut upload_id_marker: Option<String> = None;
    let mut uploads = Vec::new();
    for _ in 0..MAX_PAGES {
        let page = client
            .list_multipart_uploads()
            .bucket(bucket)
            .set_key_marker(key_marker.clone())
            .set_upload_id_marker(upload_id_marker.clone())
            .send()
            .await
            .map_err(|_| "owned multipart upload listing failed")?;
        for upload in page.uploads() {
            let key = upload.key().ok_or("multipart upload has no key")?;
            let upload_id = upload.upload_id().ok_or("multipart upload has no ID")?;
            uploads.push((key.to_owned(), upload_id.to_owned()));
            if uploads.len() > MAX_ITEMS {
                return Err("multipart upload count exceeds bounded cleanup limit");
            }
        }
        if page.is_truncated() != Some(true) {
            for (key, upload_id) in uploads {
                client
                    .abort_multipart_upload()
                    .bucket(bucket)
                    .key(key)
                    .upload_id(upload_id)
                    .send()
                    .await
                    .map_err(|_| "owned multipart upload abort failed")?;
            }
            return Ok(());
        }
        let next_key = page
            .next_key_marker()
            .ok_or("multipart listing omitted next key marker")?
            .to_owned();
        let next_id = page
            .next_upload_id_marker()
            .ok_or("multipart listing omitted next upload ID")?
            .to_owned();
        if key_marker.as_deref() == Some(&next_key) && upload_id_marker.as_deref() == Some(&next_id)
        {
            return Err("multipart listing marker did not advance");
        }
        key_marker = Some(next_key);
        upload_id_marker = Some(next_id);
    }
    Err("multipart listing exceeded bounded page count")
}

async fn delete_all_objects(client: &Client, bucket: &str) -> AdminResult<()> {
    let mut token: Option<String> = None;
    let mut keys = Vec::new();
    for _ in 0..MAX_PAGES {
        let page = client
            .list_objects_v2()
            .bucket(bucket)
            .set_continuation_token(token.clone())
            .send()
            .await
            .map_err(|_| "owned object listing failed")?;
        for object in page.contents() {
            let key = object.key().ok_or("listed object has no key")?;
            keys.push(key.to_owned());
            if keys.len() > MAX_ITEMS {
                return Err("object count exceeds bounded cleanup limit");
            }
        }
        if page.is_truncated() != Some(true) {
            for key in keys {
                client
                    .delete_object()
                    .bucket(bucket)
                    .key(key)
                    .send()
                    .await
                    .map_err(|_| "owned object deletion failed")?;
            }
            return Ok(());
        }
        let next = page
            .next_continuation_token()
            .ok_or("object listing omitted continuation token")?
            .to_owned();
        if token.as_deref() == Some(&next) {
            return Err("object listing token did not advance");
        }
        token = Some(next);
    }
    Err("object listing exceeded bounded page count")
}

fn write_new_receipt(receipt: &OwnerReceipt) -> AdminResult<()> {
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(OWNER_PATH)
        .map_err(|_| "could not create unique bucket owner receipt")?;
    serde_json::to_writer_pretty(&mut file, receipt)
        .map_err(|_| "owner receipt serialization failed")?;
    file.write_all(b"\n")
        .map_err(|_| "owner receipt write failed")?;
    file.sync_all().map_err(|_| "owner receipt sync failed")?;
    copy_receipt_to_output(receipt)
}

fn write_receipt(receipt: &OwnerReceipt) -> AdminResult<()> {
    let temporary = format!("{OWNER_PATH}.tmp");
    let mut file = File::create(&temporary).map_err(|_| "owner receipt update failed")?;
    serde_json::to_writer_pretty(&mut file, receipt)
        .map_err(|_| "owner receipt serialization failed")?;
    file.write_all(b"\n")
        .map_err(|_| "owner receipt write failed")?;
    file.sync_all().map_err(|_| "owner receipt sync failed")?;
    fs::rename(temporary, OWNER_PATH).map_err(|_| "owner receipt replacement failed")?;
    File::open(
        Path::new(OWNER_PATH)
            .parent()
            .ok_or("owner receipt parent missing")?,
    )
    .and_then(|directory| directory.sync_all())
    .map_err(|_| "owner receipt directory sync failed")?;
    copy_receipt_to_output(receipt)
}

fn read_receipt() -> AdminResult<OwnerReceipt> {
    let metadata =
        fs::symlink_metadata(OWNER_PATH).map_err(|_| "bucket owner receipt is absent")?;
    if !metadata.file_type().is_file() {
        return Err("bucket owner receipt is not regular");
    }
    serde_json::from_slice(&fs::read(OWNER_PATH).map_err(|_| "bucket owner receipt read failed")?)
        .map_err(|_| "bucket owner receipt is malformed")
}

fn copy_receipt_to_output(receipt: &OwnerReceipt) -> AdminResult<()> {
    let mut file = File::create(OWNER_OUTPUT).map_err(|_| "owner output receipt create failed")?;
    serde_json::to_writer_pretty(&mut file, receipt)
        .map_err(|_| "owner output receipt serialization failed")?;
    file.write_all(b"\n")
        .map_err(|_| "owner output receipt write failed")?;
    file.sync_all()
        .map_err(|_| "owner output receipt sync failed")
}
