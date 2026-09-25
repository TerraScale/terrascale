//! Local immutable-run and manifest I/O. All files are new, synced, and
//! checksummed before a manifest generation references them.

use std::collections::BTreeMap;
use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::os::unix::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::time::Instant;

use sha2::{Digest, Sha256};
use tb_core::{Attribution, CommitSeq, Key, KeyBound, KeyRange, MemoryBudget, Reservation};
use tb_format::{
    BlockDescriptor, FormatLimits, OwnedSegmentBlockCursor, SegmentBlockPosition, SegmentFooter,
    SegmentHeader, SegmentRecord, SegmentValue, SegmentValueRange, decode_segment_block_checked,
    decode_segment_descriptor_entry, decode_segment_directory, decode_segment_footer,
    decode_segment_header, encode_segment_block_into, encode_segment_directory_into,
    encode_segment_footer_into, encode_segment_header_into, encoded_segment_record_len,
    segment_descriptor_entry_len,
};

use crate::StorageError;
use crate::local::Version;
use crate::work::ScanCounters;

const MANIFEST_MAGIC: &[u8; 8] = b"TBMAN001";
const MANIFEST_VERSION: u16 = 1;
const MANIFEST_HEADER: usize = 8 + 2 + 2 + 8 + 8 + 8 + 4;
const RUN_ENTRY: usize = 8 + 8 + 32;
const MANIFEST_MAX_BYTES: usize = 1 << 20;

pub(crate) fn manifest_bytes_for_run_count(count: usize) -> Result<usize, StorageError> {
    MANIFEST_HEADER
        .checked_add(
            count
                .checked_mul(RUN_ENTRY)
                .ok_or(StorageError::Capacity("manifest bytes"))?,
        )
        .and_then(|bytes| bytes.checked_add(4))
        .filter(|bytes| *bytes <= MANIFEST_MAX_BYTES)
        .ok_or(StorageError::Capacity("manifest bytes"))
}

#[derive(Clone, Debug)]
pub(crate) struct RunMeta {
    pub(crate) id: u64,
    pub(crate) bytes: u64,
    pub(crate) sha256: [u8; 32],
}

#[derive(Clone, Debug)]
pub(crate) struct Manifest {
    pub(crate) generation: u64,
    pub(crate) checkpoint: CommitSeq,
    pub(crate) expired_floor_ms: u64,
    pub(crate) runs: Vec<RunMeta>,
}

impl Default for Manifest {
    fn default() -> Self {
        Self {
            generation: 0,
            checkpoint: CommitSeq::ZERO,
            expired_floor_ms: 0,
            runs: Vec::new(),
        }
    }
}

#[derive(Clone, Debug)]
pub(crate) struct RawRecord {
    pub(crate) sequence: CommitSeq,
    pub(crate) value: Option<Vec<u8>>,
}

fn run_path(directory: &Path, id: u64) -> PathBuf {
    directory.join(format!("run-{id:020}.seg"))
}

fn manifest_path(directory: &Path, slot: u64) -> PathBuf {
    directory.join(format!("manifest.{slot}"))
}

pub(crate) fn load_manifest(
    directory: &Path,
    limits: &FormatLimits,
    budget: &MemoryBudget,
    attribution: Attribution,
) -> Result<Manifest, StorageError> {
    let mut selected: Option<Manifest> = None;
    let mut any = false;
    let mut first_error = None;
    for slot in 0..=1 {
        let path = manifest_path(directory, slot);
        if !path.exists() {
            continue;
        }
        any = true;
        let length = usize::try_from(std::fs::metadata(&path)?.len())
            .map_err(|_| StorageError::Capacity("manifest bytes"))?;
        if length > MANIFEST_MAX_BYTES {
            first_error = Some(StorageError::Corrupt("manifest exceeds hard cap".into()));
            continue;
        }
        let _permit = budget.try_reserve(attribution, length)?;
        let mut bytes = vec![0_u8; length];
        File::open(&path)?.read_exact(&mut bytes)?;
        match decode_manifest(&bytes) {
            Ok(candidate) => {
                let validation = candidate
                    .runs
                    .iter()
                    .try_for_each(|run| validate_run(directory, run, limits));
                match validation {
                    Ok(()) => {
                        if selected
                            .as_ref()
                            .is_none_or(|current| candidate.generation > current.generation)
                        {
                            selected = Some(candidate);
                        }
                    }
                    Err(error) => first_error = Some(error),
                }
            }
            Err(error) => first_error = Some(error),
        }
    }
    if let Some(manifest) = selected {
        return Ok(manifest);
    }
    if any {
        return Err(first_error
            .unwrap_or_else(|| StorageError::Corrupt("no valid manifest generation".into())));
    }
    Ok(Manifest::default())
}

