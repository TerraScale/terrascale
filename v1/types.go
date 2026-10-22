package v1

import (
	"encoding/json"
	"fmt"
	"strconv"
	"time"
)

// Uint64 is a u64 carried on the wire as a canonical decimal string (no sign,
// no leading zeros), such as an offset or a byte count.
type Uint64 uint64

func (n Uint64) String() string { return strconv.FormatUint(uint64(n), 10) }

// MarshalJSON encodes n as a decimal string.
func (n Uint64) MarshalJSON() ([]byte, error) {
	return strconv.AppendQuote(nil, n.String()), nil
}

// UnmarshalJSON decodes a canonical decimal string.
func (n *Uint64) UnmarshalJSON(data []byte) error {
	var s string
	if err := json.Unmarshal(data, &s); err != nil {
		return fmt.Errorf("u64 must be a decimal string: %w", err)
	}
	v, err := parseCanonicalU64(s)
	if err != nil {
		return err
	}
	*n = Uint64(v)
	return nil
}

func parseCanonicalU64(s string) (uint64, error) {
	if s == "" || (len(s) > 1 && s[0] == '0') {
		return 0, fmt.Errorf("u64 %q is not canonical", s)
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, fmt.Errorf("u64 %q is not canonical", s)
		}
	}
	return strconv.ParseUint(s, 10, 64)
}

// Timestamp is a TerraBase timestamp: Unix milliseconds carried as a
// canonical decimal string.
type Timestamp Uint64

// Time converts t to a time.Time.
func (t Timestamp) Time() time.Time { return time.UnixMilli(int64(t)) }

func (t Timestamp) String() string { return Uint64(t).String() }

// MarshalJSON encodes t as a decimal string.
func (t Timestamp) MarshalJSON() ([]byte, error) { return Uint64(t).MarshalJSON() }

// UnmarshalJSON decodes a canonical decimal string.
func (t *Timestamp) UnmarshalJSON(data []byte) error { return (*Uint64)(t).UnmarshalJSON(data) }

// Durability is the acknowledgment level of a committed write.
type Durability string

// Durability levels.
const (
	LocalDurable  Durability = "local_durable"
	ObjectDurable Durability = "object_durable"
	RemoteDurable Durability = "remote_durable"
	Unchanged     Durability = "unchanged"
)

// State is a document's point state.
type State string

// Document states.
const (
	StateValue    State = "value"
	StateConflict State = "conflict"
)

// Document is a document as returned by GET, queries and shapes.
type Document struct {
	ID      string `json:"id" documentkey:"required"`
	Version string `json:"version"`
	State   State  `json:"state"`
	// Document is the exact JSON object for state "value".
	Document json.RawMessage `json:"document,omitempty"`
	// Siblings lists every live and deleted sibling for state "conflict".
	Siblings []Sibling `json:"siblings,omitempty"`
}

// Decode decodes the document body into v, keeping numbers as json.Number.
func (d *Document) Decode(v any) error { return Decode(d.Document, v) }

// Sibling is one concurrent version of a conflicted document.
type Sibling struct {
	Event    Event           `json:"event"`
	Deleted  bool            `json:"deleted"`
	Document json.RawMessage `json:"document,omitempty"`
}

// Event identifies the write that produced a sibling: Replica is a ULID
// and Incarnation is an opaque 32-character lowercase hex epoch token.
type Event struct {
	Replica     string `json:"replica" ulid:"required"`
	Incarnation string `json:"incarnation"`
	Sequence    Uint64 `json:"sequence"`
}

// Decode decodes JSON into v with exact numbers: interface values receive
// json.Number instead of float64.
func Decode(data []byte, v any) error { return decodeExact(data, v) }

// WriteResult is the response to a document write.
type WriteResult struct {
	// Document is the stored object returned by PATCH.
	Document   json.RawMessage `json:"document,omitempty"`
	ID         string          `json:"id" documentkey:"required"`
	Version    string          `json:"version"`
	Offset     Uint64          `json:"offset"`
	Durability Durability      `json:"durability"`
	// Created is true when a PUT or POST answered 201.
	Created bool `json:"-"`
	// Location is the Location header of a POST create.
	Location string `json:"-"`
}

// requiredResponseMembers rejects absent or null protocol members while allowing new ones.
func requiredResponseMembers(data []byte, names ...string) error {
	var members map[string]json.RawMessage
	if err := json.Unmarshal(data, &members); err != nil {
		return err
	}
	for _, name := range names {
		raw := members[name]
		if len(raw) == 0 || string(raw) == "null" {
			return fmt.Errorf("terrabase: response is missing %s", name)
		}
	}
	return nil
}

// UnmarshalJSON validates the required document envelope without restricting future states.
func (d *Document) UnmarshalJSON(data []byte) error {
	if err := requiredResponseMembers(data, "id", "version", "state"); err != nil {
		return err
	}
	type wire Document
	var decoded wire
	if err := json.Unmarshal(data, &decoded); err != nil {
		return err
	}
	if err := checkVersion(decoded.Version); err != nil {
		return err
	}
	if decoded.State == "" {
		return fmt.Errorf("terrabase: response state must not be empty")
	}
	if decoded.State == StateValue || len(decoded.Document) > 0 {
		if len(decoded.Document) == 0 || decoded.Document[0] != '{' {
			return fmt.Errorf("terrabase: response document must be a JSON object")
		}
	}
	*d = Document(decoded)
	return nil
}

// UnmarshalJSON validates the required write acknowledgement.
func (r *WriteResult) UnmarshalJSON(data []byte) error {
	if err := requiredResponseMembers(data, "id", "version", "offset", "durability"); err != nil {
		return err
	}
	type wire WriteResult
	var decoded wire
	if err := json.Unmarshal(data, &decoded); err != nil {
		return err
	}
	if err := checkVersion(decoded.Version); err != nil {
		return err
	}
	if decoded.Durability == "" {
		return fmt.Errorf("terrabase: response durability must not be empty")
	}
	*r = WriteResult(decoded)
	return nil
}
