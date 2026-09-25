//! One-call, authority-bound reads of applied distributed document state.

use tb_core::{Attribution, CollectionId, DocumentId, MemoryCategory, ReadSnapshot};
use tb_format::{
    CausalManifest, DocumentKind, SiblingDescriptor, decode_catalog, decode_document,
    descriptor_change_key, encode_catalog_key, encode_primary_key, validate_document_schema,
    verify_descriptor_change,
};
use tb_replication::{CausalContext, CollectionBinding};

use crate::distributed::DistributedDocumentStore;
use crate::distributed_apply::poison_on_applied_read_error;
use crate::distributed_state::{check_signed_binding, effective_limits, load_manifest};
use crate::{DocumentError, DocumentPoint, DocumentRead, OwnedSibling, key};

impl DistributedDocumentStore {
    /// Read every visible sibling at one protected document visibility cut.
    /// The returned owned result retains its output memory claim. No snapshot
    /// handle or applied network acknowledgment is issued by this operation.
    ///
    /// # Errors
    /// Requires reconciled receivers and rejects corrupt stored state or an
    /// output that cannot be admitted within the process memory budget.
    pub fn point_read(
        &self,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<DocumentRead, DocumentError> {
        let mut writer = self
            .writer
            .lock()
            .map_err(|_| DocumentError::NeedsRecovery)?;
        if writer.poisoned || !writer.inboxes_reconciled {
            return Err(DocumentError::NeedsRecovery);
        }
        let result = self.point_read_at(self.storage.snapshot(), collection, document);
        if let Err(error) = &result {
            poison_on_applied_read_error(&mut writer, error);
        }
        result
    }

    fn point_read_at(
        &self,
        snapshot: ReadSnapshot,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<DocumentRead, DocumentError> {
        let attribution = Attribution {
            tenant: self.config.storage.tenant,
            worker: self.config.storage.worker,
            category: MemoryCategory::Query,
        };
        let primary_key = key(encode_primary_key(collection, document))?;
        let Some(primary) = self.storage.get(snapshot, &primary_key)? else {
            return Ok(DocumentRead {
                point: DocumentPoint::Absent,
                _permit: self.budget.try_reserve(attribution, 0)?,
            });
        };
        let view = self.authority.view();
        let limits = effective_limits(self.config.replication);
        let (manifest, _manifest_permit) = load_manifest(
            &self.storage,
            &self.budget,
            &self.config.storage,
            view,
            limits,
            snapshot,
            (primary_key.as_bytes(), primary.bytes()),
        )?;
        drop(primary);
        if manifest.collection() != collection || manifest.document() != document {
            return Err(DocumentError::Corrupt(
                "D0 manifest document binding mismatch",
            ));
        }

        // D1 wrapper lengths bound the copied inner documents. The fixed and
        // per-entry context claim covers BTreeMap node capacity, including its
        // first allocation. Claim all output before copying any bytes.
        let mut output_bytes = manifest
            .siblings()
            .len()
            .checked_mul(std::mem::size_of::<OwnedSibling>())
            .and_then(|n| {
                manifest
                    .context()
                    .len()
                    .checked_mul(128)
                    .and_then(|context| context.checked_add(1_024))
                    .and_then(|context| n.checked_add(context))
            })
            .ok_or(DocumentError::Capacity("point output bytes"))?;
        for descriptor in manifest.siblings() {
            output_bytes = output_bytes
                .checked_add(descriptor.change_value_len as usize)
                .ok_or(DocumentError::Capacity("point output bytes"))?;
        }
        let permit = self.budget.try_reserve(attribution, output_bytes)?;
        let mut siblings = Vec::new();
        siblings
            .try_reserve_exact(manifest.siblings().len())
            .map_err(|_| DocumentError::Capacity("point sibling allocation"))?;

        for &descriptor in manifest.siblings() {
            siblings.push(self.owned_sibling_at(snapshot, &manifest, descriptor, attribution)?);
        }
        let context = CausalContext::from_entries(
            manifest
                .context()
                .iter()
                .map(|dot| (dot.origin, dot.sequence)),
            limits.max_origins,
        )?;
        let point = match siblings.len() {
            0 => return Err(DocumentError::Corrupt("D6 has no visible siblings")),
            1 => {
                let sibling = siblings
                    .pop()
                    .ok_or(DocumentError::Corrupt("D6 has no visible sibling"))?;
                if sibling.kind == DocumentKind::Tombstone {
                    DocumentPoint::Deleted { context, sibling }
                } else {
                    DocumentPoint::Value { context, sibling }
                }
            }
            _ => DocumentPoint::Conflict { context, siblings },
        };
        Ok(DocumentRead {
            point,
            _permit: permit,
        })
    }

    fn owned_sibling_at(
        &self,
        snapshot: ReadSnapshot,
        manifest: &CausalManifest,
        descriptor: SiblingDescriptor,
        attribution: Attribution,
    ) -> Result<OwnedSibling, DocumentError> {
        let view = self.authority.view();
        if !view
            .members()
            .any(|member| member.origin == descriptor.event.origin)
        {
            return Err(DocumentError::Corrupt("D6 sibling origin is not signed"));
        }
        let binding = CollectionBinding {
            collection: manifest.collection(),
            generation: descriptor.generation,
            schema_digest: descriptor.schema_digest,
        };
        let signed = check_signed_binding(view, binding)?;
        let catalog_key = key(encode_catalog_key(
            manifest.collection(),
            descriptor.generation,
        )?)?;
        let persisted = self
            .storage
            .get(snapshot, &catalog_key)?
            .ok_or(DocumentError::Corrupt("signed C0 catalog missing"))?;
        if persisted.bytes() != signed {
            return Err(DocumentError::Corrupt(
                "persisted C0 differs from authority",
            ));
        }
        drop(persisted);
        let catalog_work = signed
            .len()
            .checked_mul(4)
            .and_then(|bytes| bytes.checked_add(4_096))
            .ok_or(DocumentError::Capacity("catalog validation bytes"))?;
        let _catalog_permit = self.budget.try_reserve(attribution, catalog_work)?;
        let catalog = decode_catalog(signed)?;

        let change_key = key(descriptor_change_key(manifest, descriptor)?)?;
        let change_value = self
            .storage
            .get(snapshot, &change_key)?
            .ok_or(DocumentError::Corrupt("D6 references missing D1"))?;
        let change = verify_descriptor_change(
            manifest,
            descriptor,
            change_key.as_bytes(),
            change_value.bytes(),
        )?;
        if change.kind == DocumentKind::Live {
            validate_document_schema(decode_document(change.document_bytes)?, &catalog.schema)?;
        }
        let mut document_bytes = Vec::new();
        document_bytes
            .try_reserve_exact(change.document_bytes.len())
            .map_err(|_| DocumentError::Capacity("point document allocation"))?;
        document_bytes.extend_from_slice(change.document_bytes);
        Ok(OwnedSibling {
            event: change.event,
            generation: change.binding.generation,
            schema_digest: change.binding.schema_digest,
            kind: change.kind,
            document_bytes,
        })
    }
}