fn decode_manifest(bytes: &[u8]) -> Result<Manifest, StorageError> {
    if bytes.len() < MANIFEST_HEADER + 4 || bytes.len() > MANIFEST_MAX_BYTES {
        return Err(StorageError::Corrupt("manifest length".into()));
    }
    if &bytes[..8] != MANIFEST_MAGIC {
        return Err(StorageError::Corrupt("manifest magic".into()));
    }
    let major = u16::from_le_bytes(bytes[8..10].try_into().unwrap());
    let flags = u16::from_le_bytes(bytes[10..12].try_into().unwrap());
    if major != MANIFEST_VERSION || flags != 0 {
        return Err(StorageError::Corrupt("manifest version or flags".into()));
    }
    let expected_crc = u32::from_le_bytes(bytes[bytes.len() - 4..].try_into().unwrap());
    if crc32c::crc32c(&bytes[..bytes.len() - 4]) != expected_crc {
        return Err(StorageError::Corrupt("manifest checksum".into()));
    }
    let generation = u64::from_le_bytes(bytes[12..20].try_into().unwrap());
    let checkpoint = CommitSeq::new(u64::from_le_bytes(bytes[20..28].try_into().unwrap()));
    let expired_floor_ms = u64::from_le_bytes(bytes[28..36].try_into().unwrap());
    let count = u32::from_le_bytes(bytes[36..40].try_into().unwrap()) as usize;
    if count > (MANIFEST_MAX_BYTES - MANIFEST_HEADER - 4) / RUN_ENTRY
        || bytes.len() != MANIFEST_HEADER + count * RUN_ENTRY + 4
    {
        return Err(StorageError::Corrupt("manifest run count".into()));
    }
    let mut runs = Vec::with_capacity(count);
    let mut offset = MANIFEST_HEADER;
    for _ in 0..count {
        let id = u64::from_le_bytes(bytes[offset..offset + 8].try_into().unwrap());
        let size = u64::from_le_bytes(bytes[offset + 8..offset + 16].try_into().unwrap());
        let sha256 = bytes[offset + 16..offset + 48].try_into().unwrap();
        if id == 0 || size == 0 || runs.iter().any(|run: &RunMeta| run.id == id) {
            return Err(StorageError::Corrupt("manifest run identity".into()));
        }
        runs.push(RunMeta {
            id,
            bytes: size,
            sha256,
        });
        offset += RUN_ENTRY;
    }
    Ok(Manifest {
        generation,
        checkpoint,
        expired_floor_ms,
        runs,
    })
}

pub(crate) fn publish_manifest(
    directory: &Path,
    manifest: &Manifest,
    budget: &MemoryBudget,
    attribution: Attribution,
    prepaid: Option<&Reservation>,
) -> Result<(), StorageError> {
    let count = manifest.runs.len();
    let size = manifest_bytes_for_run_count(count)?;
    let _permit = borrowed_permit(budget, attribution, size, prepaid)?;
    let mut bytes = Vec::with_capacity(size);
    bytes.extend_from_slice(MANIFEST_MAGIC);
    bytes.extend_from_slice(&MANIFEST_VERSION.to_le_bytes());
    bytes.extend_from_slice(&0_u16.to_le_bytes());
    bytes.extend_from_slice(&manifest.generation.to_le_bytes());
    bytes.extend_from_slice(&manifest.checkpoint.get().to_le_bytes());
    bytes.extend_from_slice(&manifest.expired_floor_ms.to_le_bytes());
    bytes.extend_from_slice(
        &u32::try_from(count)
            .map_err(|_| StorageError::Capacity("manifest run count"))?
            .to_le_bytes(),
    );
    for run in &manifest.runs {
        bytes.extend_from_slice(&run.id.to_le_bytes());
        bytes.extend_from_slice(&run.bytes.to_le_bytes());
        bytes.extend_from_slice(&run.sha256);
    }
    let crc = crc32c::crc32c(&bytes);
    bytes.extend_from_slice(&crc.to_le_bytes());
    let target = manifest_path(directory, manifest.generation % 2);
    let temp = directory.join("manifest.pending");
    let mut file = OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .open(&temp)?;
    file.write_all(&bytes)?;
    file.sync_all()?;
    std::fs::rename(&temp, target)?;
    File::open(directory)?.sync_all()?;
    Ok(())
}

