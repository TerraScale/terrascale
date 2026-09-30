package sdk

import (
	"bytes"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"slices"
	"strconv"
)

// DocumentScope identifies one shard. Shard retains the complete u64 range.
type DocumentScope struct {
	Tenant   UUID       `json:"tenant"`
	Database UUID       `json:"database"`
	Shard    DecimalU64 `json:"shard"`
}

func (s DocumentScope) validate() error {
	for _, id := range []UUID{s.Tenant, s.Database} {
		if _, err := ParseUUID(string(id)); err != nil {
			return err
		}
	}
	return nil
}
func (s DocumentScope) path() string {
	return "/v1/tenants/" + string(s.Tenant) + "/databases/" + string(s.Database) + "/shards/" + strconv.FormatUint(uint64(s.Shard), 10)
}

type TransactionID struct {
	ExpiresAtUnixMS DecimalU64 `json:"expires_at_unix_ms"`
	Nonce           Hex16      `json:"nonce"`
}

// NewTransactionID allocates a cryptographic nonce. Keep the whole identity
// unchanged when checking status or retrying an uncertain transaction.
func NewTransactionID(expiresAtUnixMS uint64) (TransactionID, error) {
	id := TransactionID{ExpiresAtUnixMS: DecimalU64(expiresAtUnixMS)}
	_, err := rand.Read(id.Nonce[:])
	return id, err
}

type WriterIdentity struct {
	Replica     UUID  `json:"replica"`
	Incarnation Hex16 `json:"incarnation"`
}
type AuthorityMode string

const (
	AuthorityLeaderless   AuthorityMode = "leaderless"
	AuthoritySingleWriter AuthorityMode = "single_writer"
)

type MutationAuthority struct {
	Mode       AuthorityMode   `json:"mode"`
	Generation DecimalU64      `json:"generation,omitzero"`
	Writer     *WriterIdentity `json:"writer,omitempty"`
}

func (a MutationAuthority) validate() error {
	switch a.Mode {
	case AuthorityLeaderless:
		if a.Generation != 0 || a.Writer != nil {
			return fmt.Errorf("sdk: leaderless authority has extra members")
		}
	case AuthoritySingleWriter:
		if a.Generation == 0 || a.Writer == nil {
			return fmt.Errorf("sdk: single writer requires positive generation and writer")
		}
		if _, err := ParseUUID(string(a.Writer.Replica)); err != nil {
			return err
		}
	default:
		return fmt.Errorf("sdk: invalid authority mode")
	}
	return nil
}
func (a MutationAuthority) MarshalJSON() ([]byte, error) {
	if err := a.validate(); err != nil {
		return nil, err
	}
	type wire MutationAuthority
	return json.Marshal(wire(a))
}
func (a *MutationAuthority) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, []string{"mode"}, []string{"generation", "writer"})
	if err != nil {
		return err
	}
	var mode AuthorityMode
	if err := json.Unmarshal(object["mode"], &mode); err != nil {
		return err
	}
	required := []string{"mode"}
	if mode == AuthoritySingleWriter {
		required = append(required, "generation", "writer")
	}
	if _, err := nativeObject(data, required, nil); err != nil {
		return err
	}
	type wire MutationAuthority
	var value wire
	if err := decodeNativeJSON(data, &value); err != nil {
		return err
	}
	if err := MutationAuthority(value).validate(); err != nil {
		return err
	}
	*a = MutationAuthority(value)
	return nil
}

type ConditionKind string

const (
	ConditionNone    ConditionKind = "none"
	ConditionAbsent  ConditionKind = "absent"
	ConditionVersion ConditionKind = "version"
)

type DocumentCondition struct {
	Kind    ConditionKind `json:"kind"`
	Version *Hex32        `json:"version,omitempty"`
}

