//! The typed document patch vocabulary (GAP-D07, ticket #60).
//!
//! A patch is data, not a program: a closed set of path-addressed operations
//! — `Add`, `Replace`, `Remove`, `Increment`, and the `AddOrPatch` compound —
//! applied by [`apply_document_patch`] at the engine's transaction seam.
//! Paths are typed segments validated at construction (there is deliberately
//! no string form), application is strictly in vector order with later
//! operations seeing earlier effects, every refusal is a typed
//! [`PatchError`], and a patch that fails anywhere stages nothing, so the
//! transaction seam's all-or-nothing publication is the only write path.
//! The decision record is `docs/engine/gapd07-patch-language.md`; V1's
//! script-based patch surface is excluded by charter `PD-2026-08-26-08` D2
//! (no script execution, ever).

use std::collections::BTreeMap;

use crate::engine::{EngineError, Transaction};
use crate::operations::RequestValidationError;
use crate::types::{CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion};

/// The maximum number of operations one [`DocumentPatch`] carries.
pub const MAX_PATCH_OPERATIONS: usize = 32;

/// The maximum number of segments one [`PatchPath`] walks.
///
/// The bound matches `MAX_INCLUDE_PATH_SEGMENTS`, the seam's other closed
/// path vocabulary.
pub const MAX_PATCH_PATH_SEGMENTS: usize = 8;

/// The maximum byte length of one field segment.
///
/// The bound matches [`MAX_IDENTIFIER_BYTES`](crate::types), the logical
/// identifier limit, so a field segment can never out-size an identifier.
pub const MAX_PATCH_FIELD_BYTES: usize = 255;

/// One typed step of a [`PatchPath`].
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PatchPathSegment {
    /// One object member, addressed by exact field name.
    Field(String),
    /// One array position. In bounds for insert, replace, and remove; `Add`
    /// at an index inserts at that position, shifting later elements.
    Index(u32),
    /// The position past an array's last element: the only valid final
    /// segment of an `Add`, never a target of `Replace`/`Remove`/
    /// `Increment`.
    Append,
}

impl PatchPathSegment {
    /// Creates a validated field segment.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty name and
    /// [`RequestValidationError::InvalidPatch`] for a name over
    /// [`MAX_PATCH_FIELD_BYTES`] bytes or carrying control characters.
    pub fn field(name: &str) -> Result<Self, RequestValidationError> {
        validate_field_name(name)?;
        Ok(Self::Field(name.to_owned()))
    }

    /// Creates an array-position segment.
    #[must_use]
    pub const fn index(position: u32) -> Self {
        Self::Index(position)
    }

    /// Creates the array-end segment.
    #[must_use]
    pub const fn append() -> Self {
        Self::Append
    }
}

/// Validates one field-segment name against the closed bounds.
fn validate_field_name(name: &str) -> Result<(), RequestValidationError> {
    if name.is_empty() {
        return Err(RequestValidationError::EmptyField);
    }
    if name.len() > MAX_PATCH_FIELD_BYTES || name.chars().any(char::is_control) {
        return Err(RequestValidationError::InvalidPatch);
    }
    Ok(())
}

/// A validated, bounded, typed address into a document value.
///
/// Constructed only through [`PatchPath::try_from_segments`], which enforces
/// the closed bounds before any data access. There is no string form and no
/// escape alphabet at this seam: addressing is by typed segments, not by an
/// escaped pointer string (decision D3 of the patch-language record).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PatchPath {
    segments: Vec<PatchPathSegment>,
}

impl PatchPath {
    /// Creates a path from typed segments.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty segment
    /// list (the document root is addressed by whole-document operations,
    /// not by patches) or an empty field name, and
    /// [`RequestValidationError::InvalidPatch`] for a path deeper than
    /// [`MAX_PATCH_PATH_SEGMENTS`], a field name outside the byte or
    /// character bound, or an [`PatchPathSegment::Append`] that is not the
    /// final segment.
    pub fn try_from_segments(
        segments: Vec<PatchPathSegment>,
    ) -> Result<Self, RequestValidationError> {
        if segments.is_empty() {
            return Err(RequestValidationError::EmptyField);
        }
        if segments.len() > MAX_PATCH_PATH_SEGMENTS {
            return Err(RequestValidationError::InvalidPatch);
        }
        for (position, segment) in segments.iter().enumerate() {
            match segment {
                PatchPathSegment::Field(name) => validate_field_name(name)?,
                PatchPathSegment::Index(_) => {}
                PatchPathSegment::Append => {
                    if position + 1 != segments.len() {
                        return Err(RequestValidationError::InvalidPatch);
                    }
                }
            }
        }
        Ok(Self { segments })
    }