fn borrowed_permit(
    budget: &MemoryBudget,
    attribution: Attribution,
    bytes: usize,
    prepaid: Option<&Reservation>,
) -> Result<Option<Reservation>, StorageError> {
    if let Some(prepaid) = prepaid {
        if prepaid.attribution() != attribution || prepaid.bytes() < bytes {
            return Err(StorageError::Capacity("prepaid I/O memory"));
        }
        Ok(None)
    } else {
        Ok(Some(budget.try_reserve(attribution, bytes)?))
    }
}

pub(crate) fn validate_run(
    directory: &Path,
    run: &RunMeta,
    _limits: &FormatLimits,
) -> Result<(), StorageError> {
    let path = run_path(directory, run.id);
    let mut file = File::open(path)?;
    if file.metadata()?.len() != run.bytes {
        return Err(StorageError::Corrupt("immutable run length".into()));
    }
    let mut digest = Sha256::new();
    let mut block = [0_u8; 8 * 1024];
    loop {
        let count = file.read(&mut block)?;
        if count == 0 {
            break;
        }
        digest.update(&block[..count]);
    }
    let computed: [u8; 32] = digest.finalize().into();
    if computed != run.sha256 {
        return Err(StorageError::Corrupt("immutable run digest".into()));
    }
    Ok(())
}

/// Remove unfinished temporary writes. Completed runs remain until a later
/// collector proves that no valid manifest slot can still need them.
pub(crate) fn cleanup_orphans(directory: &Path) -> Result<(), StorageError> {
    let mut changed = false;
    for entry in std::fs::read_dir(directory)? {
        let entry = entry?;
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if name == "manifest.pending" {
            std::fs::remove_file(entry.path())?;
            changed = true;
            continue;
        }
        if !name.starts_with("run-") {
            continue;
        }
        if name.ends_with(".pending") {
            std::fs::remove_file(entry.path())?;
            changed = true;
        }
    }
    if changed {
        File::open(directory)?.sync_all()?;
    }
    Ok(())
}

#[derive(Clone)]
struct OwnedDescriptor {
    min_key: Vec<u8>,
    max_key: Vec<u8>,
    block_offset: u64,
    total_block_len: u32,
    block_crc32c: u32,
}

/// A bounded, monotone iterator over one validated immutable run.
pub(crate) struct ScanCursor {
    file: Option<File>,
    path: PathBuf,
    identity: RunFileIdentity,
    directory_next: u64,
    directory_end: u64,
    descriptors_left: usize,
    descriptor_index: usize,
    descriptor: Option<OwnedDescriptor>,
    block: Option<OwnedSegmentBlockCursor>,
    block_permit: Option<tb_core::Reservation>,
    position: Option<SegmentBlockPosition>,
    highest_loaded_descriptor: Option<usize>,
    pub(crate) current: Option<ScanCandidate>,
    d02_block_load_ns: Option<u64>,
}

#[derive(Clone, Copy, Eq, PartialEq)]
struct RunFileIdentity {
    device: u64,
    inode: u64,
    bytes: u64,
    modified_seconds: i64,
    modified_nanoseconds: i64,
    changed_seconds: i64,
    changed_nanoseconds: i64,
}

impl RunFileIdentity {
    fn of(file: &File) -> Result<Self, StorageError> {
        let metadata = file.metadata()?;
        Ok(Self {
            device: metadata.dev(),
            inode: metadata.ino(),
            bytes: metadata.len(),
            modified_seconds: metadata.mtime(),
            modified_nanoseconds: metadata.mtime_nsec(),
            changed_seconds: metadata.ctime(),
            changed_nanoseconds: metadata.ctime_nsec(),
        })
    }
}

