//! Trusted, offline bootstrap for one fresh local-v1 document shard.
//!
//! This command claims a new shard directory exactly once, installs its local
//! mode marker and initial schemas, verifies them through the normal strict
//! open path, and only then publishes the server's trusted local-v1 config.

use std::collections::HashSet;
use std::fs::{self, DirBuilder, File, OpenOptions};
use std::io::{Read, Write};
use std::os::unix::fs::{DirBuilderExt, OpenOptionsExt};
use std::path::{Component, Path, PathBuf};
use std::process::ExitCode;
use std::time::{SystemTime, UNIX_EPOCH};

use clap::Parser;
use serde::{Deserialize, Serialize};
use tb_core::{
    BudgetConfig, CollectionId, DatabaseId, MAX_VALUE_BYTES, MembershipEpoch, MemoryBudget,
    OriginId, ReplicaId, ShardId, TenantId, TransactionId, WorkerId,
};
use tb_document::{DocumentStore, DocumentStoreConfig, LocalSchemaBinding};
use tb_format::{FieldType, SchemaField, encode_catalog_into, encode_schema_into};
use tb_replication::{ConfigurationId, MAX_EVENT_CHANGES, MAX_WAL_GROUP_BYTES, ReplicationLimits};
use tb_storage::{LocalDocumentMode, StorageConfig};

const MAX_SETUP_BYTES: usize = 256 * 1024;
const MAX_LOCAL_V1_CONFIG_BYTES: usize = 256 * 1024;
const MIN_USABLE_MEMORY_BYTES: usize = 64 * 1024 * 1024;
const DEFAULT_RETRY_WINDOW_MS: u64 = 24 * 60 * 60 * 1_000;
const TRANSACTION_EXPIRY_MARGIN_MS: u64 = 1_000;
const SYSTEM_WORKER_ID: WorkerId = WorkerId(0);

#[derive(Parser, Debug)]
#[command(
    name = "terrabase-init",
    about = "Initialize a fresh local-v1 TerraBase shard and its initial schemas",
    version
)]
struct Args {
    /// Strict, bounded JSON v1 file describing the shard and its schemas.
    #[arg(long)]
    input: PathBuf,

    /// Output path for the trusted server local-v1 config; it must not exist.
    #[arg(long)]
    local_v1_config: PathBuf,

    /// New shard directory to claim; it must not exist.
    #[arg(long)]
    data_dir: PathBuf,

    /// Process memory admission ceiling in bytes.
    #[arg(long)]
    memory_limit_bytes: usize,

    /// Memory headroom reserved for runtime and allocator overhead in bytes.
    #[arg(long)]
    memory_headroom_bytes: usize,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SetupFile {
    version: u64,
    tenant: String,
    database: String,
    shard: String,
    collections: Vec<SetupCollection>,
}

struct ValidatedSetup {
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
    collections: Vec<SetupCollection>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SetupCollection {
    collection: String,
    fields: Vec<SetupField>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SetupField {
    field_id: u32,
    parent_field_id: u32,
    value_type: InputFieldType,
    element_type: Option<InputFieldType>,
    required: bool,
    nullable: bool,
    array_element_nullable: bool,
    name: String,
}

#[derive(Clone, Copy, Deserialize)]
enum InputFieldType {
    Null,
    Bool,
    I64,
    Utf8,
    Bytes,
    Map,
    Array,
}

impl InputFieldType {
    const fn format_type(self) -> FieldType {
        match self {
            Self::Null => FieldType::Null,
            Self::Bool => FieldType::Bool,
            Self::I64 => FieldType::I64,
            Self::Utf8 => FieldType::Utf8,
            Self::Bytes => FieldType::Bytes,
            Self::Map => FieldType::Map,
            Self::Array => FieldType::Array,
        }
    }
}

struct PreparedCollection {
    collection: CollectionId,
    schema_bytes: Vec<u8>,
    binding: LocalSchemaBinding,
}

#[derive(Serialize)]
struct LocalV1File {
    version: u8,
    tenant: String,
    database: String,
    shard: String,
    replica: String,
    incarnation: String,
    epoch: String,
    configuration: String,
    collections: Vec<LocalCollectionFile>,
}

#[derive(Serialize)]
struct LocalCollectionFile {
    collection: String,
    generation: String,
    digest: String,
}

struct ResolvedTarget {
    path: PathBuf,
    parent: PathBuf,
}

fn main() -> ExitCode {
    let args = Args::parse();
    match initialize(args) {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            eprintln!("terrabase-init: {message}");
            ExitCode::FAILURE
        }
    }
}

fn initialize(args: Args) -> Result<(), &'static str> {
    let setup = read_setup(&args.input)?;
    let prepared = prepare_collections(setup.collections)?;
    let (data_target, config_target) = resolve_targets(&args.data_dir, &args.local_v1_config)?;
    ensure_config_target_absent(&config_target.path)?;