    /// Returns the typed segments, in walk order.
    #[must_use]
    pub fn segments(&self) -> &[PatchPathSegment] {
        &self.segments
    }
}

/// One path-addressed document mutation.
#[derive(Clone, Debug, PartialEq)]
pub enum PatchOperation {
    /// Create-or-overwrite at the path. Creates missing intermediate
    /// objects (never arrays), inserts in bounds at an array index, and
    /// appends at [`PatchPathSegment::Append`].
    Add {
        /// The addressed position.
        path: PatchPath,
        /// The value to place there.
        value: DocumentValue,
    },
    /// Set the value at a position that must already exist.
    Replace {
        /// The addressed position.
        path: PatchPath,
        /// The value to place there.
        value: DocumentValue,
    },
    /// Remove a member or array element that must already exist.
    Remove {
        /// The addressed position.
        path: PatchPath,
    },
    /// Add a signed delta to an integer at the path, setting the delta when
    /// the position is absent. Non-integer targets refuse typed.
    Increment {
        /// The addressed position.
        path: PatchPath,
        /// The signed delta.
        delta: i64,
    },
}

/// A bounded, ordered list of patch operations.
///
/// Operations apply strictly in vector order, each to the result of the
/// previous one. Constructed only through [`DocumentPatch::try_new`].
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentPatch {
    operations: Vec<PatchOperation>,
}

impl DocumentPatch {
    /// Creates a patch after validating its shape.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty
    /// operation list and [`RequestValidationError::InvalidPatch`] for a
    /// list longer than [`MAX_PATCH_OPERATIONS`] or an operation whose
    /// `Replace`/`Remove`/`Increment` path ends in
    /// [`PatchPathSegment::Append`] — the array end is a position only
    /// `Add` can address.
    pub fn try_new(operations: Vec<PatchOperation>) -> Result<Self, RequestValidationError> {
        if operations.is_empty() {
            return Err(RequestValidationError::EmptyField);
        }
        if operations.len() > MAX_PATCH_OPERATIONS {
            return Err(RequestValidationError::InvalidPatch);
        }
        for operation in &operations {
            let path = match operation {
                PatchOperation::Add { .. } => continue,
                PatchOperation::Replace { path, .. }
                | PatchOperation::Remove { path }
                | PatchOperation::Increment { path, .. } => path,
            };
            if matches!(path.segments().last(), Some(PatchPathSegment::Append)) {
                return Err(RequestValidationError::InvalidPatch);
            }
        }
        Ok(Self { operations })
    }

    /// Returns the operations, in application order.
    #[must_use]
    pub fn operations(&self) -> &[PatchOperation] {
        &self.operations
    }
}

/// The patch-if-present, create-if-missing compound (V1's `patchIfMissing`
/// and `CreateIfMissing` shape).
#[derive(Clone, Debug, PartialEq)]
pub struct AddOrPatch {
    if_missing: Document,
    patch: DocumentPatch,
}

impl AddOrPatch {
    /// Wraps the create-value and the existing-document patch.
    #[must_use]
    pub fn new(if_missing: Document, patch: DocumentPatch) -> Self {
        Self { if_missing, patch }
    }

    /// Returns the document stored when the target is missing.
    #[must_use]
    pub fn if_missing(&self) -> &Document {
        &self.if_missing
    }

    /// Returns the patch applied when the target exists.
    #[must_use]
    pub fn patch(&self) -> &DocumentPatch {
        &self.patch
    }
}