func (c DocumentCondition) validate() error {
	if (c.Kind == ConditionNone || c.Kind == ConditionAbsent) && c.Version == nil || c.Kind == ConditionVersion && c.Version != nil {
		return nil
	}
	return fmt.Errorf("sdk: invalid document condition")
}
func (c DocumentCondition) MarshalJSON() ([]byte, error) {
	if err := c.validate(); err != nil {
		return nil, err
	}
	type wire DocumentCondition
	return json.Marshal(wire(c))
}
func (c *DocumentCondition) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, []string{"kind"}, []string{"version"})
	if err != nil {
		return err
	}
	var kind ConditionKind
	if err := json.Unmarshal(object["kind"], &kind); err != nil {
		return err
	}
	required := []string{"kind"}
	if kind == ConditionVersion {
		required = append(required, "version")
	}
	if _, err := nativeObject(data, required, nil); err != nil {
		return err
	}
	type wire DocumentCondition
	var value wire
	if err := decodeNativeJSON(data, &value); err != nil {
		return err
	}
	if err := DocumentCondition(value).validate(); err != nil {
		return err
	}
	*c = DocumentCondition(value)
	return nil
}

type Observed struct {
	Replica     UUID       `json:"replica"`
	Incarnation Hex16      `json:"incarnation"`
	Sequence    DecimalU64 `json:"sequence"`
}

func compareObserved(a, b Observed) int {
	if a.Replica < b.Replica {
		return -1
	}
	if a.Replica > b.Replica {
		return 1
	}
	return bytes.Compare(a.Incarnation[:], b.Incarnation[:])
}
func validateObserved(context []Observed) error {
	if len(context) > 1024 {
		return fmt.Errorf("sdk: native context limit")
	}
	for i, dot := range context {
		if _, err := ParseUUID(string(dot.Replica)); err != nil {
			return err
		}
		if dot.Sequence == 0 || i > 0 && compareObserved(context[i-1], dot) >= 0 {
			return fmt.Errorf("sdk: context requires sorted unique origins and positive sequences")
		}
	}
	return nil
}

type WriteValue struct {
	Kind   string          `json:"kind"`
	Fields []DocumentField `json:"fields,omitempty"`
}

func (v WriteValue) validate() error {
	if v.Kind == "tombstone" {
		if v.Fields != nil {
			return fmt.Errorf("sdk: tombstone cannot contain fields")
		}
		return nil
	}
	if v.Kind != "live" {
		return fmt.Errorf("sdk: invalid native write value")
	}
	if len(v.Fields) > 1024 {
		return fmt.Errorf("sdk: native field limit")
	}
	nodes := 0
	total := 98
	var previous uint32
	for _, field := range v.Fields {
		if field.ID == 0 || field.ID <= previous {
			return fmt.Errorf("sdk: native fields require sorted unique positive IDs")
		}
		previous = field.ID
		_, payload, err := field.Value.encode(1, &nodes)
		if err != nil {
			return err
		}
		total += 13 + len(payload)
		if total > 1<<20 {
			return fmt.Errorf("sdk: encoded native document exceeds 1 MiB")
		}
	}
	return nil
}
func (v WriteValue) MarshalJSON() ([]byte, error) {
	if err := v.validate(); err != nil {
		return nil, err
	}
	if v.Kind == "tombstone" {
		return []byte(`{"kind":"tombstone"}`), nil
	}
	return json.Marshal(struct {
		Kind   string          `json:"kind"`
		Fields []DocumentField `json:"fields"`
	}{"live", nonnil(v.Fields)})
}
func (v *WriteValue) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, []string{"kind"}, []string{"fields"})
	if err != nil {
		return err
	}
	var kind string
	if err := json.Unmarshal(object["kind"], &kind); err != nil {
		return err
	}
	required := []string{"kind"}
	if kind == "live" {
		required = append(required, "fields")
	}
	if _, err := nativeObject(data, required, nil); err != nil {
		return err
	}
	value := WriteValue{Kind: kind}
	if kind == "live" {
		if err := decodeNativeJSON(object["fields"], &value.Fields); err != nil {
			return err
		}
	}
	if err := value.validate(); err != nil {
		return err
	}
	*v = value
	return nil
}