    let budget = make_memory_budget(args.memory_limit_bytes, args.memory_headroom_bytes)?;
    let identity = make_identity(setup.tenant, setup.database, setup.shard)?;
    let bindings: Vec<_> = prepared.iter().map(|entry| entry.binding).collect();
    let config_bytes = encode_local_config(&identity, &bindings)?;
    let transaction_ids = fresh_transaction_ids(prepared.len())?;

    claim_shard_directory(&data_target)?;

    let document_config = make_document_config(
        &data_target.path,
        setup.tenant,
        setup.database,
        setup.shard,
        identity.origin,
        identity.epoch,
        identity.configuration,
    );
    let mode = LocalDocumentMode::new(
        setup.tenant,
        setup.database,
        setup.shard,
        identity.origin,
        identity.epoch,
        identity.configuration.to_bytes(),
    );

    let mut bootstrap =
        DocumentStore::bootstrap_local_v1(document_config.clone(), budget.clone(), mode)
            .map_err(|_| "local shard bootstrap failed; inspect the claimed data directory")?;
    for (entry, transaction_id) in prepared.iter().zip(transaction_ids) {
        bootstrap
            .provision_schema(transaction_id, entry.collection, 1, 0, &entry.schema_bytes)
            .map_err(|_| "schema provisioning failed; inspect the claimed data directory")?;
    }
    drop(bootstrap);

    let verified = DocumentStore::open_local_v1(document_config, budget, mode, &bindings).map_err(
        |_| "strict local shard verification failed; inspect the claimed data directory",
    )?;

    publish_config(&config_target, &config_bytes)?;
    drop(verified);

    println!(
        "Initialized local-v1 shard at {}",
        data_target.path.display()
    );
    Ok(())
}

struct SetupIdentity {
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
    origin: OriginId,
    epoch: MembershipEpoch,
    configuration: ConfigurationId,
}

fn read_setup(path: &Path) -> Result<ValidatedSetup, &'static str> {
    let metadata = fs::symlink_metadata(path).map_err(|_| "cannot read setup input")?;
    if !metadata.file_type().is_file() {
        return Err("setup input must be a regular file");
    }
    let mut file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK)
        .open(path)
        .map_err(|_| "cannot read setup input")?;
    let opened_metadata = file.metadata().map_err(|_| "cannot read setup input")?;
    if !opened_metadata.is_file() {
        return Err("setup input must be a regular file");
    }
    if opened_metadata.len() > MAX_SETUP_BYTES as u64 {
        return Err("setup input exceeds the size limit");
    }
    let mut bytes = Vec::new();
    Read::by_ref(&mut file)
        .take((MAX_SETUP_BYTES + 1) as u64)
        .read_to_end(&mut bytes)
        .map_err(|_| "cannot read setup input")?;
    if bytes.len() > MAX_SETUP_BYTES {
        return Err("setup input exceeds the size limit");
    }

    let setup: SetupFile = serde_json::from_slice(&bytes).map_err(|_| "invalid setup input")?;
    if setup.version != 1
        || setup.collections.is_empty()
        || setup.collections.len() > MAX_EVENT_CHANGES
    {
        return Err("invalid setup input");
    }
    let tenant = TenantId::parse_canonical(&setup.tenant).map_err(|_| "invalid setup input")?;
    let database =
        DatabaseId::parse_canonical(&setup.database).map_err(|_| "invalid setup input")?;
    let shard = parse_canonical_u64(&setup.shard)
        .map(ShardId::new)
        .ok_or("invalid setup input")?;
    Ok(ValidatedSetup {
        tenant,
        database,
        shard,
        collections: setup.collections,
    })
}

fn parse_canonical_u64(value: &str) -> Option<u64> {
    let parsed = value.parse::<u64>().ok()?;
    (parsed.to_string() == value).then_some(parsed)
}