#[derive(Clone)]
pub(crate) struct ScanCandidate {
    pub(crate) key: Key,
    pub(crate) sequence: CommitSeq,
    pub(crate) value_range: Option<SegmentValueRange>,
}

impl ScanCursor {
    pub(crate) fn open(
        directory: &Path,
        run: &RunMeta,
        read: &ReadContext<'_>,
    ) -> Result<Self, StorageError> {
        let mut start = 0;
        let mut end = 0;
        let mut count = 0;
        let (file, identity) = with_run_directory(
            directory,
            run,
            read.limits,
            read.budget,
            read.attribution,
            read.work,
            read.prepaid,
            |file, parsed, header| {
                start = header.directory_offset + 12;
                end = header.directory_offset + header.directory_len - 4;
                count = parsed.descriptor_count();
                Ok((file.try_clone()?, RunFileIdentity::of(file)?))
            },
        )?;
        Ok(Self {
            file: Some(file),
            path: run_path(directory, run.id),
            identity,
            directory_next: start,
            directory_end: end,
            descriptors_left: count,
            descriptor_index: 0,
            descriptor: None,
            block: None,
            block_permit: None,
            position: None,
            highest_loaded_descriptor: None,
            current: None,
            d02_block_load_ns: None,
        })
    }

    pub(crate) fn enable_d02_block_timing(&mut self) {
        self.d02_block_load_ns = Some(0);
    }

    pub(crate) fn d02_block_load_ns(&self) -> u64 {
        self.d02_block_load_ns.unwrap_or(0)
    }

    pub(crate) fn has_file(&self) -> bool {
        self.file.is_some()
    }

    pub(crate) fn evict_file(&mut self) {
        self.file = None;
    }

    fn ensure_file(&mut self, read: &ReadContext<'_>) -> Result<(), StorageError> {
        if self.file.is_none() {
            let file = File::open(&self.path)?;
            read.work.run_open();
            read.work.run_metadata();
            if RunFileIdentity::of(&file)? != self.identity {
                return Err(StorageError::Corrupt(
                    "immutable run identity changed".into(),
                ));
            }
            self.file = Some(file);
        }
        Ok(())
    }

    pub(crate) fn cached_bytes(&self) -> usize {
        self.descriptor
            .as_ref()
            .filter(|_| self.block.is_some())
            .map_or(0, |d| d.total_block_len as usize)
    }

    pub(crate) fn evict_block(&mut self, work: &ScanCounters) {
        if let Some(block) = self.block.take() {
            self.position = Some(block.position());
            self.block_permit = None;
            work.block_cache_eviction();
        }
    }

    pub(crate) fn value(
        &mut self,
        range: SegmentValueRange,
        read: &ReadContext<'_>,
    ) -> Result<Vec<u8>, StorageError> {
        if self.block.is_none() {
            self.load_block(read)?;
        }
        self.block
            .as_ref()
            .and_then(|block| block.value_bytes(range))
            .map(<[u8]>::to_vec)
            .ok_or_else(|| StorageError::Corrupt("scan value no longer in checked block".into()))
    }

    fn next_descriptor(
        &mut self,
        read: &ReadContext<'_>,
        scratch: &mut Vec<u8>,
    ) -> Result<bool, StorageError> {
        if self.descriptors_left == 0 {
            if self.directory_next != self.directory_end {
                return Err(StorageError::Corrupt("run directory cursor length".into()));
            }
            return Ok(false);
        }
        self.ensure_file(read)?;
        let file = self.file.as_mut().expect("ensured run file");
        file.seek(SeekFrom::Start(self.directory_next))?;
        let mut prefix = [0_u8; 8];
        file.read_exact(&mut prefix)?;
        read.work.directory_bytes(prefix.len());
        let len = segment_descriptor_entry_len(&prefix, read.limits)?;
        let next = self
            .directory_next
            .checked_add(len as u64)
            .ok_or_else(|| StorageError::Corrupt("run directory cursor overflow".into()))?;
        if next > self.directory_end {
            return Err(StorageError::Corrupt(
                "run directory cursor outside verified bytes".into(),
            ));
        }
        scratch.resize(len, 0);
        scratch[..8].copy_from_slice(&prefix);
        file.read_exact(&mut scratch[8..])?;
        read.work.directory_bytes(len - 8);
        let entry = decode_segment_descriptor_entry(scratch, read.limits)?;
        self.descriptor = Some(OwnedDescriptor {
            min_key: entry.min_key.to_vec(),
            max_key: entry.max_key.to_vec(),
            block_offset: entry.block_offset,
            total_block_len: entry.total_block_len,
            block_crc32c: entry.block_crc32c,
        });
        self.directory_next = next;
        self.descriptors_left -= 1;
        self.descriptor_index += 1;
        read.work.next_descriptor();
        self.position = None;
        Ok(true)
    }

