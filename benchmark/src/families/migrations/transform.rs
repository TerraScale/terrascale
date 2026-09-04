//! The declarative transform vocabulary of one Schema Migration (SM-03).
//!
//! A migration moves a collection between [`SchemaVersion`] identities by
//! applying a closed, ordered list of typed field transforms to every
//! document of the collection. Every transform states its own coercion
//! contract — exactly which document shapes it changes, which it leaves
//! untouched, and which it rejects fail-closed — so the same list drives the
//! dry-run plan, the apply pass, and the rollback inverse without any
//! embedded interpreter or callback.

use std::collections::BTreeMap;

use crate::families::{
    payloads::{SchemaDefinition, SchemaVersion, validate_name as validate_field_name},
    schemas::validate_schema,
    state::RESERVED_COLLECTION,
};

use super::MigrationError;
use crate::types::{CollectionName, DocumentValue};

/// The maximum number of transforms in one migration contract.
///
/// The bound keeps a migration's per-document work bounded by construction,
/// mirroring the schema rule bound of the model this module evolves.
pub const MAX_MIGRATION_TRANSFORMS: usize = 256;

/// One closed, explicit coercion between two logical field types.
///
/// Every coercion is total on the source type it names and rejects values
/// outside its explicit domain: a coercion never guesses. The two exact
/// pairs ([`Coercion::IntegerToText`]/[`Coercion::TextToInteger`] and
/// [`Coercion::IntegerToNumber`]/[`Coercion::NumberToInteger`]) invert each
/// other exactly, which is what makes a reversible migration's rollback
/// lossless.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Coercion {
    /// Renders a signed integer as its canonical decimal text.
    IntegerToText,
    /// Parses canonical signed-decimal text into an integer.
    ///
    /// Rejected for every other text: surrounding whitespace, a leading
    /// `+`, leading zeros, an empty digit run, or an out-of-range value.
    TextToInteger,
    /// Widens a signed integer into a number.
    ///
    /// Rejected when the integer lies outside the exactly representable
    /// integer range of the float domain: a coercion never loses a digit
    /// silently.
    IntegerToNumber,
    /// Narrows a number into an integer.
    ///
    /// Rejected for non-finite values, fractional values, and values
    /// outside the signed 64-bit integer range.
    NumberToInteger,
}

/// The closed literal set a declarative default can carry.
///
/// The literal is the value an [`FieldTransform::AddField`] transform writes
/// when the field is absent; it is typed and bounded by construction, never
/// an arbitrary document subtree.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum TransformLiteral {
    /// A boolean default.
    Boolean(bool),
    /// A signed integer default.
    Integer(i64),
    /// A text default.
    Text(String),
}

impl TransformLiteral {
    /// Returns the literal as a document value.
    #[must_use]
    pub fn to_value(&self) -> DocumentValue {
        match self {
            Self::Boolean(value) => DocumentValue::Boolean(*value),
            Self::Integer(value) => DocumentValue::Integer(*value),
            Self::Text(value) => DocumentValue::String(value.clone()),
        }
    }
}

/// Why one transform cannot apply to one document value.
///
/// The rejection is typed and content-safe: it names a rule and a field,
/// never a field value.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TransformRejection {
    /// The field's value cannot be coerced under the named coercion.
    ValueNotCoercible,
    /// A rename found its destination field already present, so moving the
    /// value would silently overwrite it.
    TargetFieldOccupied,
}

/// The outcome of applying one transform to one document value.
#[derive(Clone, Debug, PartialEq)]
pub enum FieldOutcome {
    /// The transform's coercion contract says this document is not its
    /// shape: the value is returned unchanged. Every transform is defined
    /// so that re-applying it to its own result is `Unchanged` — page
    /// application is idempotent by construction.
    Unchanged,
    /// The transform changed the value.
    Changed(DocumentValue),
    /// The transform's coercion contract rejects this value; the migration
    /// fails closed with the typed [`MigrationError::TransformRejected`].
    Rejected(TransformRejection),
}