fn prepare_collections(
    collections: Vec<SetupCollection>,
) -> Result<Vec<PreparedCollection>, &'static str> {
    let mut prepared = Vec::with_capacity(collections.len());
    let mut previous = None;
    for input in collections {
        let collection =
            CollectionId::parse_canonical(&input.collection).map_err(|_| "invalid setup input")?;
        if previous.is_some_and(|prior| prior >= collection) {
            return Err("collections must be sorted and unique");
        }
        previous = Some(collection);

        let fields: Vec<_> = input
            .fields
            .iter()
            .map(|field| SchemaField {
                field_id: field.field_id,
                parent_field_id: field.parent_field_id,
                value_type: field.value_type.format_type(),
                element_type: field.element_type.map(InputFieldType::format_type),
                flags: u8::from(field.required)
                    | (u8::from(field.nullable) << 1)
                    | (u8::from(field.array_element_nullable) << 2),
                name: &field.name,
            })
            .collect();
        let mut schema_bytes = Vec::new();
        encode_schema_into(&mut schema_bytes, &fields).map_err(|_| "invalid schema definition")?;
        let digest = encode_catalog_into(&mut Vec::new(), collection, 1, 0, &schema_bytes)
            .map_err(|_| "invalid schema definition")?;
        prepared.push(PreparedCollection {
            collection,
            schema_bytes,
            binding: LocalSchemaBinding {
                collection,
                generation: 1,
                schema_digest: digest,
            },
        });
    }
    Ok(prepared)
}

fn resolve_targets(
    data_dir: &Path,
    local_v1_config: &Path,
) -> Result<(ResolvedTarget, ResolvedTarget), &'static str> {
    let data_target = resolve_target(data_dir)?;
    let config_target = resolve_target(local_v1_config)?;
    let data_lexical = lexical_absolute(data_dir)?;
    let config_lexical = lexical_absolute(local_v1_config)?;
    if config_lexical.starts_with(&data_lexical)
        || config_target.path.starts_with(&data_target.path)
    {
        return Err("local-v1 config output must be outside the shard directory");
    }
    Ok((data_target, config_target))
}

fn resolve_target(path: &Path) -> Result<ResolvedTarget, &'static str> {
    let name = path.file_name().ok_or("invalid output path")?;
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let physical_parent = fs::canonicalize(parent).map_err(|_| "output path parent must exist")?;
    if !physical_parent.is_dir() {
        return Err("output path parent must be a directory");
    }
    Ok(ResolvedTarget {
        path: physical_parent.join(name),
        parent: physical_parent,
    })
}

fn lexical_absolute(path: &Path) -> Result<PathBuf, &'static str> {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map_err(|_| "cannot resolve output path")?
            .join(path)
    };
    let mut normalized = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::Prefix(prefix) => normalized.push(prefix.as_os_str()),
            Component::RootDir => normalized.push(component.as_os_str()),
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            Component::Normal(name) => normalized.push(name),
        }
    }
    Ok(normalized)
}

fn ensure_config_target_absent(path: &Path) -> Result<(), &'static str> {
    match fs::symlink_metadata(path) {
        Ok(_) => Err("local-v1 config output already exists"),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(_) => Err("cannot inspect local-v1 config output"),
    }
}

fn make_memory_budget(limit: usize, headroom: usize) -> Result<MemoryBudget, &'static str> {
    if headroom == 0 || headroom >= limit || limit - headroom < MIN_USABLE_MEMORY_BYTES {
        return Err("invalid memory limit or headroom");
    }
    let config =
        BudgetConfig::new(limit, headroom).map_err(|_| "invalid memory limit or headroom")?;
    Ok(MemoryBudget::new(config))
}

fn make_identity(
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
) -> Result<SetupIdentity, &'static str> {
    let replica = ReplicaId::new();
    let mut incarnation = [0_u8; 16];
    loop {
        getrandom::fill(&mut incarnation).map_err(|_| "operating-system randomness unavailable")?;
        if incarnation != [0; 16] {
            break;
        }
    }
    let configuration = loop {
        let mut bytes = [0_u8; 32];
        getrandom::fill(&mut bytes).map_err(|_| "operating-system randomness unavailable")?;
        if let Ok(configuration) = ConfigurationId::from_bytes(bytes) {
            break configuration;
        }
    };
    Ok(SetupIdentity {
        tenant,
        database,
        shard,
        origin: OriginId {
            replica_id: replica,
            incarnation,
        },
        epoch: MembershipEpoch::new(1),
        configuration,
    })
}