type DocumentWrite struct {
	Collection       UUID              `json:"collection"`
	Document         UUID              `json:"document"`
	SchemaGeneration DecimalU64        `json:"schema_generation"`
	SchemaDigest     Hex32             `json:"schema_digest"`
	Condition        DocumentCondition `json:"condition"`
	Observed         []Observed        `json:"observed"`
	Value            WriteValue        `json:"value"`
}
type CommitRequest struct {
	Version       uint8             `json:"version"`
	TransactionID TransactionID     `json:"transaction_id"`
	Authority     MutationAuthority `json:"authority"`
	EffectDigest  Hex32             `json:"effect_digest"`
	Writes        []DocumentWrite   `json:"writes"`
}

// NewCommitRequest owns and sorts writes, fields and observed origins before
// computing the producer's canonical effect digest. Duplicate identities fail.
func NewCommitRequest(scope DocumentScope, id TransactionID, authority MutationAuthority, writes []DocumentWrite) (CommitRequest, error) {
	request := CommitRequest{Version: 1, TransactionID: id, Authority: authority, Writes: slices.Clone(writes)}
	if authority.Writer != nil {
		writer := *authority.Writer
		request.Authority.Writer = &writer
	}
	for i := range request.Writes {
		write := &request.Writes[i]
		write.Observed = nonnil(slices.Clone(write.Observed))
		slices.SortFunc(write.Observed, compareObserved)
		write.Value.Fields = slices.Clone(write.Value.Fields)
		if write.Value.Kind == "live" {
			write.Value.Fields = nonnil(write.Value.Fields)
		}
		slices.SortFunc(write.Value.Fields, func(a, b DocumentField) int {
			if a.ID < b.ID {
				return -1
			}
			if a.ID > b.ID {
				return 1
			}
			return 0
		})
		if write.Condition.Version != nil {
			version := *write.Condition.Version
			write.Condition.Version = &version
		}
	}
	slices.SortFunc(request.Writes, compareWrites)
	digest, err := CanonicalEffectDigest(scope, request.Authority, request.Writes)
	request.EffectDigest = digest
	return request, err
}
func compareWrites(a, b DocumentWrite) int {
	if a.Collection < b.Collection {
		return -1
	}
	if a.Collection > b.Collection {
		return 1
	}
	if a.Document < b.Document {
		return -1
	}
	if a.Document > b.Document {
		return 1
	}
	return 0
}