/// One closed declarative field transform.
///
/// Coercion contracts, per kind:
///
/// - [`FieldTransform::AddField`]: the field is absent — set it to the
///   default; the field is present — keep the present value. Adding is
///   add-if-absent, so it never overwrites stored data.
/// - [`FieldTransform::RemoveField`]: the field is present — remove it; the
///   field is absent — nothing to remove. Removal drops the stored values,
///   which is the primary rollback limit of the vocabulary.
/// - [`FieldTransform::RenameField`]: the source is present and the
///   destination absent — move the value; the source absent — nothing to
///   move; the destination present — reject, because moving would
///   overwrite it.
/// - [`FieldTransform::RetypeField`]: the field is present and carries a
///   value the coercion addresses — coerce it, rejecting values outside the
///   coercion's explicit domain; any other shape — nothing this coercion
///   addresses, the value passes through unchanged.
///
/// Every contract clause is a skip or a rejection, never a silent guess, and
/// transforms apply to object values only: a non-object document value passes
/// through unchanged and is judged by the target schema, exactly as the
/// schema evaluator judges it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum FieldTransform {
    /// Set the field to a typed default when it is absent.
    AddField {
        /// The field name.
        field: String,
        /// The default written when the field is absent.
        default: TransformLiteral,
    },
    /// Remove the field and its stored values.
    RemoveField {
        /// The field name.
        field: String,
    },
    /// Move the field's value to a new field name.
    RenameField {
        /// The present field name.
        from: String,
        /// The destination field name.
        to: String,
    },
    /// Convert the field's value under one closed coercion.
    RetypeField {
        /// The field name.
        field: String,
        /// The explicit coercion.
        coercion: Coercion,
    },
}

impl FieldTransform {
    /// Returns the transform's primary field name.
    #[must_use]
    pub fn field(&self) -> &str {
        match self {
            Self::AddField { field, .. }
            | Self::RemoveField { field }
            | Self::RetypeField { field, .. } => field,
            Self::RenameField { from, .. } => from,
        }
    }

    /// Returns the transform's destination field when it differs from the
    /// primary one.
    #[must_use]
    pub fn destination_field(&self) -> Option<&str> {
        match self {
            Self::RenameField { to, .. } => Some(to),
            _ => None,
        }
    }

    /// Applies the transform to one document value under its coercion
    /// contract.
    #[must_use]
    pub fn apply(&self, value: &DocumentValue) -> FieldOutcome {
        let DocumentValue::Object(fields) = value else {
            return FieldOutcome::Unchanged;
        };
        match self {
            Self::AddField { field, default } => {
                if fields.contains_key(field) {
                    FieldOutcome::Unchanged
                } else {
                    let mut next = fields.clone();
                    next.insert(field.clone(), default.to_value());
                    FieldOutcome::Changed(DocumentValue::Object(next))
                }
            }
            Self::RemoveField { field } => {
                if fields.contains_key(field) {
                    let mut next = fields.clone();
                    next.remove(field);
                    FieldOutcome::Changed(DocumentValue::Object(next))
                } else {
                    FieldOutcome::Unchanged
                }
            }
            Self::RenameField { from, to } => match fields.get(from) {
                None => FieldOutcome::Unchanged,
                Some(present) => {
                    if fields.contains_key(to) {
                        FieldOutcome::Rejected(TransformRejection::TargetFieldOccupied)
                    } else {
                        let mut next = fields.clone();
                        let moved = present.clone();
                        next.remove(from);
                        next.insert(to.clone(), moved);
                        FieldOutcome::Changed(DocumentValue::Object(next))
                    }
                }
            },
            Self::RetypeField { field, coercion } => match fields.get(field) {
                None => FieldOutcome::Unchanged,
                Some(present) => match coerce(*coercion, present) {
                    Coerced::Addressed(coerced) => {
                        let mut next = fields.clone();
                        next.insert(field.clone(), coerced);
                        FieldOutcome::Changed(DocumentValue::Object(next))
                    }
                    Coerced::Refused => {
                        FieldOutcome::Rejected(TransformRejection::ValueNotCoercible)
                    }
                    Coerced::NotAddressed => FieldOutcome::Unchanged,
                },
            },
        }
    }
}

/// The result of attempting one closed coercion on one value.
enum Coerced {
    /// The coercion addressed the value and produced the coerced value.
    Addressed(DocumentValue),
    /// The coercion addressed the value's type but the value lies outside
    /// the coercion's explicit domain.
    Refused,
    /// The coercion does not address this value's logical type.
    NotAddressed,
}

/// Coerces one value under one closed coercion.
fn coerce(coercion: Coercion, value: &DocumentValue) -> Coerced {
    match (coercion, value) {
        (Coercion::IntegerToText, DocumentValue::Integer(number)) => {
            Coerced::Addressed(DocumentValue::String(number.to_string()))
        }
        (Coercion::TextToInteger, DocumentValue::String(text)) => {
            match parse_canonical_integer(text) {
                Some(number) => Coerced::Addressed(DocumentValue::Integer(number)),
                None => Coerced::Refused,
            }
        }
        (Coercion::IntegerToNumber, DocumentValue::Integer(number)) => {
            if integer_widens_exactly(*number) {
                Coerced::Addressed(DocumentValue::Number(integer_to_number(*number)))
            } else {
                Coerced::Refused
            }
        }
        (Coercion::NumberToInteger, DocumentValue::Number(number)) => {
            match number_narrows_exactly(*number) {
                Some(narrowed) => Coerced::Addressed(DocumentValue::Integer(narrowed)),
                None => Coerced::Refused,
            }
        }
        _ => Coerced::NotAddressed,
    }
}