    fn load_block(&mut self, read: &ReadContext<'_>) -> Result<(), StorageError> {
        let started = self.d02_block_load_ns.map(|_| Instant::now());
        self.ensure_file(read)?;
        let descriptor = self
            .descriptor
            .as_ref()
            .ok_or_else(|| StorageError::Corrupt("scan block without descriptor".into()))?;
        let len = descriptor.total_block_len as usize;
        let permit = read.budget.try_reserve(read.attribution, len)?;
        let mut bytes = vec![0_u8; len];
        let file = self.file.as_mut().expect("ensured run file");
        file.seek(SeekFrom::Start(descriptor.block_offset))?;
        file.read_exact(&mut bytes)?;
        read.work.block_bytes(bytes.len());
        let borrowed = descriptor.borrowed();
        let block = if let Some(position) = self.position {
            OwnedSegmentBlockCursor::resume(bytes, &borrowed, read.limits, position)?
        } else {
            OwnedSegmentBlockCursor::decode(bytes, &borrowed, read.limits)?
        };
        if self
            .highest_loaded_descriptor
            .is_some_and(|index| index >= self.descriptor_index)
        {
            read.work.block_reread();
        }
        self.highest_loaded_descriptor = Some(self.descriptor_index);
        self.block = Some(block);
        self.block_permit = Some(permit);
        if let (Some(total), Some(started)) = (&mut self.d02_block_load_ns, started) {
            *total = total
                .saturating_add(u64::try_from(started.elapsed().as_nanos()).unwrap_or(u64::MAX));
        }
        Ok(())
    }

    pub(crate) fn advance(
        &mut self,
        read: &ReadContext<'_>,
        scratch: &mut Vec<u8>,
        range: &KeyRange,
    ) -> Result<(), StorageError> {
        self.current = None;
        loop {
            if self.descriptor.is_none() && !self.next_descriptor(read, scratch)? {
                return Ok(());
            }
            if self.block.is_none() {
                self.load_block(read)?;
            }
            let block = self
                .block
                .as_mut()
                .ok_or_else(|| StorageError::Corrupt("missing scan block".into()))?;
            if let Some(record) = block.next_record()? {
                read.work.next_record();
                if !within_range(record.record.key, range) {
                    if match range.end() {
                        KeyBound::Unbounded => false,
                        KeyBound::Included(end) => record.record.key > end.as_bytes(),
                        KeyBound::Excluded(end) => record.record.key >= end.as_bytes(),
                    } {
                        return Ok(());
                    }
                    continue;
                }
                self.current = Some(ScanCandidate {
                    key: Key::new(record.record.key.to_vec())?,
                    sequence: CommitSeq::new(record.record.sequence),
                    value_range: record.value_range,
                });
                self.position = Some(block.position());
                return Ok(());
            }
            self.block = None;
            self.block_permit = None;
            self.descriptor = None;
            self.position = None;
        }
    }
}

impl OwnedDescriptor {
    fn borrowed(&self) -> BlockDescriptor<'_> {
        BlockDescriptor {
            min_key: &self.min_key,
            max_key: &self.max_key,
            block_offset: self.block_offset,
            total_block_len: self.total_block_len,
            block_crc32c: self.block_crc32c,
        }
    }
}