/// The outcome of one applied patch.
///
/// The V1 status set this maps is `Patched`/`Created`/`NotModified`
/// (`PatchResult.cs`); V1's `Skipped` and `DocumentDoesNotExist` statuses
/// become typed refusals instead, because a write path that reports success
/// while silently changing nothing is the shape the typed seam refuses.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PatchOutcome {
    /// The patch applied and the document changed.
    Applied,
    /// The target was missing and the `AddOrPatch` create leg stored the
    /// if-missing document.
    Created,
    /// The patched value equals the original value: nothing was staged.
    NotModified,
}

/// A typed patch refusal. No path detail, payload, or script text crosses
/// this seam.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PatchError {
    /// The patch shape was malformed. Construction refusals are reported
    /// through [`RequestValidationError`] directly; this variant carries
    /// the shape checks the seam re-checks at application time.
    Construction(RequestValidationError),
    /// The target document does not exist (V1 parity: a patch on a missing
    /// document fails closed).
    MissingTarget,
    /// The observed document version contradicts the expectation.
    StaleTarget {
        /// The version the seam actually observed.
        observed: u64,
    },
    /// An addressed position does not exist: a `Replace`/`Remove` target, an
    /// out-of-bounds array index, or a walk through an absent member.
    PathNotFound,
    /// A segment addressed a container of the wrong kind — a field into a
    /// non-object, an index or append into a non-array or a missing array
    /// (arrays are never materialized) — or `Increment` on a non-integer.
    TypeMismatch,
    /// A checked `Increment` would overflow the integer range.
    ArithmeticOverflow,
    /// The underlying transaction read or staging failed, including the
    /// document structural limits and collection schema enforcement the
    /// write path already runs.
    Engine(EngineError),
}

/// Checks the expected-version discipline against the observed version.
///
/// This mirrors the check the commit seam performs on the snapshot view
/// (`conflict_between`), so a patch that passes here and still loses a race
/// is rejected by the commit as `CommitError::Conflict` rather than
/// silently overwriting.
fn check_expectation(observed: u64, expected: ExpectedVersion) -> Result<(), PatchError> {
    let satisfied = match expected {
        ExpectedVersion::Any => true,
        // A create expectation names a version the document already has.
        ExpectedVersion::New => false,
        ExpectedVersion::Exact(version) => version == observed,
    };
    if satisfied { Ok(()) } else { Err(PatchError::StaleTarget { observed }) }
}

/// Returns the value as a mutable object, refusing other kinds.
fn as_object_mut(
    value: &mut DocumentValue,
) -> Result<&mut BTreeMap<String, DocumentValue>, PatchError> {
    match value {
        DocumentValue::Object(fields) => Ok(fields),
        _ => Err(PatchError::TypeMismatch),
    }
}

/// Returns the value as a mutable array, refusing other kinds.
fn as_array_mut(value: &mut DocumentValue) -> Result<&mut Vec<DocumentValue>, PatchError> {
    match value {
        DocumentValue::Array(items) => Ok(items),
        _ => Err(PatchError::TypeMismatch),
    }
}

/// Applies one `Add`: create-or-overwrite with object auto-vivification.
fn add_at(
    container: &mut DocumentValue,
    segments: &[PatchPathSegment],
    value: DocumentValue,
) -> Result<(), PatchError> {
    let Some((first, rest)) = segments.split_first() else {
        return Err(PatchError::Construction(RequestValidationError::EmptyField));
    };
    match first {
        PatchPathSegment::Field(name) => {
            let fields = as_object_mut(container)?;
            if rest.is_empty() {
                fields.insert(name.clone(), value);
                return Ok(());
            }
            let child = fields
                .entry(name.clone())
                .or_insert_with(|| DocumentValue::Object(BTreeMap::new()));
            add_at(child, rest, value)
        }
        PatchPathSegment::Index(index) => {
            let items = as_array_mut(container)?;
            let position = usize::try_from(*index).map_err(|_| PatchError::PathNotFound)?;
            if rest.is_empty() {
                if position >= items.len() {
                    // V1 parity: an insert must address an existing position;
                    // the end is `Append`, not `len`.
                    return Err(PatchError::PathNotFound);
                }
                items.insert(position, value);
                return Ok(());
            }
            let child = items.get_mut(position).ok_or(PatchError::PathNotFound)?;
            add_at(child, rest, value)
        }
        PatchPathSegment::Append => {
            // Construction guarantees `Append` is final and only under `Add`;
            // the walk treats reaching it mid-path as a malformed shape.
            if !rest.is_empty() {
                return Err(PatchError::Construction(RequestValidationError::InvalidPatch));
            }
            as_array_mut(container)?.push(value);
            Ok(())
        }
    }
}