/// Widens one signed integer into the float domain.
#[allow(clippy::cast_precision_loss, reason = "widening is the coercion's stated contract")]
fn integer_to_number(number: i64) -> f64 {
    number as f64
}

/// Reports whether the float domain holds this integer exactly.
#[allow(
    clippy::cast_possible_truncation,
    clippy::cast_precision_loss,
    clippy::float_cmp,
    reason = "the round-trip equality is the exactness check itself"
)]
fn integer_widens_exactly(number: i64) -> bool {
    let widened = integer_to_number(number);
    #[allow(
        clippy::cast_possible_truncation,
        clippy::cast_precision_loss,
        reason = "round-trip check"
    )]
    let round_tripped = widened as i64;
    round_tripped == number && integer_to_number(round_tripped) == widened && widened.fract() == 0.0
}

/// Narrows one float into the integer domain when it holds an exact integer.
#[allow(
    clippy::cast_possible_truncation,
    clippy::cast_possible_wrap,
    clippy::cast_precision_loss,
    clippy::float_cmp,
    reason = "the round-trip equality is the exactness check itself"
)]
fn number_narrows_exactly(number: f64) -> Option<i64> {
    if !number.is_finite() || number.fract() != 0.0 {
        return None;
    }
    #[allow(
        clippy::cast_possible_truncation,
        clippy::cast_possible_wrap,
        reason = "the round-trip check below rejects every inexact narrowing"
    )]
    let narrowed = number as i64;
    #[allow(
        clippy::cast_precision_loss,
        reason = "the equality check rejects every lossy widening"
    )]
    let round_tripped = narrowed as f64;
    if round_tripped == number { Some(narrowed) } else { None }
}

/// Parses exactly the canonical signed-decimal rendering of one integer.
///
/// The grammar is `-?0|-?[1-9][0-9]*` inside the signed 64-bit range: the
/// inverse of Rust's `i64::to_string`, so [`Coercion::IntegerToText`] and
/// [`Coercion::TextToInteger`] round-trip exactly. The render-back check
/// rejects `-0`, leading zeros, and every other alias of a canonical value.
fn parse_canonical_integer(text: &str) -> Option<i64> {
    let digits = text.strip_prefix('-').unwrap_or(text);
    if digits.is_empty() || !digits.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    let number = text.parse::<i64>().ok()?;
    if number.to_string() == text { Some(number) } else { None }
}

/// Why a migration's rollback limit is not "reversible".
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RollbackBlockedCause {
    /// A removed field's stored values were dropped; no inverse can restore
    /// them.
    FieldRemoved,
    /// An added field's default cannot be distinguished from a value that
    /// was already stored equal to the default, so removing the field could
    /// drop pre-migration data.
    FieldDefaulted,
}

/// The rollback limit of one migration contract: what a reverse migration
/// can and cannot restore.
///
/// The limit is computed from the transform list, stated in the contract,
/// carried on the receipt, and tested: a reversible migration's inverse is
/// the reversed transform list with every direction swapped; a migration
/// containing a destructive transform is not rollbackable and applying it
/// requires the explicit acknowledgment
/// ([`MigrationOptions::acknowledge_not_rollbackable`](super::MigrationOptions)).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RollbackLimit {
    /// The transform list inverts exactly; rollback restores every document
    /// the forward pass changed.
    Reversible,
    /// At least one transform's effect cannot be inverted; rollback is
    /// refused and the causes state why.
    NotReversible {
        /// The destructive transform kinds the contract contains.
        causes: Vec<RollbackBlockedCause>,
    },
}

/// One explicit Schema Migration contract: move one collection from one
/// [`SchemaVersion`] to the next under an ordered list of declarative
/// transforms (CONTEXT: Schema Migration).
///
/// The contract is the migration's manifest identity: its collection, its
/// from and to versions, its transform list, and its target schema. It is
/// forward-only (`to > from`, the same direction the recorded schema's
/// evolution allows), its target's version identity must equal `to`, and it
/// is rejected on the reserved family collection. This is distinct from the
/// V1-to-TerraBase Logical Migration, which moves data between engines.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaMigration {
    collection: CollectionName,
    from: SchemaVersion,
    to: SchemaVersion,
    target: SchemaDefinition,
    transforms: Vec<FieldTransform>,
}