fn append_block(
    file: &mut File,
    records: &mut Vec<SegmentRecord<'_>>,
    descriptors: &mut Vec<OwnedDescriptor>,
    limits: &FormatLimits,
    block: &mut Vec<u8>,
) -> Result<(), StorageError> {
    if records.is_empty() {
        return Ok(());
    }
    let offset = file.stream_position()?;
    let checksum = encode_segment_block_into(block, records, limits)?;
    file.write_all(block)?;
    let first = records
        .first()
        .ok_or(StorageError::Corrupt("empty block".into()))?;
    let last = records
        .last()
        .ok_or(StorageError::Corrupt("empty block".into()))?;
    descriptors.push(OwnedDescriptor {
        min_key: first.key.to_vec(),
        max_key: last.key.to_vec(),
        block_offset: offset,
        total_block_len: u32::try_from(block.len())
            .map_err(|_| StorageError::Capacity("run block bytes"))?,
        block_crc32c: checksum,
    });
    records.clear();
    Ok(())
}

pub(crate) fn run_build_reservation_bytes(limits: &FormatLimits) -> Result<usize, StorageError> {
    let directory_bytes = limits
        .max_directory_bytes
        .checked_mul(3)
        .ok_or(StorageError::Capacity("run build memory"))?;
    limits
        .max_block_stored_bytes
        .checked_add(directory_bytes)
        .and_then(|bytes| bytes.checked_add(64 * 1024))
        .ok_or(StorageError::Capacity("run build memory"))
}

pub(crate) fn write_run(
    directory: &Path,
    id: u64,
    mutable: &BTreeMap<Key, Vec<Version>>,
    limits: &FormatLimits,
    budget: &MemoryBudget,
    attribution: Attribution,
    prepaid: Option<&Reservation>,
) -> Result<RunMeta, StorageError> {
    if mutable.is_empty() {
        return Err(StorageError::Corrupt(
            "attempted empty immutable run".into(),
        ));
    }
    let reserve_bytes = run_build_reservation_bytes(limits)?;
    let _permit = borrowed_permit(budget, attribution, reserve_bytes, prepaid)?;
    let temp = directory.join(format!("run-{id:020}.pending"));
    let final_path = run_path(directory, id);
    if final_path.exists() {
        // The caller only supplies an unpublished next-generation identity.
        // A preexisting file is an orphan from a failed earlier attempt.
        std::fs::remove_file(&final_path)?;
        File::open(directory)?.sync_all()?;
    }
    if temp.exists() {
        std::fs::remove_file(&temp)?;
    }
    let mut file = OpenOptions::new()
        .create_new(true)
        .read(true)
        .write(true)
        .open(&temp)?;
    file.write_all(&[0_u8; 36])?;
    let mut descriptors = Vec::new();
    let mut records = Vec::new();
    let mut block = Vec::new();
    let mut block_plain_bytes = 0_usize;
    for (key, versions) in mutable {
        for version in versions {
            let record = SegmentRecord {
                key: key.as_bytes(),
                sequence: version.sequence.get(),
                value: version
                    .value
                    .as_deref()
                    .map_or(SegmentValue::Tombstone, SegmentValue::Put),
            };
            let encoded_len = encoded_segment_record_len(record, limits)?;
            if !records.is_empty()
                && (block_plain_bytes + encoded_len > limits.max_block_plain_bytes
                    || records.len() >= limits.max_records_per_block)
            {
                append_block(
                    &mut file,
                    &mut records,
                    &mut descriptors,
                    limits,
                    &mut block,
                )?;
                block_plain_bytes = 0;
            }
            if encoded_len > limits.max_block_plain_bytes {
                return Err(StorageError::Capacity("single run record exceeds block"));
            }
            records.push(record);
            block_plain_bytes += encoded_len;
            if descriptors.len() > limits.max_segment_blocks {
                return Err(StorageError::Capacity("run block count"));
            }
        }
    }
    append_block(
        &mut file,
        &mut records,
        &mut descriptors,
        limits,
        &mut block,
    )?;
    if descriptors.len() > limits.max_segment_blocks {
        return Err(StorageError::Capacity("run block count"));
    }
    seal_run(
        file,
        directory,
        &temp,
        &final_path,
        id,
        &descriptors,
        limits,
    )
}

