use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{anyhow, bail, Context, Result};
use roaring::RoaringBitmap;
use serde::{Deserialize, Deserializer, Serialize, Serializer};

pub const RETAINED_GENERATIONS: usize = 8;
pub const BUCKET_GENERATIONS: u64 = 8;
pub const HORIZON_BUCKETS: usize = 8;
pub const CHECKPOINT_FRAMES: u64 = 1024;
const MANIFEST_FORMAT: u32 = 1;
const MANIFEST_FILE: &str = "sync-manifest";
const REVISION_PREFIX: &str = "ts1";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Revision {
    pub incarnation: String,
    pub generation: u64,
    pub frame: u64,
}

impl Revision {
    pub fn parse(text: &str) -> Option<Self> {
        let mut parts = text.split(':');
        if parts.next()? != REVISION_PREFIX {
            return None;
        }
        let incarnation = parts.next()?;
        let generation = parts.next()?.parse().ok()?;
        let frame = parts.next()?.parse().ok()?;
        if parts.next().is_some() || !is_ulid(incarnation) {
            return None;
        }
        Some(Self {
            incarnation: incarnation.to_string(),
            generation,
            frame,
        })
    }

    pub fn encode(&self) -> String {
        format!(
            "{REVISION_PREFIX}:{}:{}:{}",
            self.incarnation, self.generation, self.frame
        )
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Sealed {
    pub generation: u64,
    pub end_frame: u64,
    pub full: bool,
    pub pages: Vec<(u32, u64)>,
}

impl Sealed {
    fn pages_after(&self, frame: u64) -> impl Iterator<Item = u32> + '_ {
        self.pages
            .iter()
            .filter(move |(_, last)| *last > frame)
            .map(|(page, _)| *page)
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Bucket {
    pub first_generation: u64,
    pub last_generation: u64,
    #[serde(serialize_with = "bitmap_to_hex", deserialize_with = "bitmap_from_hex")]
    pub pages: RoaringBitmap,
}

fn bitmap_to_hex<S: Serializer>(pages: &RoaringBitmap, s: S) -> Result<S::Ok, S::Error> {
    let mut bytes = Vec::with_capacity(pages.serialized_size());
    pages
        .serialize_into(&mut bytes)
        .map_err(serde::ser::Error::custom)?;
    s.serialize_str(&bytes.iter().map(|b| format!("{b:02x}")).collect::<String>())
}

fn bitmap_from_hex<'de, D: Deserializer<'de>>(d: D) -> Result<RoaringBitmap, D::Error> {
    let text = String::deserialize(d)?;
    let bytes = (0..text.len())
        .step_by(2)
        .map(|i| text.get(i..i + 2).and_then(|h| u8::from_str_radix(h, 16).ok()))
        .collect::<Option<Vec<u8>>>()
        .ok_or_else(|| serde::de::Error::custom("invalid hex page set"))?;
    RoaringBitmap::deserialize_from(&bytes[..]).map_err(serde::de::Error::custom)
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Manifest {
    pub format: u32,
    pub incarnation: String,
    pub generation: u64,
    pub wal_salt: Option<(u32, u32)>,
    pub sealed: Vec<Sealed>,
    pub horizon: Vec<Bucket>,
}

impl Manifest {
    pub fn new_incarnation() -> Self {
        Self {
            format: MANIFEST_FORMAT,
            incarnation: new_ulid(),
            generation: 1,
            wal_salt: None,
            sealed: Vec::new(),
            horizon: Vec::new(),
        }
    }

    pub fn seal(&mut self, live: &Live) {
        self.sealed.push(Sealed {
            generation: self.generation,
            end_frame: live.max_frame,
            full: live.full,
            pages: live.pages.iter().map(|(p, f)| (*p, *f)).collect(),
        });
        self.generation += 1;
        self.wal_salt = None;
        while self.sealed.len() > RETAINED_GENERATIONS {
            let oldest = self.sealed.remove(0);
            if oldest.full {
                self.horizon.clear();
                continue;
            }
            let pages = oldest.pages.iter().map(|(page, _)| *page);
            match self.horizon.last_mut() {
                Some(bucket)
                    if bucket.last_generation - bucket.first_generation + 1 < BUCKET_GENERATIONS =>
                {
                    bucket.pages.extend(pages);
                    bucket.last_generation = oldest.generation;
                }
                _ => self.horizon.push(Bucket {
                    first_generation: oldest.generation,
                    last_generation: oldest.generation,
                    pages: pages.collect(),
                }),
            }
            let excess = self.horizon.len().saturating_sub(HORIZON_BUCKETS);
            self.horizon.drain(..excess);
        }
    }

    pub fn seal_full(&mut self) {
        self.seal(&Live {
            full: true,
            ..Live::default()
        });
    }

    pub fn current(&self, frame: u64) -> Revision {
        Revision {
            incarnation: self.incarnation.clone(),
            generation: self.generation,
            frame,
        }
    }

    pub fn path(dir: &Path) -> PathBuf {
        dir.join(MANIFEST_FILE)
    }

    pub fn load(dir: &Path) -> Result<Option<Self>> {
        let path = Self::path(dir);
        let mut text = String::new();
        match File::open(&path) {
            Ok(mut file) => file.read_to_string(&mut text)?,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(e) => return Err(e.into()),
        };
        let (crc, body) = text
            .split_once('\n')
            .ok_or_else(|| anyhow!("manifest has no checksum line"))?;
        let expected = u32::from_str_radix(crc, 16).context("manifest checksum")?;
        if crc32c::crc32c(body.as_bytes()) != expected {
            bail!("manifest checksum mismatch");
        }
        let manifest: Self = serde_json::from_str(body)?;
        if manifest.format != MANIFEST_FORMAT || !is_ulid(&manifest.incarnation) {
            bail!("manifest format {} is not supported", manifest.format);
        }
        Ok(Some(manifest))
    }

    pub fn store(&self, dir: &Path) -> Result<u64> {
        let body = serde_json::to_string(self)?;
        let text = format!("{:08x}\n{body}", crc32c::crc32c(body.as_bytes()));
        let tmp = dir.join(format!("{MANIFEST_FILE}.tmp"));
        let mut file = OpenOptions::new()
            .create(true)
            .write(true)
            .truncate(true)
            .open(&tmp)?;
        file.write_all(text.as_bytes())?;
        file.sync_all()?;
        fs::rename(&tmp, Self::path(dir))?;
        sync_dir(dir)?;
        Ok(text.len() as u64)
    }
}

#[derive(Debug, Clone, Default)]
pub struct Live {
    pub pages: BTreeMap<u32, u64>,
    pub max_frame: u64,
    pub checkpoint_seq: u32,
    pub full: bool,
}

#[derive(Debug, PartialEq, Eq)]
pub enum Plan {
    Full(Reason),
    Pages(BTreeSet<u32>),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Reason {
    Bootstrap,
    Unrecognized,
    OtherIncarnation,
    Ahead,
    Expired,
    Rewritten,
}

impl Reason {
    pub fn as_str(self) -> &'static str {
        match self {
            Reason::Bootstrap => "bootstrap",
            Reason::Unrecognized => "unrecognized",
            Reason::OtherIncarnation => "other-incarnation",
            Reason::Ahead => "ahead",
            Reason::Expired => "expired",
            Reason::Rewritten => "rewritten",
        }
    }
}

pub fn plan(manifest: &Manifest, live: &Live, client: &str) -> Plan {
    if client.is_empty() {
        return Plan::Full(Reason::Bootstrap);
    }
    let Some(rev) = Revision::parse(client) else {
        return Plan::Full(Reason::Unrecognized);
    };
    if rev.incarnation != manifest.incarnation {
        return Plan::Full(Reason::OtherIncarnation);
    }
    if rev.generation > manifest.generation {
        return Plan::Full(Reason::Ahead);
    }
    let mut pages = BTreeSet::new();
    if rev.generation < manifest.generation {
        let (start, mut floor, mut expected) =
            match manifest.sealed.iter().position(|s| s.generation == rev.generation) {
                Some(start) => (start, rev.frame, rev.generation),
                None => {
                    let Some(k) = manifest.horizon.iter().position(|b| {
                        b.first_generation <= rev.generation && rev.generation <= b.last_generation
                    }) else {
                        return Plan::Full(Reason::Expired);
                    };
                    let mut next = manifest.horizon[k].first_generation;
                    for bucket in &manifest.horizon[k..] {
                        if bucket.first_generation != next {
                            return Plan::Full(Reason::Expired);
                        }
                        pages.extend(bucket.pages.iter());
                        next = bucket.last_generation + 1;
                    }
                    (0, 0, next)
                }
            };
        for sealed in &manifest.sealed[start..] {
            if sealed.generation != expected {
                return Plan::Full(Reason::Expired);
            }
            if sealed.full {
                return Plan::Full(Reason::Rewritten);
            }
            if floor > sealed.end_frame {
                return Plan::Full(Reason::Ahead);
            }
            pages.extend(sealed.pages_after(floor));
            floor = 0;
            expected += 1;
        }
        if manifest.sealed.last().map(|s| s.generation + 1) != Some(manifest.generation) {
            return Plan::Full(Reason::Expired);
        }
    } else if rev.frame > live.max_frame {
        return Plan::Full(Reason::Ahead);
    }
    if live.full {
        return Plan::Full(Reason::Rewritten);
    }
    let floor = if rev.generation == manifest.generation {
        rev.frame
    } else {
        0
    };
    pages.extend(
        live.pages
            .iter()
            .filter(|(_, last)| **last > floor)
            .map(|(page, _)| *page),
    );
    Plan::Pages(pages)
}

pub fn read_wal_salt(wal: &Path) -> Result<Option<(u32, u32)>> {
    let mut header = [0u8; 32];
    match File::open(wal) {
        Ok(mut file) => match file.read_exact(&mut header) {
            Ok(()) => {}
            Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
            Err(e) => return Err(e.into()),
        },
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.into()),
    }
    let word = |at: usize| u32::from_be_bytes(header[at..at + 4].try_into().unwrap());
    if word(0) & 0xFFFF_FFFE != 0x377F_0682 {
        return Ok(None);
    }
    Ok(Some((word(16), word(20))))
}

pub fn sync_dir(dir: &Path) -> Result<()> {
    File::open(dir)?.sync_all()?;
    Ok(())
}

pub fn new_ulid() -> String {
    const ALPHABET: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("clock after 1970")
        .as_millis()
        & ((1u128 << 48) - 1);
    let value = (millis << 80) | (rand::random::<u128>() & ((1u128 << 80) - 1));
    (0..26)
        .map(|i| ALPHABET[((value >> (125 - 5 * i)) & 31) as usize] as char)
        .collect()
}

fn is_ulid(text: &str) -> bool {
    text.len() == 26
        && text.as_bytes()[0] <= b'7'
        && text
            .bytes()
            .all(|b| b"0123456789ABCDEFGHJKMNPQRSTVWXYZ".contains(&b))
}