impl SchemaMigration {
    /// Creates one validated migration contract.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::InvalidContract`] for a reserved
    /// collection, a non-forward version step (`to` not greater than
    /// `from`), a target schema whose version identity differs from `to`, a
    /// transform list longer than [`MAX_MIGRATION_TRANSFORMS`], an invalid
    /// field name, or a rename whose source and destination coincide;
    /// [`MigrationError::TargetSchemaMalformed`] for a target schema that
    /// fails the whole-definition semantics of the schema model.
    pub fn try_new(
        collection: CollectionName,
        from: SchemaVersion,
        to: SchemaVersion,
        target: SchemaDefinition,
        transforms: Vec<FieldTransform>,
    ) -> Result<Self, MigrationError> {
        if collection.as_str() == RESERVED_COLLECTION || to.get() <= from.get() {
            return Err(MigrationError::InvalidContract);
        }
        if target.version() != to || transforms.len() > MAX_MIGRATION_TRANSFORMS {
            return Err(MigrationError::InvalidContract);
        }
        for transform in &transforms {
            validate_field_name(transform.field()).map_err(|_| MigrationError::InvalidContract)?;
            if let Some(destination) = transform.destination_field() {
                validate_field_name(destination).map_err(|_| MigrationError::InvalidContract)?;
                if destination == transform.field() {
                    return Err(MigrationError::InvalidContract);
                }
            }
        }
        validate_schema(&target).map_err(|_| MigrationError::TargetSchemaMalformed)?;
        Ok(Self { collection, from, to, target, transforms })
    }

    /// Returns the migrated collection.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the version the collection is recorded at before the
    /// migration.
    #[must_use]
    pub fn from_version(&self) -> SchemaVersion {
        self.from
    }

    /// Returns the version the migration advances the collection to.
    #[must_use]
    pub fn to_version(&self) -> SchemaVersion {
        self.to
    }

    /// Returns the target schema the collection validates under after the
    /// migration.
    #[must_use]
    pub fn target(&self) -> &SchemaDefinition {
        &self.target
    }

    /// Returns the ordered declarative transform list.
    #[must_use]
    pub fn transforms(&self) -> &[FieldTransform] {
        &self.transforms
    }

    /// States the contract's rollback limit.
    #[must_use]
    pub fn rollback_limit(&self) -> RollbackLimit {
        let mut causes = Vec::new();
        for transform in &self.transforms {
            let cause = match transform {
                FieldTransform::RemoveField { .. } => RollbackBlockedCause::FieldRemoved,
                FieldTransform::AddField { .. } => RollbackBlockedCause::FieldDefaulted,
                FieldTransform::RenameField { .. } | FieldTransform::RetypeField { .. } => {
                    continue;
                }
            };
            if !causes.contains(&cause) {
                causes.push(cause);
            }
        }
        if causes.is_empty() {
            RollbackLimit::Reversible
        } else {
            RollbackLimit::NotReversible { causes }
        }
    }

    /// Returns the exact inverse transform list, in reverse order, when the
    /// contract's rollback limit is [`RollbackLimit::Reversible`].
    #[must_use]
    pub fn inverse_transforms(&self) -> Option<Vec<FieldTransform>> {
        if self.rollback_limit() != RollbackLimit::Reversible {
            return None;
        }
        Some(
            self.transforms
                .iter()
                .rev()
                .map(|transform| match transform {
                    FieldTransform::RenameField { from, to } => {
                        FieldTransform::RenameField { from: to.clone(), to: from.clone() }
                    }
                    FieldTransform::RetypeField { field, coercion } => {
                        FieldTransform::RetypeField {
                            field: field.clone(),
                            coercion: match coercion {
                                Coercion::IntegerToText => Coercion::TextToInteger,
                                Coercion::TextToInteger => Coercion::IntegerToText,
                                Coercion::IntegerToNumber => Coercion::NumberToInteger,
                                Coercion::NumberToInteger => Coercion::IntegerToNumber,
                            },
                        }
                    }
                    // The rollback limit computation refused these kinds; the
                    // inverse list only exists for the reversible vocabulary.
                    FieldTransform::AddField { .. } | FieldTransform::RemoveField { .. } => {
                        unreachable!("reversible contracts hold no add or remove transforms")
                    }
                })
                .collect(),
        )
    }
}

/// Per-kind transform application counts for migration evidence.
///
/// The counts are document-level applications, not value bytes: one renamed
/// field on one document counts once.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TransformCounts {
    /// Fields added with their default.
    pub added: u64,
    /// Fields removed with their stored values.
    pub removed: u64,
    /// Fields renamed.
    pub renamed: u64,
    /// Fields retyped under one closed coercion.
    pub retyped: u64,
}

/// Builds one object value from ordered field pairs.
pub(crate) fn object(fields: Vec<(&str, DocumentValue)>) -> DocumentValue {
    DocumentValue::Object(
        fields.into_iter().map(|(key, value)| (key.to_owned(), value)).collect::<BTreeMap<_, _>>(),
    )
}