// CanonicalEffectBytes implements TB-DOC-EFFECT-v1, including recursively
// encoded tb-format values. Transaction identity is deliberately excluded.
func CanonicalEffectBytes(scope DocumentScope, authority MutationAuthority, writes []DocumentWrite) ([]byte, error) {
	if err := scope.validate(); err != nil {
		return nil, err
	}
	if err := authority.validate(); err != nil {
		return nil, err
	}
	if len(writes) == 0 || len(writes) > 1024 {
		return nil, fmt.Errorf("sdk: transaction requires 1..1024 writes")
	}
	encoded := append([]byte("TB-DOC-EFFECT-v1\x00"), scope.Tenant.binary()...)
	encoded = append(encoded, scope.Database.binary()...)
	encoded = binary.LittleEndian.AppendUint64(encoded, uint64(scope.Shard))
	if authority.Mode == AuthorityLeaderless {
		encoded = append(encoded, 0)
	} else {
		encoded = append(encoded, 1)
		encoded = binary.LittleEndian.AppendUint64(encoded, uint64(authority.Generation))
		encoded = append(encoded, authority.Writer.Replica.binary()...)
		encoded = append(encoded, authority.Writer.Incarnation[:]...)
	}
	encoded = binary.LittleEndian.AppendUint64(encoded, uint64(len(writes)))
	for i, write := range writes {
		for _, id := range []UUID{write.Collection, write.Document} {
			if _, err := ParseUUID(string(id)); err != nil {
				return nil, err
			}
		}
		if write.SchemaGeneration == 0 || i > 0 && compareWrites(writes[i-1], write) >= 0 {
			return nil, fmt.Errorf("sdk: writes require positive schemas and sorted unique document identities")
		}
		if err := write.Condition.validate(); err != nil {
			return nil, err
		}
		if authority.Mode == AuthoritySingleWriter && write.Condition.Kind == ConditionNone {
			return nil, fmt.Errorf("sdk: single writer requires an explicit condition")
		}
		if err := validateObserved(write.Observed); err != nil {
			return nil, err
		}
		if err := write.Value.validate(); err != nil {
			return nil, err
		}
		encoded = append(encoded, write.Collection.binary()...)
		encoded = append(encoded, write.Document.binary()...)
		encoded = binary.LittleEndian.AppendUint64(encoded, uint64(write.SchemaGeneration))
		encoded = append(encoded, write.SchemaDigest[:]...)
		switch write.Condition.Kind {
		case ConditionNone:
			encoded = append(encoded, 0)
		case ConditionAbsent:
			encoded = append(encoded, 1)
		case ConditionVersion:
			encoded = append(encoded, 2)
			encoded = append(encoded, write.Condition.Version[:]...)
		}
		encoded = binary.LittleEndian.AppendUint64(encoded, uint64(len(write.Observed)))
		for _, dot := range write.Observed {
			encoded = append(encoded, dot.Replica.binary()...)
			encoded = append(encoded, dot.Incarnation[:]...)
			encoded = binary.LittleEndian.AppendUint64(encoded, uint64(dot.Sequence))
		}
		if write.Value.Kind == "tombstone" {
			encoded = append(encoded, 0)
		} else {
			encoded = append(encoded, 1)
			encoded = binary.LittleEndian.AppendUint64(encoded, uint64(len(write.Value.Fields)))
			nodes := 0
			for _, field := range write.Value.Fields {
				tag, value, err := field.Value.encode(1, &nodes)
				if err != nil {
					return nil, err
				}
				encoded = binary.LittleEndian.AppendUint32(encoded, field.ID)
				encoded = append(encoded, tag)
				encoded = binary.LittleEndian.AppendUint64(encoded, uint64(len(value)))
				encoded = append(encoded, value...)
			}
		}
		if len(encoded) > MaxNativeBodyBytes {
			return nil, fmt.Errorf("sdk: transaction canonical byte limit")
		}
	}
	return encoded, nil
}
func CanonicalEffectDigest(scope DocumentScope, authority MutationAuthority, writes []DocumentWrite) (Hex32, error) {
	encoded, err := CanonicalEffectBytes(scope, authority, writes)
	if err != nil {
		return Hex32{}, err
	}
	return Hex32(sha256.Sum256(encoded)), nil
}
func (r CommitRequest) validate(scope DocumentScope) error {
	if r.Version != 1 {
		return fmt.Errorf("sdk: native request version must be 1")
	}
	digest, err := CanonicalEffectDigest(scope, r.Authority, r.Writes)
	if err != nil {
		return err
	}
	if digest != r.EffectDigest {
		return fmt.Errorf("sdk: transaction effect digest does not match scope and effects")
	}
	return nil
}

type DocumentEvent struct {
	Database    UUID       `json:"database"`
	Shard       DecimalU64 `json:"shard"`
	Replica     UUID       `json:"replica"`
	Incarnation Hex16      `json:"incarnation"`
	Sequence    DecimalU64 `json:"sequence"`
}

func (e DocumentEvent) validate(scope DocumentScope) error {
	if e.Database != scope.Database || e.Shard != scope.Shard || e.Sequence == 0 || e.Incarnation == (Hex16{}) {
		return fmt.Errorf("sdk: receipt event does not match native scope or sequence")
	}
	_, err := ParseUUID(string(e.Replica))
	return err
}

type Durability string

const (
	DurabilityLocal  Durability = "local_durable"
	DurabilityObject Durability = "object_durable"
)

type CommitResponse struct {
	Version        uint8             `json:"version"`
	TransactionID  TransactionID     `json:"transaction_id"`
	Authority      MutationAuthority `json:"authority"`
	EffectDigest   Hex32             `json:"effect_digest"`
	Event          DocumentEvent     `json:"event"`
	CommitSequence DecimalU64        `json:"commit_sequence"`
	Durability     Durability        `json:"durability"`
}