/// Applies one `Replace` or `Remove`: the addressed position must exist.
fn replace_or_remove_at(
    container: &mut DocumentValue,
    segments: &[PatchPathSegment],
    value: Option<DocumentValue>,
) -> Result<(), PatchError> {
    let Some((first, rest)) = segments.split_first() else {
        return Err(PatchError::Construction(RequestValidationError::EmptyField));
    };
    match first {
        PatchPathSegment::Field(name) => {
            let fields = as_object_mut(container)?;
            if rest.is_empty() {
                let existing = fields.get_mut(name).ok_or(PatchError::PathNotFound)?;
                match value {
                    Some(replacement) => *existing = replacement,
                    None => {
                        fields.remove(name);
                    }
                }
                return Ok(());
            }
            let child = fields.get_mut(name).ok_or(PatchError::PathNotFound)?;
            replace_or_remove_at(child, rest, value)
        }
        PatchPathSegment::Index(index) => {
            let items = as_array_mut(container)?;
            let position = usize::try_from(*index).map_err(|_| PatchError::PathNotFound)?;
            if rest.is_empty() {
                if position >= items.len() {
                    return Err(PatchError::PathNotFound);
                }
                match value {
                    Some(replacement) => items[position] = replacement,
                    None => {
                        items.remove(position);
                    }
                }
                return Ok(());
            }
            let child = items.get_mut(position).ok_or(PatchError::PathNotFound)?;
            replace_or_remove_at(child, rest, value)
        }
        PatchPathSegment::Append => {
            Err(PatchError::Construction(RequestValidationError::InvalidPatch))
        }
    }
}

/// Applies one `Increment`: typed integer arithmetic with a set-on-absent.
fn increment_at(
    container: &mut DocumentValue,
    segments: &[PatchPathSegment],
    delta: i64,
) -> Result<(), PatchError> {
    let Some((first, rest)) = segments.split_first() else {
        return Err(PatchError::Construction(RequestValidationError::EmptyField));
    };
    match first {
        PatchPathSegment::Field(name) => {
            let fields = as_object_mut(container)?;
            if rest.is_empty() {
                return match fields.get(name) {
                    None => {
                        fields.insert(name.clone(), DocumentValue::Integer(delta));
                        Ok(())
                    }
                    Some(DocumentValue::Integer(current)) => match current.checked_add(delta) {
                        Some(total) => {
                            fields.insert(name.clone(), DocumentValue::Integer(total));
                            Ok(())
                        }
                        None => Err(PatchError::ArithmeticOverflow),
                    },
                    Some(_) => Err(PatchError::TypeMismatch),
                };
            }
            let child = fields.get_mut(name).ok_or(PatchError::PathNotFound)?;
            increment_at(child, rest, delta)
        }
        PatchPathSegment::Index(index) => {
            let items = as_array_mut(container)?;
            let position = usize::try_from(*index).map_err(|_| PatchError::PathNotFound)?;
            if rest.is_empty() {
                let Some(DocumentValue::Integer(current)) = items.get(position) else {
                    return match items.get(position) {
                        // An out-of-bounds position does not exist; an
                        // in-bounds non-integer is a type mismatch.
                        None => Err(PatchError::PathNotFound),
                        Some(_) => Err(PatchError::TypeMismatch),
                    };
                };
                let total = current.checked_add(delta).ok_or(PatchError::ArithmeticOverflow)?;
                items[position] = DocumentValue::Integer(total);
                return Ok(());
            }
            let child = items.get_mut(position).ok_or(PatchError::PathNotFound)?;
            increment_at(child, rest, delta)
        }
        PatchPathSegment::Append => {
            Err(PatchError::Construction(RequestValidationError::InvalidPatch))
        }
    }
}