fn seal_run(
    mut file: File,
    directory: &Path,
    temp: &Path,
    final_path: &Path,
    id: u64,
    descriptors: &[OwnedDescriptor],
    limits: &FormatLimits,
) -> Result<RunMeta, StorageError> {
    let directory_offset = file.stream_position()?;
    let borrowed: Vec<_> = descriptors.iter().map(OwnedDescriptor::borrowed).collect();
    let mut directory_bytes = Vec::new();
    let directory_crc32c = encode_segment_directory_into(&mut directory_bytes, &borrowed, limits)?;
    file.write_all(&directory_bytes)?;
    let run_bytes = file.stream_position()? + 24;
    let mut footer = Vec::new();
    encode_segment_footer_into(
        &mut footer,
        SegmentFooter {
            run_bytes,
            directory_crc32c,
        },
    )?;
    file.write_all(&footer)?;
    let mut header = Vec::new();
    encode_segment_header_into(
        &mut header,
        SegmentHeader {
            block_count: u32::try_from(descriptors.len())
                .map_err(|_| StorageError::Capacity("run block count"))?,
            directory_offset,
            directory_len: directory_bytes.len() as u64,
        },
        limits,
    )?;
    file.seek(SeekFrom::Start(0))?;
    file.write_all(&header)?;
    sync_and_publish_run(file, directory, temp, final_path, id, run_bytes)
}

fn sync_and_publish_run(
    mut file: File,
    directory: &Path,
    temp: &Path,
    final_path: &Path,
    id: u64,
    run_bytes: u64,
) -> Result<RunMeta, StorageError> {
    file.sync_all()?;
    file.seek(SeekFrom::Start(0))?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 8 * 1024];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }
    let sha256 = digest.finalize().into();
    std::fs::rename(temp, final_path)?;
    File::open(directory)?.sync_all()?;
    crate::crash_at("after_run_sync_before_manifest");
    Ok(RunMeta {
        id,
        bytes: run_bytes,
        sha256,
    })
}

pub(crate) fn remove_unpublished_run(directory: &Path, run: &RunMeta) -> Result<(), StorageError> {
    std::fs::remove_file(run_path(directory, run.id))?;
    File::open(directory)?.sync_all()?;
    Ok(())
}

#[allow(
    clippy::too_many_arguments,
    reason = "run I/O carries the checked read budget and optional prepaid permit"
)]
fn with_run_directory<T>(
    directory: &Path,
    run: &RunMeta,
    limits: &FormatLimits,
    budget: &MemoryBudget,
    attribution: Attribution,
    work: &ScanCounters,
    prepaid: Option<&Reservation>,
    action: impl FnOnce(
        &mut File,
        &tb_format::SegmentDirectory<'_>,
        SegmentHeader,
    ) -> Result<T, StorageError>,
) -> Result<T, StorageError> {
    let mut file = File::open(run_path(directory, run.id))?;
    work.run_open();
    let length = file.metadata()?.len();
    work.run_metadata();
    if length != run.bytes {
        return Err(StorageError::Corrupt("run length changed".into()));
    }
    let mut header_bytes = [0_u8; 36];
    file.read_exact(&mut header_bytes)?;
    work.header_bytes(header_bytes.len());
    let header = decode_segment_header(&header_bytes, limits)?;
    let end = header
        .directory_offset
        .checked_add(header.directory_len)
        .and_then(|offset| offset.checked_add(24))
        .ok_or_else(|| StorageError::Corrupt("run offsets overflow".into()))?;
    if end != run.bytes {
        return Err(StorageError::Corrupt("run directory offset".into()));
    }
    file.seek(SeekFrom::End(-24))?;
    let mut footer_bytes = [0_u8; 24];
    file.read_exact(&mut footer_bytes)?;
    work.footer_bytes(footer_bytes.len());
    let footer = decode_segment_footer(&footer_bytes)?;
    if footer.run_bytes != run.bytes {
        return Err(StorageError::Corrupt("run footer length".into()));
    }
    let directory_len = usize::try_from(header.directory_len)
        .map_err(|_| StorageError::Capacity("run directory bytes"))?;
    if directory_len > limits.max_directory_bytes {
        return Err(StorageError::Capacity("run directory bytes"));
    }
    let _directory_permit = borrowed_permit(budget, attribution, directory_len, prepaid)?;
    let mut directory_bytes = vec![0_u8; directory_len];
    file.seek(SeekFrom::Start(header.directory_offset))?;
    file.read_exact(&mut directory_bytes)?;
    work.directory_bytes(directory_bytes.len());
    let parsed = decode_segment_directory(&directory_bytes, limits)?;
    if parsed.descriptor_count() != header.block_count as usize
        || parsed.checksum() != footer.directory_crc32c
    {
        return Err(StorageError::Corrupt("run directory count or CRC".into()));
    }
    for descriptor in parsed.descriptors() {
        let descriptor = descriptor?;
        if descriptor.block_offset < 36
            || descriptor
                .block_offset
                .saturating_add(u64::from(descriptor.total_block_len))
                > header.directory_offset
        {
            return Err(StorageError::Corrupt(
                "run block outside data section".into(),
            ));
        }
    }
    action(&mut file, &parsed, header)
}

