package terrabase

import (
	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

// ---------------------------------------------------------------------------
// Shared closed vocabularies
// ---------------------------------------------------------------------------

// ReadLevel is the read consistency level of one read or query. The
// zero value asks the engine to serve at its default level.
type ReadLevel uint8

// The read consistency levels.
const (
	ReadLevelDefault ReadLevel = iota // the engine's default level
	ReadLevelLocal
	ReadLevelRegionalQuorum
	ReadLevelGlobalQuorum
)

func (level ReadLevel) valid() bool { return level <= ReadLevelGlobalQuorum }

func (level ReadLevel) proto() v1.ReadLevel {
	switch level {
	case ReadLevelLocal:
		return v1.ReadLevel_READ_LEVEL_LOCAL
	case ReadLevelGlobalQuorum:
		return v1.ReadLevel_READ_LEVEL_GLOBAL_QUORUM
	default:
		// The engine's default discipline (regional quorum) is served
		// when the level is unspecified.
		return v1.ReadLevel_READ_LEVEL_REGIONAL_QUORUM
	}
}

// Acknowledgement is the acknowledgement discipline of one committed
// mutation. The zero value asks the engine for its default.
type Acknowledgement uint8

// The acknowledgement disciplines.
const (
	AckDefault Acknowledgement = iota // the engine's default discipline
	AckRegionalQuorum
	AckGlobalQuorum
	AckBackup
)

func (ack Acknowledgement) valid() bool { return ack <= AckBackup }

func (ack Acknowledgement) proto() v1.Acknowledgement {
	switch ack {
	case AckGlobalQuorum:
		return v1.Acknowledgement_ACKNOWLEDGEMENT_GLOBAL_QUORUM
	case AckBackup:
		return v1.Acknowledgement_ACKNOWLEDGEMENT_BACKUP
	default:
		return v1.Acknowledgement_ACKNOWLEDGEMENT_REGIONAL_QUORUM
	}
}

func acknowledgementFrom(ack v1.Acknowledgement) (Acknowledgement, bool) {
	switch ack {
	case v1.Acknowledgement_ACKNOWLEDGEMENT_REGIONAL_QUORUM:
		return AckRegionalQuorum, true
	case v1.Acknowledgement_ACKNOWLEDGEMENT_GLOBAL_QUORUM:
		return AckGlobalQuorum, true
	case v1.Acknowledgement_ACKNOWLEDGEMENT_BACKUP:
		return AckBackup, true
	default:
		return 0, false
	}
}

// BackupOutcome is whether one commit reached a backup tier.
type BackupOutcome uint8

// The backup outcomes.
const (
	BackupNone BackupOutcome = iota
	BackupFull
	BackupPartial
)

func backupOutcomeFrom(outcome v1.BackupOutcome) (BackupOutcome, bool) {
	switch outcome {
	case v1.BackupOutcome_BACKUP_OUTCOME_NONE:
		return BackupNone, true
	case v1.BackupOutcome_BACKUP_OUTCOME_FULL:
		return BackupFull, true
	case v1.BackupOutcome_BACKUP_OUTCOME_PARTIAL:
		return BackupPartial, true
	default:
		return 0, false
	}
}

// Expected is the expected-version discipline of one mutation: the
// compare that must hold for the write to commit. The zero value
// expects any version.
type Expected struct {
	kind     expectedKind
	exactLen uint64
}

type expectedKind uint8

const (
	expectedAny expectedKind = iota
	expectedAbsent
	expectedExact
)

// ExpectAbsent refuses the write unless the document does not yet
// exist: a create that fails on any prior version.
func ExpectAbsent() Expected { return Expected{kind: expectedAbsent} }

// ExpectAny commits regardless of the stored version: a blind put.
func ExpectAny() Expected { return Expected{} }

// ExpectExact commits only when the stored version equals version: the
// optimistic-concurrency compare-and-swap.
func ExpectExact(version uint64) Expected {
	return Expected{kind: expectedExact, exactLen: version}
}

func (expected Expected) proto() *v1.ExpectedVersion {
	switch expected.kind {
	case expectedAbsent:
		return &v1.ExpectedVersion{Discipline: &v1.ExpectedVersion_Absent{Absent: &v1.ExpectedAbsent{}}}
	case expectedExact:
		return &v1.ExpectedVersion{Discipline: &v1.ExpectedVersion_Exact{Exact: expected.exactLen}}
	default:
		return &v1.ExpectedVersion{Discipline: &v1.ExpectedVersion_Any{Any: &v1.ExpectedAny{}}}
	}
}

// CommitReceipt is the receipt of one committed mutation.
type CommitReceipt struct {
	Sequence    uint64 // the backbone sequence of the commit
	Version     uint64 // the resulting document version
	OperationID string // the operation identity the engine recorded
	Ack         Acknowledgement
	Backup      BackupOutcome
}

func commitReceiptFrom(receipt *v1.CommitReceipt) (CommitReceipt, error) {
	if receipt == nil {
		return CommitReceipt{}, contractError("the engine returned a committed outcome without a receipt")
	}
	ack, ok := acknowledgementFrom(receipt.GetAcknowledgement())
	if !ok {
		return CommitReceipt{}, contractError("the engine returned an unknown commit acknowledgement")
	}
	backup, ok := backupOutcomeFrom(receipt.GetBackup())
	if !ok {
		return CommitReceipt{}, contractError("the engine returned an unknown backup outcome")
	}
	if err := optionalName("operation id", receipt.GetOperationId(), maxOperationBytes); err != nil {
		return CommitReceipt{}, contractError("the engine returned an invalid commit operation id")
	}
	return CommitReceipt{
		Sequence:    receipt.GetSequence(),
		Version:     receipt.GetVersion(),
		OperationID: receipt.GetOperationId(),
		Ack:         ack,
		Backup:      backup,
	}, nil
}

// WriteReason is why a write was refused before commit.
type WriteReason uint8

// The write-refusal reasons.
const (
	WriteInvalidDocument WriteReason = iota + 1
	WriteSchemaRefused
	WriteNotFound
)

func writeReasonFrom(reason v1.WriteReason) (WriteReason, bool) {
	switch reason {
	case v1.WriteReason_WRITE_REASON_INVALID_DOCUMENT:
		return WriteInvalidDocument, true
	case v1.WriteReason_WRITE_REASON_SCHEMA_REFUSED:
		return WriteSchemaRefused, true
	case v1.WriteReason_WRITE_REASON_NOT_FOUND:
		return WriteNotFound, true
	default:
		return 0, false
	}
}

// String names the refusal reason.
func (reason WriteReason) String() string {
	switch reason {
	case WriteInvalidDocument:
		return "invalid_document"
	case WriteSchemaRefused:
		return "schema_refused"
	case WriteNotFound:
		return "not_found"
	default:
		return "unspecified"
	}
}

// WriteOutcome is the outcome of one document write, delete, or batch:
// committed, lost the expected-version race, or refused before commit.
// Conflicts and refusals are engine outcomes, not transport errors;
// test them with the accessors rather than the error return.
type WriteOutcome struct {
	commit   *CommitReceipt
	conflict bool
	reason   WriteReason
	refused  bool
}

// Committed reports the receipt when the mutation committed.
func (outcome WriteOutcome) Committed() (CommitReceipt, bool) {
	if outcome.commit == nil {
		return CommitReceipt{}, false
	}
	return *outcome.commit, true
}

// Conflict reports whether the mutation lost the expected-version race.
func (outcome WriteOutcome) Conflict() bool { return outcome.conflict }

// Refused reports the refusal reason when the engine refused the
// mutation before commit.
func (outcome WriteOutcome) Refused() (WriteReason, bool) { return outcome.reason, outcome.refused }

func writeOutcomeFrom(outcome *v1.WriteOutcome) (WriteOutcome, error) {
	if outcome == nil {
		return WriteOutcome{}, contractError("the engine returned no write outcome")
	}
	switch typed := outcome.GetOutcome().(type) {
	case *v1.WriteOutcome_Committed:
		receipt, err := commitReceiptFrom(typed.Committed)
		if err != nil {
			return WriteOutcome{}, err
		}
		return WriteOutcome{commit: &receipt}, nil
	case *v1.WriteOutcome_Conflict:
		if typed.Conflict == nil {
			return WriteOutcome{}, contractError("the engine returned an empty write conflict")
		}
		return WriteOutcome{conflict: true}, nil
	case *v1.WriteOutcome_Rejected:
		if typed.Rejected == nil {
			return WriteOutcome{}, contractError("the engine returned an empty write refusal")
		}
		reason, ok := writeReasonFrom(typed.Rejected.GetReason())
		if !ok {
			return WriteOutcome{}, contractError("the engine returned an unknown write refusal reason")
		}
		return WriteOutcome{reason: reason, refused: true}, nil
	default:
		return WriteOutcome{}, contractError("the engine returned a write outcome outside the contract")
	}
}

// ---------------------------------------------------------------------------
// Documents, queries, and families
// ---------------------------------------------------------------------------

// Document is one document as stored, plus the serving position of the
// read that produced it (filled by read and query paths).
type Document struct {
	ID         string
	Collection string
	Version    uint64
	Body       Value // object bodies decode as map[string]Value
	Position   uint64
}

func documentFrom(body *v1.DocumentBody) (Document, error) {
	if body == nil {
		return Document{}, localError("the engine sent a document carrying no body")
	}
	if err := boundName("document id", body.GetId(), maxDocumentBytes); err != nil {
		return Document{}, err
	}
	if err := boundName("collection", body.GetCollection(), maxCollectionBytes); err != nil {
		return Document{}, err
	}
	decoded, err := valueFromProto(body.GetBody())
	if err != nil {
		return Document{}, err
	}
	return Document{
		ID:         body.GetId(),
		Collection: body.GetCollection(),
		Version:    body.GetVersion(),
		Body:       decoded,
	}, nil
}

// DocumentPut identifies and bodies one document to put.
type DocumentPut struct {
	Collection string
	ID         string
	Body       Value
}

// BatchItem is one item of an atomic batch.
type BatchItem struct {
	Document    DocumentPut
	Expected    Expected
	OperationID string
}

// QueryResult is the outcome of one query execution.
type QueryResult struct {
	Documents []Document
	Count     int
	Truncated bool // the item bound cut the result short
	Position  uint64
}

// CancelOutcome is the outcome of one query cancellation.
type CancelOutcome uint8

// The cancellation outcomes.
const (
	Cancelled CancelOutcome = iota + 1
	AlreadyComplete
	UnknownQuery
)

func cancelOutcomeFrom(outcome v1.CancelOutcome) (CancelOutcome, bool) {
	switch outcome {
	case v1.CancelOutcome_CANCEL_OUTCOME_CANCELLED:
		return Cancelled, true
	case v1.CancelOutcome_CANCEL_OUTCOME_ALREADY_COMPLETE:
		return AlreadyComplete, true
	case v1.CancelOutcome_CANCEL_OUTCOME_UNKNOWN_QUERY:
		return UnknownQuery, true
	default:
		return 0, false
	}
}

// Family is the closed family-kind vocabulary of the family operations.
type Family string

// The closed family kinds.
const (
	FamilyAttachment        Family = "attachment"
	FamilyCounter           Family = "counter"
	FamilyTimeSeries        Family = "time_series"
	FamilyRevisions         Family = "revisions"
	FamilyCompareExchange   Family = "compare_exchange"
	FamilyIdentities        Family = "identities"
	FamilyExpirationRefresh Family = "expiration_refresh"
	FamilyArchival          Family = "archival"
)

// FamilyResponse is one family answer: the named codec and its payload.
type FamilyResponse struct {
	Codec   string
	Payload []byte
}

// ---------------------------------------------------------------------------
// Backbone subscriptions
// ---------------------------------------------------------------------------

// Start is where one subscription starts: the live edge or a fixed
// backbone sequence.
type Start struct {
	live     bool
	sequence uint64
}

// FromLive starts from the live edge.
func FromLive() Start { return Start{live: true} }

// FromSequence starts from one backbone sequence.
func FromSequence(sequence uint64) Start { return Start{sequence: sequence} }

func (start Start) proto() *v1.SubscriptionStart {
	if start.live {
		return &v1.SubscriptionStart{Start: &v1.SubscriptionStart_Live{Live: &v1.SubscriptionLive{}}}
	}
	return &v1.SubscriptionStart{Start: &v1.SubscriptionStart_Sequence{Sequence: start.sequence}}
}

// ChangeKind is the kind of one backbone change event.
type ChangeKind uint8

// The change kinds.
const (
	ChangePut ChangeKind = iota + 1
	ChangeDelete
)

func changeKindFrom(kind v1.ChangeKind) (ChangeKind, bool) {
	switch kind {
	case v1.ChangeKind_CHANGE_KIND_PUT:
		return ChangePut, true
	case v1.ChangeKind_CHANGE_KIND_DELETE:
		return ChangeDelete, true
	default:
		return 0, false
	}
}

// String names the change kind.
func (kind ChangeKind) String() string {
	switch kind {
	case ChangePut:
		return "put"
	case ChangeDelete:
		return "delete"
	default:
		return "unspecified"
	}
}

// ChangeEvent is one ordered backbone change event.
type ChangeEvent struct {
	Sequence    uint64
	OperationID string
	Kind        ChangeKind
	Collection  string
	Document    string
}

// Confirmation is the first frame of one subscription stream: the
// engine confirmed the subscription and names its start sequence.
type Confirmation struct {
	SubscriptionID uint64
	StartSequence  uint64
}

// Batch is one ordered, bounded batch of change events.
type Batch struct {
	SubscriptionID uint64
	FromSequence   uint64
	Events         []ChangeEvent
	NextSequence   uint64
}

// Trigger is the content-free change trigger: the client re-pulls.
type Trigger struct {
	SubscriptionID uint64
	Collection     string
	Sequence       uint64
	OperationID    string
}

// CloseReason is why a subscription closed.
type CloseReason uint8

// The subscription close reasons.
const (
	CloseReplaced CloseReason = iota + 1
	CloseShuttingDown
)

func closeReasonFrom(reason v1.SubscriptionCloseReason) (CloseReason, bool) {
	switch reason {
	case v1.SubscriptionCloseReason_SUBSCRIPTION_CLOSE_REASON_REPLACED:
		return CloseReplaced, true
	case v1.SubscriptionCloseReason_SUBSCRIPTION_CLOSE_REASON_SHUTTING_DOWN:
		return CloseShuttingDown, true
	default:
		return 0, false
	}
}

// Closed is the close frame of one subscription stream.
type Closed struct {
	SubscriptionID uint64
	Reason         CloseReason
}

// ---------------------------------------------------------------------------
// Sync sessions
// ---------------------------------------------------------------------------

// SyncSession is one established sync delivery session.
type SyncSession struct {
	ID            uint64
	Checkpoint    []byte
	RetainedFloor []byte
}

// DeltaPage is one page of a sync delta pull.
type DeltaPage struct {
	SessionID            uint64
	Codec                string
	Payload              []byte
	Next                 []byte
	Commits              uint32
	SingleCommitOversize bool
}

// Snapshot is the answer of one sync snapshot pull.
type Snapshot struct {
	SessionID  uint64
	Codec      string
	Payload    []byte
	Checkpoint []byte
}

// SyncChange is one sync-session mutation: a put of named fields or a
// delete.
type SyncChange struct {
	Delete bool
	Fields map[string]Value // put mutations only
}

// SyncPut builds the put of named fields.
func SyncPut(fields map[string]Value) SyncChange { return SyncChange{Fields: fields} }

// SyncDelete builds the delete.
func SyncDelete() SyncChange { return SyncChange{Delete: true} }

func (change SyncChange) proto() (*v1.SyncChange, error) {
	if change.Delete {
		return &v1.SyncChange{Change: &v1.SyncChange_Delete{Delete: &v1.SyncDelete{}}}, nil
	}
	if len(change.Fields) == 0 {
		return nil, localError("a sync put carries no fields")
	}
	if len(change.Fields) > maxMapEntries {
		return nil, localError("a sync put exceeds %d fields", maxMapEntries)
	}
	fields := make([]*v1.SyncField, 0, len(change.Fields))
	for name, value := range change.Fields {
		if name == "" || len(name) > maxNameBytes {
			return nil, localError("a sync field name must be 1..=%d bytes", maxNameBytes)
		}
		converted, err := valueToProto(value)
		if err != nil {
			return nil, localError("sync field %q: %s", name, localDetail(err))
		}
		fields = append(fields, &v1.SyncField{Name: name, Value: converted})
	}
	return &v1.SyncChange{Change: &v1.SyncChange_Put{Put: &v1.SyncPut{Fields: fields}}}, nil
}

// MutationReason is why a sync mutation was refused.
type MutationReason uint8

// The sync mutation refusal reasons.
const (
	MutationScopeRefused MutationReason = iota + 1
	MutationInvalidBody
	MutationSequenceOutOfOrder
)

func mutationReasonFrom(reason v1.MutationReason) (MutationReason, bool) {
	switch reason {
	case v1.MutationReason_MUTATION_REASON_SCOPE_REFUSED:
		return MutationScopeRefused, true
	case v1.MutationReason_MUTATION_REASON_INVALID_BODY:
		return MutationInvalidBody, true
	case v1.MutationReason_MUTATION_REASON_SEQUENCE_OUT_OF_ORDER:
		return MutationSequenceOutOfOrder, true
	default:
		return 0, false
	}
}

// String names the refusal reason.
func (reason MutationReason) String() string {
	switch reason {
	case MutationScopeRefused:
		return "scope_refused"
	case MutationInvalidBody:
		return "invalid_body"
	case MutationSequenceOutOfOrder:
		return "sequence_out_of_order"
	default:
		return "unspecified"
	}
}

// MutationOutcome is the outcome of one sync-session mutation.
type MutationOutcome struct {
	receipt *CommitReceipt
	reason  MutationReason
	refused bool
}

// Applied reports the receipt when the mutation committed.
func (outcome MutationOutcome) Applied() (CommitReceipt, bool) {
	if outcome.receipt == nil {
		return CommitReceipt{}, false
	}
	return *outcome.receipt, true
}

// Refused reports the refusal reason when the engine refused the
// mutation.
func (outcome MutationOutcome) Refused() (MutationReason, bool) {
	return outcome.reason, outcome.refused
}

func mutationOutcomeFrom(outcome *v1.MutationOutcome) (MutationOutcome, error) {
	if outcome == nil {
		return MutationOutcome{}, contractError("the engine returned no sync mutation outcome")
	}
	switch typed := outcome.GetOutcome().(type) {
	case *v1.MutationOutcome_Applied:
		receipt, err := commitReceiptFrom(typed.Applied)
		if err != nil {
			return MutationOutcome{}, err
		}
		return MutationOutcome{receipt: &receipt}, nil
	case *v1.MutationOutcome_Rejected:
		if typed.Rejected == nil {
			return MutationOutcome{}, contractError("the engine returned an empty sync mutation refusal")
		}
		reason, ok := mutationReasonFrom(typed.Rejected.GetReason())
		if !ok {
			return MutationOutcome{}, contractError("the engine returned an unknown sync mutation refusal reason")
		}
		return MutationOutcome{reason: reason, refused: true}, nil
	default:
		return MutationOutcome{}, contractError("the engine returned a sync mutation outcome outside the contract")
	}
}

// ---------------------------------------------------------------------------
// Schema and migrations
// ---------------------------------------------------------------------------

// SchemaInfo is the enforced state of one collection's schema.
type SchemaInfo struct {
	EnforcedVersion uint64
	RuleCount       uint32
}

// ViolationKind is the closed schema-violation vocabulary.
type ViolationKind uint8

// The schema violation kinds.
const (
	ViolationDocumentNotObject ViolationKind = iota + 1
	ViolationMissingRequiredField
	ViolationTypeMismatch
	ViolationValueNotAllowed
	ViolationNumberOutOfRange
	ViolationLengthOutOfRange
)

func violationKindFrom(kind v1.ViolationKind) ViolationKind {
	switch kind {
	case v1.ViolationKind_VIOLATION_KIND_DOCUMENT_NOT_OBJECT:
		return ViolationDocumentNotObject
	case v1.ViolationKind_VIOLATION_KIND_MISSING_REQUIRED_FIELD:
		return ViolationMissingRequiredField
	case v1.ViolationKind_VIOLATION_KIND_TYPE_MISMATCH:
		return ViolationTypeMismatch
	case v1.ViolationKind_VIOLATION_KIND_VALUE_NOT_ALLOWED:
		return ViolationValueNotAllowed
	case v1.ViolationKind_VIOLATION_KIND_NUMBER_OUT_OF_RANGE:
		return ViolationNumberOutOfRange
	case v1.ViolationKind_VIOLATION_KIND_LENGTH_OUT_OF_RANGE:
		return ViolationLengthOutOfRange
	default:
		return ViolationTypeMismatch
	}
}

// Violation is one field of a schema validation answer.
type Violation struct {
	Field string
	Kind  ViolationKind
}

// Validation is the answer of one schema validation.
type Validation struct {
	EnforcedVersion uint64
	Valid           bool
	Violations      []Violation
}

// TransformKind is the closed field-transform vocabulary.
type TransformKind uint8

// The field transform kinds.
const (
	TransformAddField TransformKind = iota + 1
	TransformRemoveField
	TransformRenameField
	TransformRetypeField
)

func (kind TransformKind) proto() (v1.FieldTransform, bool) {
	switch kind {
	case TransformAddField:
		return v1.FieldTransform_FIELD_TRANSFORM_ADD_FIELD, true
	case TransformRemoveField:
		return v1.FieldTransform_FIELD_TRANSFORM_REMOVE_FIELD, true
	case TransformRenameField:
		return v1.FieldTransform_FIELD_TRANSFORM_RENAME_FIELD, true
	case TransformRetypeField:
		return v1.FieldTransform_FIELD_TRANSFORM_RETYPE_FIELD, true
	default:
		return v1.FieldTransform_FIELD_TRANSFORM_UNSPECIFIED, false
	}
}

// Coercion is the closed coercion vocabulary of a retype transform.
type Coercion uint8

// The coercions.
const (
	CoercionIntegerToText Coercion = iota + 1
	CoercionTextToInteger
	CoercionIntegerToNumber
	CoercionNumberToInteger
)

func (coercion Coercion) proto() v1.Coercion {
	switch coercion {
	case CoercionTextToInteger:
		return v1.Coercion_COERCION_TEXT_TO_INTEGER
	case CoercionIntegerToNumber:
		return v1.Coercion_COERCION_INTEGER_TO_NUMBER
	case CoercionNumberToInteger:
		return v1.Coercion_COERCION_NUMBER_TO_INTEGER
	default:
		return v1.Coercion_COERCION_INTEGER_TO_TEXT
	}
}

// TransformRejection is why a transform literal was rejected.
type TransformRejection uint8

// The transform rejections.
const (
	RejectionValueNotCoercible TransformRejection = iota + 1
	RejectionTargetFieldOccupied
)

func transformRejectionFrom(rejection v1.TransformRejection) (TransformRejection, bool) {
	switch rejection {
	case v1.TransformRejection_TRANSFORM_REJECTION_VALUE_NOT_COERCIBLE:
		return RejectionValueNotCoercible, true
	case v1.TransformRejection_TRANSFORM_REJECTION_TARGET_FIELD_OCCUPIED:
		return RejectionTargetFieldOccupied, true
	default:
		return 0, false
	}
}

// Literal is the literal default of a retype transform.
type Literal struct {
	kind    literalKind
	boolean bool
	integer int64
	text    string
}

type literalKind uint8

const (
	literalBoolean literalKind = iota + 1
	literalInteger
	literalText
)

// BoolLiteral builds the boolean literal.
func BoolLiteral(value bool) Literal { return Literal{kind: literalBoolean, boolean: value} }

// IntLiteral builds the integer literal.
func IntLiteral(value int64) Literal { return Literal{kind: literalInteger, integer: value} }

// TextLiteral builds the text literal.
func TextLiteral(value string) Literal { return Literal{kind: literalText, text: value} }

func (literal Literal) proto() *v1.TransformLiteral {
	switch literal.kind {
	case literalBoolean:
		return &v1.TransformLiteral{Literal: &v1.TransformLiteral_Boolean{Boolean: literal.boolean}}
	case literalInteger:
		return &v1.TransformLiteral{Literal: &v1.TransformLiteral_Integer{Integer: literal.integer}}
	default:
		return &v1.TransformLiteral{Literal: &v1.TransformLiteral_Text{Text: literal.text}}
	}
}

// TransformStep is one step of a migration contract.
type TransformStep struct {
	Kind     TransformKind
	Field    string
	To       string // the rename or retype target
	Default  *Literal
	Coercion Coercion
}

// MigrationContract is the declared migration contract.
type MigrationContract struct {
	Collection  string
	FromVersion uint64
	ToVersion   uint64
	Target      Value
	Transforms  []TransformStep
}

func (contract MigrationContract) proto() (*v1.SchemaMigrationContract, error) {
	if len(contract.Transforms) > 64 {
		return nil, localError("a migration contract exceeds 64 transform steps")
	}
	target, err := valueToProto(contract.Target)
	if err != nil {
		return nil, localError("migration target: %s", localDetail(err))
	}
	steps := make([]*v1.FieldTransformStep, 0, len(contract.Transforms))
	for index, step := range contract.Transforms {
		kind, ok := step.Kind.proto()
		if !ok {
			return nil, localError("transform step %d carries no kind", index)
		}
		var literal *v1.TransformLiteral
		if step.Default != nil {
			literal = step.Default.proto()
		}
		steps = append(steps, &v1.FieldTransformStep{
			Kind:     kind,
			Field:    step.Field,
			To:       step.To,
			Default:  literal,
			Coercion: step.Coercion.proto(),
		})
	}
	return &v1.SchemaMigrationContract{
		Collection:  contract.Collection,
		FromVersion: contract.FromVersion,
		ToVersion:   contract.ToVersion,
		Target:      target,
		Transforms:  steps,
	}, nil
}

// PlanProblemKind is the closed plan-problem vocabulary.
type PlanProblemKind uint8

// The plan problem kinds.
const (
	ProblemTransformRejected PlanProblemKind = iota + 1
	ProblemTargetViolations
)

func planProblemKindFrom(kind v1.PlanProblemKind) PlanProblemKind {
	if kind == v1.PlanProblemKind_PLAN_PROBLEM_KIND_TARGET_VIOLATIONS {
		return ProblemTargetViolations
	}
	return ProblemTransformRejected
}

// PlanProblem is one problem of a migration plan.
type PlanProblem struct {
	Document     string
	Field        string
	Kind         PlanProblemKind
	Rejection    TransformRejection // set when Kind is ProblemTransformRejected
	HasRejection bool
	Violations   []Violation
}

// TransformCounts is the per-kind transform counts of one plan.
type TransformCounts struct {
	Added   uint64
	Removed uint64
	Renamed uint64
	Retyped uint64
}

func transformCountsFrom(counts *v1.TransformCounts) TransformCounts {
	if counts == nil {
		return TransformCounts{}
	}
	return TransformCounts{
		Added:   counts.GetAdded(),
		Removed: counts.GetRemoved(),
		Renamed: counts.GetRenamed(),
		Retyped: counts.GetRetyped(),
	}
}

// MigrationPlan is the dry-run plan of one migration.
type MigrationPlan struct {
	Manifest          string
	Collection        string
	FromVersion       uint64
	ToVersion         uint64
	Ready             bool
	Documents         uint64
	SourceDigest      string
	ResultDigest      string
	TransformCounts   TransformCounts
	Problems          []PlanProblem
	ProblemsTruncated bool
}

func migrationPlanFrom(plan *v1.MigrationPlan) MigrationPlan {
	converted := MigrationPlan{
		Manifest:          plan.GetManifest(),
		Collection:        plan.GetCollection(),
		FromVersion:       plan.GetFromVersion(),
		ToVersion:         plan.GetToVersion(),
		Ready:             plan.GetReady(),
		Documents:         plan.GetDocuments(),
		SourceDigest:      plan.GetSourceDigest(),
		ResultDigest:      plan.GetResultDigest(),
		TransformCounts:   transformCountsFrom(plan.GetTransformCounts()),
		ProblemsTruncated: plan.GetProblemsTruncated(),
	}
	for _, problem := range plan.GetProblems() {
		item := PlanProblem{
			Document: problem.GetDocument(),
			Field:    problem.GetField(),
			Kind:     planProblemKindFrom(problem.GetKind()),
		}
		item.Rejection, item.HasRejection = transformRejectionFrom(problem.GetRejection())
		for _, violation := range problem.GetViolations() {
			item.Violations = append(item.Violations, Violation{
				Field: violation.GetField(),
				Kind:  violationKindFrom(violation.GetKind()),
			})
		}
		converted.Problems = append(converted.Problems, item)
	}
	return converted
}

// RollbackLimit is the closed rollback-limit vocabulary.
type RollbackLimit uint8

// The rollback limits.
const (
	RollbackReversible RollbackLimit = iota + 1
	RollbackNotReversible
)

func rollbackLimitFrom(limit v1.RollbackLimitKind) RollbackLimit {
	if limit == v1.RollbackLimitKind_ROLLBACK_LIMIT_KIND_NOT_REVERSIBLE {
		return RollbackNotReversible
	}
	return RollbackReversible
}

// MigrationReceipt is the receipt of one applied migration.
type MigrationReceipt struct {
	Collection                  string
	Manifest                    string
	FromVersion                 uint64
	ToVersion                   uint64
	SourceDigest                string
	ResultDigest                string
	Documents                   uint64
	Transformed                 uint64
	TransformCounts             TransformCounts
	Pages                       uint64
	RollbackLimit               RollbackLimit
	AcknowledgedNotRollbackable bool
	AlreadyApplied              bool
}

// MigrationStateKind is the closed migration-state vocabulary.
type MigrationStateKind uint8

// The migration states.
const (
	MigrationNone MigrationStateKind = iota + 1
	MigrationInProgress
	MigrationRollingBack
	MigrationComplete
)

func migrationStateKindFrom(kind v1.MigrationStateKind) MigrationStateKind {
	switch kind {
	case v1.MigrationStateKind_MIGRATION_STATE_KIND_IN_PROGRESS:
		return MigrationInProgress
	case v1.MigrationStateKind_MIGRATION_STATE_KIND_ROLLING_BACK:
		return MigrationRollingBack
	case v1.MigrationStateKind_MIGRATION_STATE_KIND_COMPLETE:
		return MigrationComplete
	default:
		return MigrationNone
	}
}

// MigrationState is the state of one collection's migration.
type MigrationState struct {
	Kind        MigrationStateKind
	Manifest    string
	FromVersion uint64
	ToVersion   uint64
	Documents   uint64
	Transformed uint64
	Pages       uint64
}

// ---------------------------------------------------------------------------
// Replication observation
// ---------------------------------------------------------------------------

// ClockStamp is one causal-clock stamp of a conflict participant.
type ClockStamp struct {
	Ticks   uint64
	Counter uint64
}

// ConflictOutcomeKind is the closed conflict-outcome vocabulary.
type ConflictOutcomeKind uint8

// The conflict outcome kinds.
const (
	ConflictWinner ConflictOutcomeKind = iota + 1
	ConflictDeposedOwnerRetained
	ConflictUntrustedClock
)

func conflictOutcomeKindFrom(kind v1.ConflictOutcomeKind) ConflictOutcomeKind {
	switch kind {
	case v1.ConflictOutcomeKind_CONFLICT_OUTCOME_KIND_WINNER:
		return ConflictWinner
	case v1.ConflictOutcomeKind_CONFLICT_OUTCOME_KIND_DEPOSED_OWNER_RETAINED:
		return ConflictDeposedOwnerRetained
	default:
		return ConflictUntrustedClock
	}
}

// ConflictOutcome is the outcome of one resolved conflict.
type ConflictOutcome struct {
	Kind   ConflictOutcomeKind
	Member string
}

// ConflictRecord is one observed conflict.
type ConflictRecord struct {
	Tenant     string
	Operations []string
	Members    []string
	Clocks     []ClockStamp
	Outcome    ConflictOutcome
}

// ConflictsReport is the replication conflicts of one member.
type ConflictsReport struct {
	Member  string
	Records []ConflictRecord
}

// FencedRecord is one observed fenced range.
type FencedRecord struct {
	Range        string
	BatchEpoch   uint64
	HighestEpoch uint64
	Source       string
}

// FencedReport is the fenced ranges of one member.
type FencedReport struct {
	Member  string
	Records []FencedRecord
}

// DepositionRecord is one observed leadership deposition.
type DepositionRecord struct {
	Range      string
	HeldEpoch  uint64
	OwnerEpoch uint64
	NewOwner   string
}

// DepositionsReport is the leadership depositions of one member.
type DepositionsReport struct {
	Member  string
	Records []DepositionRecord
}

// Cursor is the replication position of one member.
type Cursor struct {
	Member   string
	Position uint64
}

// ReplicationStatus is the closed replication-status vocabulary.
type ReplicationStatus uint8

// The replication statuses.
const (
	ReplicationAssigned ReplicationStatus = iota + 1
	ReplicationActive
	ReplicationResponsive
	ReplicationStale
	ReplicationUnavailable
	ReplicationDegraded
	ReplicationUnknown
)

func replicationStatusFrom(status v1.ReplicationStatus) ReplicationStatus {
	switch status {
	case v1.ReplicationStatus_REPLICATION_STATUS_ASSIGNED:
		return ReplicationAssigned
	case v1.ReplicationStatus_REPLICATION_STATUS_ACTIVE:
		return ReplicationActive
	case v1.ReplicationStatus_REPLICATION_STATUS_RESPONSIVE:
		return ReplicationResponsive
	case v1.ReplicationStatus_REPLICATION_STATUS_STALE:
		return ReplicationStale
	case v1.ReplicationStatus_REPLICATION_STATUS_UNAVAILABLE:
		return ReplicationUnavailable
	case v1.ReplicationStatus_REPLICATION_STATUS_DEGRADED:
		return ReplicationDegraded
	default:
		return ReplicationUnknown
	}
}

// String names the status.
func (status ReplicationStatus) String() string {
	switch status {
	case ReplicationAssigned:
		return "assigned"
	case ReplicationActive:
		return "active"
	case ReplicationResponsive:
		return "responsive"
	case ReplicationStale:
		return "stale"
	case ReplicationUnavailable:
		return "unavailable"
	case ReplicationDegraded:
		return "degraded"
	default:
		return "unknown"
	}
}

// ReplicationHealth is the serving health of one replication member.
type ReplicationHealth struct {
	Member string
	Status ReplicationStatus
}

// DenialSeam is which seam observed a cross-tenant denial.
type DenialSeam uint8

// The denial seams.
const (
	SeamServeRequest DenialSeam = iota + 1
	SeamApplyDelivery
)

func denialSeamFrom(seam v1.DenialSeam) DenialSeam {
	if seam == v1.DenialSeam_DENIAL_SEAM_APPLY_DELIVERY {
		return SeamApplyDelivery
	}
	return SeamServeRequest
}

// DenialRecord is one observed cross-tenant denial.
type DenialRecord struct {
	Seam            DenialSeam
	ScopeTenant     string
	AttemptedTenant string
	Counterparty    string
}

// DenialsReport is the cross-tenant denials of one member.
type DenialsReport struct {
	Member  string
	Records []DenialRecord
}

// ---------------------------------------------------------------------------
// Topology, administration, supervision
// ---------------------------------------------------------------------------

// BindingKind is the transport a binding serves.
type BindingKind uint8

// The binding kinds.
const (
	BindingLocal BindingKind = iota + 1
	BindingNetworkMTLS
)

func bindingKindFrom(kind v1.BindingKind) BindingKind {
	if kind == v1.BindingKind_BINDING_KIND_LOCAL {
		return BindingLocal
	}
	return BindingNetworkMTLS
}

// ClusterMode is the cluster mode of the serving topology.
type ClusterMode uint8

// The cluster modes.
const (
	ClusterStandalone ClusterMode = iota + 1
	ClusterActive
	ClusterBackup
)

func clusterModeFrom(mode v1.ClusterMode) ClusterMode {
	switch mode {
	case v1.ClusterMode_CLUSTER_MODE_ACTIVE:
		return ClusterActive
	case v1.ClusterMode_CLUSTER_MODE_BACKUP:
		return ClusterBackup
	default:
		return ClusterStandalone
	}
}

// Topology is the answer of one topology read.
type Topology struct {
	Binding                 BindingKind
	Subsystems              []string
	EngineTopologyAvailable bool
	ClusterMode             ClusterMode
}

// AdminSnapshot is the administrative status of the serving instance.
type AdminSnapshot struct {
	RegistryVersion string
	FramesServed    uint64
	Sessions        uint32
	Subscriptions   uint32
	Scopes          uint32
}

// HealthState is the serving health of the engine.
type HealthState uint8

// The health states.
const (
	HealthStarting HealthState = iota + 1
	HealthServing
	HealthDraining
	HealthStopped
)

func healthStateFrom(state v1.HealthState) HealthState {
	switch state {
	case v1.HealthState_HEALTH_STATE_STARTING:
		return HealthStarting
	case v1.HealthState_HEALTH_STATE_DRAINING:
		return HealthDraining
	case v1.HealthState_HEALTH_STATE_STOPPED:
		return HealthStopped
	case v1.HealthState_HEALTH_STATE_SERVING:
		return HealthServing
	default:
		return 0
	}
}

// String names the state.
func (state HealthState) String() string {
	switch state {
	case HealthStarting:
		return "starting"
	case HealthServing:
		return "serving"
	case HealthDraining:
		return "draining"
	case HealthStopped:
		return "stopped"
	default:
		return "unspecified"
	}
}

// SupervisionReport is the answer of one supervision health read.
type SupervisionReport struct {
	State         HealthState
	Sessions      uint32
	Subscriptions uint32
	FramesServed  uint64
}

// Readiness is the gateway-facing admission answer derived from the engine's
// typed supervision state. Ready is true only while the engine is serving.
type Readiness struct {
	Ready  bool
	Report SupervisionReport
}

// ShutdownReason is why the engine was asked to shut down.
type ShutdownReason uint8

// The shutdown reasons.
const (
	ShutdownOperator ShutdownReason = iota + 1
	ShutdownSupervisor
)

func (reason ShutdownReason) proto() (v1.ShutdownReason, bool) {
	switch reason {
	case ShutdownOperator:
		return v1.ShutdownReason_SHUTDOWN_REASON_OPERATOR, true
	case ShutdownSupervisor:
		return v1.ShutdownReason_SHUTDOWN_REASON_SUPERVISOR, true
	default:
		return v1.ShutdownReason_SHUTDOWN_REASON_UNSPECIFIED, false
	}
}

// ShutdownReceipt is the answer of one supervision shutdown request.
type ShutdownReceipt struct {
	State    HealthState
	InFlight uint32
}

// ---------------------------------------------------------------------------
// Tier administration
// ---------------------------------------------------------------------------

// TierPolicy is one tier policy configuration.
type TierPolicy struct {
	PolicyID           string
	Version            uint32
	NVMeBudgetBytes    uint64
	MinResidenceMoves  uint32
	PromotionThreshold uint32
	PinnedSegmentIDs   []uint64
}

// TierPolicyStatus is the receipt of one tier configuration.
type TierPolicyStatus struct {
	PolicyID string
	Version  uint32
	Paused   bool
}

// TierStatus is the answer of one tier inspect.
type TierStatus struct {
	Configured    bool
	Paused        bool
	ManifestEpoch uint64
	ObjectCount   uint32
	NVMeBytes     uint64
}

// TierDecision is the tier administration decision vocabulary.
type TierDecision uint8

// The tier decisions.
const (
	TierPromote TierDecision = iota + 1
	TierDemote
	TierSplit
	TierDecisionNone
	TierRefused
)

func tierDecisionFrom(decision v1.TierDecision) TierDecision {
	switch decision {
	case v1.TierDecision_TIER_DECISION_PROMOTE:
		return TierPromote
	case v1.TierDecision_TIER_DECISION_DEMOTE:
		return TierDemote
	case v1.TierDecision_TIER_DECISION_SPLIT:
		return TierSplit
	case v1.TierDecision_TIER_DECISION_REFUSED:
		return TierRefused
	default:
		return TierDecisionNone
	}
}

// TierMoveReason is the cause of one tier move decision.
type TierMoveReason struct {
	Fact     string
	Observed string
}

// TierMove is the receipt of one tier policy evaluation or move.
type TierMove struct {
	PolicyID         string
	PolicyVersion    uint32
	Decision         TierDecision
	Reasons          []TierMoveReason
	SegmentID        uint64
	Bytes            uint64
	ManifestBefore   uint64
	ManifestAfter    uint64
	DryRun           bool
	ManifestAccepted bool
}

func tierMoveFrom(receipt *v1.TierMoveReceipt) TierMove {
	converted := TierMove{
		PolicyID:         receipt.GetPolicyId(),
		PolicyVersion:    receipt.GetPolicyVersion(),
		Decision:         tierDecisionFrom(receipt.GetDecision()),
		SegmentID:        receipt.GetSegmentId(),
		Bytes:            receipt.GetBytes(),
		ManifestBefore:   receipt.GetManifestBefore(),
		ManifestAfter:    receipt.GetManifestAfter(),
		DryRun:           receipt.GetDryRun(),
		ManifestAccepted: receipt.GetManifestAccepted(),
	}
	for _, reason := range receipt.GetReasons() {
		converted.Reasons = append(converted.Reasons, TierMoveReason{
			Fact:     reason.GetFact(),
			Observed: reason.GetObserved(),
		})
	}
	return converted
}
