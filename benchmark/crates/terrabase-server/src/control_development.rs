//! Bounded durable development-control audit, isolated from database registry IDs.
use super::{
    CollectionName, CommitOutcome, ControlError, ControlPlane, Document, DocumentId, DocumentValue,
    ExpectedVersion, LocalDurability, TenantId, registry_error,
};

impl ControlPlane {
    pub(crate) fn development_audit(
        &self,
        name: &str,
        event: serde_json::Value,
    ) -> Result<(), ControlError> {
        let scope = self.engine.scope_for(
            &TenantId::try_new("terrascale-development-audit")
                .map_err(|e| ControlError::Registry(format!("{e:?}")))?,
        );
        let snapshot = self.engine.read_snapshot(&scope).map_err(|e| registry_error(&e))?;
        let mut tx = self
            .engine
            .begin(
                &scope,
                snapshot,
                crate::read_operation::next().map_err(|()| {
                    ControlError::Registry("audit operation identities exhausted".into())
                })?,
            )
            .map_err(|e| registry_error(&e))?;
        let id = DocumentId::try_new(name).map_err(|_| ControlError::InvalidName(name.into()))?;
        let existing = tx.read(&id).map_err(|e| registry_error(&e))?;
        let mut events: Vec<serde_json::Value> = match existing.as_ref().map(Document::value) {
            Some(DocumentValue::String(value)) => serde_json::from_str(value)
                .map_err(|_| ControlError::Registry("invalid development audit".into()))?,
            None => Vec::new(),
            _ => return Err(ControlError::Registry("invalid development audit".into())),
        };
        events.push(event);
        if events.len() > 100 {
            events.remove(0);
        }
        let expected = existing.as_ref().map_or(ExpectedVersion::New, |document| {
            ExpectedVersion::Exact(document.metadata().version())
        });
        let document = Document::new(
            id,
            CollectionName::try_new("development-audit")
                .map_err(|e| ControlError::Registry(format!("{e:?}")))?,
            DocumentValue::String(
                serde_json::to_string(&events)
                    .map_err(|_| ControlError::Registry("audit serialization failed".into()))?,
            ),
        );
        tx.put(document, expected).map_err(|e| registry_error(&e))?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            _ => Err(ControlError::Registry("development audit commit failed".into())),
        }
    }
}