#[allow(
    clippy::too_many_arguments,
    reason = "block I/O carries the checked read budget and optional prepaid permit"
)]
fn read_block<T>(
    file: &mut File,
    descriptor: &BlockDescriptor<'_>,
    limits: &FormatLimits,
    budget: &MemoryBudget,
    attribution: Attribution,
    work: &ScanCounters,
    prepaid: Option<&Reservation>,
    action: impl FnOnce(&tb_format::SegmentBlock<'_>) -> Result<T, StorageError>,
) -> Result<T, StorageError> {
    let len = descriptor.total_block_len as usize;
    let _permit = borrowed_permit(budget, attribution, len, prepaid)?;
    let mut bytes = vec![0_u8; len];
    file.seek(SeekFrom::Start(descriptor.block_offset))?;
    file.read_exact(&mut bytes)?;
    work.block_bytes(bytes.len());
    let block = decode_segment_block_checked(&bytes, descriptor, limits)?;
    action(&block)
}

#[derive(Clone, Copy)]
pub(crate) struct ReadContext<'a> {
    pub(crate) limits: &'a FormatLimits,
    pub(crate) budget: &'a MemoryBudget,
    pub(crate) attribution: Attribution,
    pub(crate) work: &'a ScanCounters,
    pub(crate) prepaid: Option<&'a Reservation>,
}

pub(crate) fn point(
    directory: &Path,
    run: &RunMeta,
    key: &Key,
    snapshot: CommitSeq,
    read: &ReadContext<'_>,
) -> Result<Option<RawRecord>, StorageError> {
    let ReadContext {
        limits,
        budget,
        attribution,
        work,
        prepaid,
    } = *read;
    with_run_directory(
        directory,
        run,
        limits,
        budget,
        attribution,
        work,
        prepaid,
        |file, directory, _header| {
            let mut best: Option<RawRecord> = None;
            for descriptor in directory.descriptors() {
                let descriptor = descriptor?;
                work.point_descriptor();
                if key.as_bytes() < descriptor.min_key || key.as_bytes() > descriptor.max_key {
                    continue;
                }
                read_block(
                    file,
                    &descriptor,
                    limits,
                    budget,
                    attribution,
                    work,
                    prepaid,
                    |block| {
                        for record in block.records() {
                            let record = record?;
                            work.point_record();
                            if record.key != key.as_bytes() || record.sequence > snapshot.get() {
                                continue;
                            }
                            let sequence = CommitSeq::new(record.sequence);
                            let value = match record.value {
                                SegmentValue::Put(value) => Some(value),
                                SegmentValue::Tombstone => None,
                            };
                            if best.as_ref().is_some_and(|current| {
                                sequence == current.sequence && value != current.value.as_deref()
                            }) {
                                return Err(StorageError::Corrupt(
                                    "different values at one key and commit sequence".into(),
                                ));
                            }
                            if best
                                .as_ref()
                                .is_none_or(|current| sequence > current.sequence)
                            {
                                best = Some(RawRecord {
                                    sequence,
                                    value: value.map(<[u8]>::to_vec),
                                });
                            }
                        }
                        Ok(())
                    },
                )?;
            }
            Ok(best)
        },
    )
}

fn within_range(key: &[u8], range: &KeyRange) -> bool {
    let after_start = match range.start() {
        KeyBound::Unbounded => true,
        KeyBound::Included(start) => key >= start.as_bytes(),
        KeyBound::Excluded(start) => key > start.as_bytes(),
    };
    let before_end = match range.end() {
        KeyBound::Unbounded => true,
        KeyBound::Included(end) => key <= end.as_bytes(),
        KeyBound::Excluded(end) => key < end.as_bytes(),
    };
    after_start && before_end
}
