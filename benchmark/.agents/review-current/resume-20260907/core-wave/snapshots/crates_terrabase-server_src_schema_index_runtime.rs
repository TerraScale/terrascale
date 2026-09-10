//! Applies declared physical indexes and TTL before migration completion.
use crate::schema_api::{CollectionSchema, DurableSchemaRecord, IndexKind};
use std::collections::BTreeSet;
use terrabase::{
    engine::Engine,
    types::{CollectionName, TenantScope},
};

fn scalar_fields(schema: &CollectionSchema) -> BTreeSet<String> {
    schema
        .fields
        .iter()
        .filter(|field| field.indexed || field.unique)
        .map(|field| field.name.clone())
        .chain(
            schema
                .indexes
                .iter()
                .filter(|index| index.kind != Some(IndexKind::Fts))
                .flat_map(|index| index.fields.clone()),
        )
        .collect()
}

pub(super) fn advance(
    engine: &Engine,
    scope: &TenantScope,
    record: &DurableSchemaRecord,
) -> Result<bool, String> {
    let schema = &record.document.schema;
    let collection = CollectionName::try_new(&schema.name).map_err(|_| "invalid collection")?;
    engine
        .set_collection_history_retention(&collection, schema.history_retention_days)
        .map_err(|_| "history retention installation failed")?;
    let fields = scalar_fields(schema);
    let mut ready = true;
    for field in &fields {
        engine.register_index_field(&collection, field).map_err(|_| "index registration failed")?;
    }
    let text_fields: BTreeSet<_> = schema
        .indexes
        .iter()
        .filter(|index| index.kind == Some(IndexKind::Fts))
        .flat_map(|index| index.fields.iter())
        .chain(schema.fts.iter().flat_map(|fts| fts.fields.iter()))
        .collect();
    for field in text_fields {
        engine
            .register_fts_field(&collection, field)
            .map_err(|_| "full-text registration failed")?;
        ready &=
            engine.schema_fts_ready(&collection, field).map_err(|_| "full-text progress failed")?;
    }
    for descriptor in engine.index_descriptors().map_err(|_| "index progress failed")? {
        if descriptor.collection == schema.name && fields.contains(&descriptor.field) {
            ready &= descriptor.active;
        }
    }
    // A removed TTL writes an explicit disabled descriptor, so the historical
    // schemaless expires_at fallback cannot unexpectedly resume.
    if schema.ttl.is_some() || record.previous_schema.as_ref().is_some_and(|old| old.ttl.is_some())
    {
        ready &= engine
            .advance_schema_ttl(&collection, schema.ttl.as_ref().map(|ttl| ttl.field.as_str()), 256)
            .map_err(|_| "TTL installation failed")?;
    }
    if !ready {
        return Ok(false);
    }
    for index in schema.indexes.iter().filter(|index| index.kind != Some(IndexKind::Fts)) {
        ready &= engine
            .register_schema_index(
                scope.tenant(),
                &collection,
                &index.name,
                &index.fields,
                index.unique,
            )
            .map_err(|_| "declared index installation failed")?;
    }
    for field in schema.fields.iter().filter(|field| field.unique) {
        ready &= engine
            .register_schema_index(
                scope.tenant(),
                &collection,
                &format!("field:{}", field.name),
                std::slice::from_ref(&field.name),
                true,
            )
            .map_err(|_| "field uniqueness installation failed")?;
    }
    remove_retired_indexes(
        engine,
        scope,
        &collection,
        schema,
        record.previous_schema.as_ref(),
        &fields,
    )?;
    Ok(ready)
}

fn remove_retired_indexes(
    engine: &Engine,
    scope: &TenantScope,
    collection: &CollectionName,
    schema: &CollectionSchema,
    previous_schema: Option<&CollectionSchema>,
    fields: &BTreeSet<String>,
) -> Result<(), String> {
    if let Some(old) = previous_schema {
        let current_text: BTreeSet<_> = schema
            .indexes
            .iter()
            .filter(|index| index.kind == Some(IndexKind::Fts))
            .flat_map(|index| index.fields.iter())
            .chain(schema.fts.iter().flat_map(|fts| fts.fields.iter()))
            .collect();
        let old_text: BTreeSet<_> = old
            .indexes
            .iter()
            .filter(|index| index.kind == Some(IndexKind::Fts))
            .flat_map(|index| index.fields.iter())
            .chain(old.fts.iter().flat_map(|fts| fts.fields.iter()))
            .collect();
        for field in old_text.difference(&current_text) {
            engine
                .remove_schema_fts(collection, field)
                .map_err(|_| "retired full-text index removal failed")?;
        }

        for index in old.indexes.iter().filter(|index| index.kind != Some(IndexKind::Fts)) {
            if !schema
                .indexes
                .iter()
                .any(|current| current.name == index.name && current.kind != Some(IndexKind::Fts))
            {
                engine
                    .remove_schema_index(scope.tenant(), collection, &index.name)
                    .map_err(|_| "retired index removal failed")?;
            }
        }
        for field in old.fields.iter().filter(|field| field.unique) {
            if !schema.fields.iter().any(|current| current.name == field.name && current.unique) {
                engine
                    .remove_schema_index(
                        scope.tenant(),
                        collection,
                        &format!("field:{}", field.name),
                    )
                    .map_err(|_| "retired uniqueness removal failed")?;
            }
        }
        for field in scalar_fields(old).difference(fields) {
            engine
                .remove_index_field(collection, field)
                .map_err(|_| "retired field index removal failed")?;
        }
    }
    Ok(())
}