/// Applies one operation to a working value. Any refusal abandons the whole
/// working value; the caller stages nothing.
fn apply_operation(
    value: &mut DocumentValue,
    operation: &PatchOperation,
) -> Result<(), PatchError> {
    match operation {
        PatchOperation::Add { path, value: replacement } => {
            add_at(value, path.segments(), replacement.clone())
        }
        PatchOperation::Replace { path, value: replacement } => {
            replace_or_remove_at(value, path.segments(), Some(replacement.clone()))
        }
        PatchOperation::Remove { path } => replace_or_remove_at(value, path.segments(), None),
        PatchOperation::Increment { path, delta } => increment_at(value, path.segments(), *delta),
    }
}

/// Applies one document-targeted patch at the engine's transaction seam.
///
/// The target is read with exact collection-and-identity key resolution,
/// the expected-version discipline is checked against the observed version
/// before anything is staged, every operation applies to a working clone in
/// vector order, and the result stages as exactly one
/// [`Transaction::put`] — inheriting the document limits, the collection
/// schema enforcement, and the commit seam's all-or-nothing publication. A
/// patch that refuses anywhere stages nothing; a patch whose result equals
/// the original value stages nothing and reports
/// [`PatchOutcome::NotModified`].
///
/// A patch is one staged put, so a second patch of the same document in one
/// transaction is the commit seam's existing duplicate-target refusal, not
/// a second write.
///
/// # Errors
///
/// Returns the typed refusal set of [`PatchError`]: a missing target, a
/// stale expectation, an addressed position that does not exist, a
/// wrong-kind container or non-integer increment target, arithmetic
/// overflow, or the underlying engine failure.
pub fn apply_document_patch(
    transaction: &mut Transaction<'_>,
    collection: &CollectionName,
    id: &DocumentId,
    expected: ExpectedVersion,
    patch: &DocumentPatch,
) -> Result<PatchOutcome, PatchError> {
    let Some(mut document) =
        transaction.read_in_collection(collection, id).map_err(PatchError::Engine)?
    else {
        return Err(PatchError::MissingTarget);
    };
    check_expectation(document.metadata().version(), expected)?;
    let original = document.value().clone();
    let mut patched = original.clone();
    for operation in patch.operations() {
        apply_operation(&mut patched, operation)?;
    }
    if patched == original {
        return Ok(PatchOutcome::NotModified);
    }
    document.set_value(patched);
    transaction.put(document, expected).map_err(PatchError::Engine)?;
    Ok(PatchOutcome::Applied)
}

/// Applies the patch-if-present, create-if-missing compound.
///
/// The create leg stages the if-missing document under
/// [`ExpectedVersion::New`] — it is a create — and reports
/// [`PatchOutcome::Created`]; the present leg is
/// [`apply_document_patch`] under the caller's expectation. The create
/// document must carry exactly the addressed collection and id: V1's
/// create-if-missing path stores the create value under the command's
/// addressed key (`PatchDocumentCommand.cs:186` puts `id` with the create
/// body), never at an identity the value itself names, so a compound
/// whose create document retargets the write is refused as malformed
/// rather than silently writing to the unaddressed target.
///
/// # Errors
///
/// Returns the [`apply_document_patch`] refusal set,
/// [`PatchError::Construction`] with
/// [`RequestValidationError::InvalidPatch`] when the create document's
/// collection or id differs from the addressed pair, or the engine
/// failure of staging the create.
pub fn apply_add_or_patch(
    transaction: &mut Transaction<'_>,
    collection: &CollectionName,
    id: &DocumentId,
    expected: ExpectedVersion,
    add_or_patch: &AddOrPatch,
) -> Result<PatchOutcome, PatchError> {
    let create = add_or_patch.if_missing();
    if create.collection() != collection || create.id() != id {
        return Err(PatchError::Construction(RequestValidationError::InvalidPatch));
    }
    let existing = transaction.read_in_collection(collection, id).map_err(PatchError::Engine)?;
    if existing.is_some() {
        return apply_document_patch(transaction, collection, id, expected, add_or_patch.patch());
    }
    transaction
        .put(add_or_patch.if_missing().clone(), ExpectedVersion::New)
        .map_err(PatchError::Engine)?;
    Ok(PatchOutcome::Created)
}