fn encode_local_config(
    identity: &SetupIdentity,
    bindings: &[LocalSchemaBinding],
) -> Result<Vec<u8>, &'static str> {
    let file = LocalV1File {
        version: 1,
        tenant: identity.tenant.to_string(),
        database: identity.database.to_string(),
        shard: identity.shard.get().to_string(),
        replica: identity.origin.replica_id.to_string(),
        incarnation: lower_hex(&identity.origin.incarnation),
        epoch: identity.epoch.get().to_string(),
        configuration: lower_hex(&identity.configuration.to_bytes()),
        collections: bindings
            .iter()
            .map(|binding| LocalCollectionFile {
                collection: binding.collection.to_string(),
                generation: binding.generation.to_string(),
                digest: lower_hex(&binding.schema_digest),
            })
            .collect(),
    };
    let bytes = serde_json::to_vec(&file).map_err(|_| "cannot encode local-v1 config")?;
    if bytes.len() > MAX_LOCAL_V1_CONFIG_BYTES {
        return Err("local-v1 config exceeds the size limit");
    }
    Ok(bytes)
}

fn lower_hex(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut value = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let byte = *byte;
        value.push(char::from(HEX[usize::from(byte >> 4)]));
        value.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    value
}

fn make_document_config(
    data_dir: &Path,
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
    origin: OriginId,
    epoch: MembershipEpoch,
    configuration: ConfigurationId,
) -> DocumentStoreConfig {
    DocumentStoreConfig {
        storage: StorageConfig::local(data_dir, tenant, SYSTEM_WORKER_ID),
        database,
        shard,
        origin,
        epoch,
        configuration,
        replication: ReplicationLimits {
            max_origins: 64,
            max_changes: MAX_EVENT_CHANGES,
            max_siblings: 64,
            max_value_bytes: MAX_VALUE_BYTES,
            max_payload_bytes: MAX_WAL_GROUP_BYTES,
        },
    }
}

fn claim_shard_directory(target: &ResolvedTarget) -> Result<(), &'static str> {
    let mut builder = DirBuilder::new();
    builder.mode(0o700);
    builder.create(&target.path).map_err(|error| {
        if error.kind() == std::io::ErrorKind::AlreadyExists {
            "shard data directory already exists"
        } else {
            "cannot claim shard data directory"
        }
    })?;
    sync_directory(&target.parent).map_err(|_| "cannot sync shard directory claim")
}

fn fresh_transaction_ids(count: usize) -> Result<Vec<TransactionId>, &'static str> {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| "system clock is before the Unix epoch")?
        .as_millis();
    let now = u64::try_from(now).map_err(|_| "system clock is out of range")?;
    let expiry = now
        .checked_add(DEFAULT_RETRY_WINDOW_MS - TRANSACTION_EXPIRY_MARGIN_MS)
        .ok_or("system clock is out of range")?;
    let mut nonces = HashSet::with_capacity(count);
    let mut transactions = Vec::with_capacity(count);
    while transactions.len() < count {
        let mut nonce = [0_u8; 16];
        getrandom::fill(&mut nonce).map_err(|_| "operating-system randomness unavailable")?;
        if nonce != [0; 16] && nonces.insert(nonce) {
            transactions.push(TransactionId::new(expiry, nonce));
        }
    }
    Ok(transactions)
}

fn publish_config(target: &ResolvedTarget, contents: &[u8]) -> Result<(), &'static str> {
    let mut temp_path = None;
    let mut temp_file = None;
    for _ in 0..8 {
        let mut nonce = [0_u8; 8];
        getrandom::fill(&mut nonce).map_err(|_| "operating-system randomness unavailable")?;
        let candidate = target.parent.join(format!(
            ".terrabase-init-{}-{}.tmp",
            std::process::id(),
            lower_hex(&nonce)
        ));
        match OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .open(&candidate)
        {
            Ok(file) => {
                temp_path = Some(candidate);
                temp_file = Some(file);
                break;
            }
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(_) => return Err("cannot create private local-v1 config temporary file"),
        }
    }
    let temp_path = temp_path.ok_or("cannot create private local-v1 config temporary file")?;
    let mut temp_file = temp_file.ok_or("cannot create private local-v1 config temporary file")?;
    temp_file
        .write_all(contents)
        .map_err(|_| "cannot write local-v1 config temporary file")?;
    temp_file
        .sync_all()
        .map_err(|_| "cannot sync local-v1 config temporary file")?;
    drop(temp_file);

    // A same-directory hard link publishes the complete inode atomically and
    // returns an error instead of replacing an existing destination.
    fs::hard_link(&temp_path, &target.path)
        .map_err(|_| "cannot publish local-v1 config without replacing an existing file")?;
    sync_directory(&target.parent).map_err(|_| "cannot sync published local-v1 config")?;
    fs::remove_file(&temp_path).map_err(|_| "cannot remove local-v1 config temporary file")?;
    sync_directory(&target.parent).map_err(|_| "cannot sync local-v1 config cleanup")?;
    Ok(())
}

fn sync_directory(path: &Path) -> std::io::Result<()> {
    File::open(path)?.sync_all()
}