func sameAuthority(a, b MutationAuthority) bool {
	if a.Mode != b.Mode || a.Generation != b.Generation || (a.Writer == nil) != (b.Writer == nil) {
		return false
	}
	return a.Writer == nil || *a.Writer == *b.Writer
}
func (r CommitResponse) validate(scope DocumentScope, request TransactionStatusRequest) error {
	if r.Version != 1 || r.TransactionID != request.TransactionID || r.EffectDigest != request.EffectDigest || !sameAuthority(r.Authority, request.Authority) || r.CommitSequence == 0 || (r.Durability != DurabilityLocal && r.Durability != DurabilityObject) {
		return fmt.Errorf("sdk: invalid or mismatched native commit receipt")
	}
	return r.Event.validate(scope)
}

type TransactionStatusRequest struct {
	Version       uint8             `json:"version"`
	TransactionID TransactionID     `json:"transaction_id"`
	Authority     MutationAuthority `json:"authority"`
	EffectDigest  Hex32             `json:"effect_digest"`
}

func (r CommitRequest) StatusRequest() TransactionStatusRequest {
	return TransactionStatusRequest{Version: 1, TransactionID: r.TransactionID, Authority: r.Authority, EffectDigest: r.EffectDigest}
}

type TransactionState string

const (
	TransactionCommitted TransactionState = "committed"
	TransactionRejected  TransactionState = "rejected"
	TransactionPending   TransactionState = "pending"
	TransactionUnknown   TransactionState = "unknown"
	TransactionExpired   TransactionState = "expired"
)

type TransactionRejection struct {
	Code string `json:"code"`
}
type TransactionOutcome struct {
	Receipt *CommitResponse       `json:"receipt,omitempty"`
	Error   *TransactionRejection `json:"error,omitempty"`
}

func (o *TransactionOutcome) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, nil, []string{"receipt", "error"})
	if err != nil {
		return err
	}
	if len(object) != 1 {
		return fmt.Errorf("sdk: transaction outcome requires exactly one variant")
	}
	for _, raw := range object {
		if bytes.Equal(bytes.TrimSpace(raw), []byte("null")) {
			return fmt.Errorf("sdk: transaction outcome variant cannot be null")
		}
	}
	type wire TransactionOutcome
	var value wire
	if err := decodeNativeJSON(data, &value); err != nil {
		return err
	}
	*o = TransactionOutcome(value)
	return nil
}

type TransactionStatusResponse struct {
	Version       uint8               `json:"version"`
	State         TransactionState    `json:"state"`
	TransactionID TransactionID       `json:"transaction_id"`
	Authority     MutationAuthority   `json:"authority"`
	EffectDigest  Hex32               `json:"effect_digest"`
	Outcome       *TransactionOutcome `json:"outcome"`
}

// Definitive reports only an original committed or rejected outcome. Expiry,
// unknown and pending do not prove that the effects were aborted.
func (r TransactionStatusResponse) Definitive() bool {
	return r.State == TransactionCommitted || r.State == TransactionRejected
}
func (r TransactionStatusResponse) validate(scope DocumentScope, request TransactionStatusRequest) error {
	if r.Version != 1 || r.TransactionID != request.TransactionID || r.EffectDigest != request.EffectDigest || !sameAuthority(r.Authority, request.Authority) {
		return fmt.Errorf("sdk: transaction status identity mismatch")
	}
	switch r.State {
	case TransactionCommitted:
		if r.Outcome == nil || r.Outcome.Receipt == nil || r.Outcome.Error != nil {
			return fmt.Errorf("sdk: committed status requires exactly one receipt")
		}
		return r.Outcome.Receipt.validate(scope, request)
	case TransactionRejected:
		if r.Outcome == nil || r.Outcome.Error == nil || r.Outcome.Receipt != nil || r.Outcome.Error.Code != "precondition_failed" {
			return fmt.Errorf("sdk: rejected status requires precondition_failed")
		}
	case TransactionPending, TransactionUnknown, TransactionExpired:
		if r.Outcome != nil {
			return fmt.Errorf("sdk: unresolved status outcome must be null")
		}
	default:
		return fmt.Errorf("sdk: invalid transaction status state")
	}
	return nil
}
