package sdk

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
)

type PointState string

const (
	PointAbsent   PointState = "absent"
	PointDeleted  PointState = "deleted"
	PointValue    PointState = "value"
	PointConflict PointState = "conflict"
)

type DocumentSibling struct {
	Event            DocumentEvent   `json:"event"`
	SchemaGeneration DecimalU64      `json:"schema_generation"`
	SchemaDigest     Hex32           `json:"schema_digest"`
	Kind             string          `json:"kind"`
	Fields           []DocumentField `json:"fields,omitempty"`
}

func (s DocumentSibling) MarshalJSON() ([]byte, error) {
	if err := (WriteValue{Kind: s.Kind, Fields: s.Fields}).validate(); err != nil {
		return nil, err
	}
	type wire DocumentSibling
	if s.Kind == "tombstone" {
		return json.Marshal(wire(s))
	}
	return json.Marshal(struct {
		Event            DocumentEvent   `json:"event"`
		SchemaGeneration DecimalU64      `json:"schema_generation"`
		SchemaDigest     Hex32           `json:"schema_digest"`
		Kind             string          `json:"kind"`
		Fields           []DocumentField `json:"fields"`
	}{s.Event, s.SchemaGeneration, s.SchemaDigest, s.Kind, nonnil(s.Fields)})
}

func (s *DocumentSibling) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, []string{"event", "schema_generation", "schema_digest", "kind"}, []string{"fields"})
	if err != nil {
		return err
	}
	var kind string
	if err := json.Unmarshal(object["kind"], &kind); err != nil {
		return err
	}
	required := []string{"event", "schema_generation", "schema_digest", "kind"}
	if kind == "live" {
		required = append(required, "fields")
	}
	if _, err := nativeObject(data, required, nil); err != nil {
		return err
	}
	type wire DocumentSibling
	var value wire
	if err := decodeNativeJSON(data, &value); err != nil {
		return err
	}
	if kind != "live" && kind != "tombstone" {
		return fmt.Errorf("sdk: invalid document sibling kind")
	}
	*s = DocumentSibling(value)
	return nil
}

type DocumentPoint struct {
	Version      uint8             `json:"version"`
	State        PointState        `json:"state"`
	StateVersion *Hex32            `json:"state_version"`
	Context      []Observed        `json:"context"`
	Siblings     []DocumentSibling `json:"siblings"`
}

func (p DocumentPoint) validate(scope DocumentScope) error {
	if p.Version != 1 {
		return fmt.Errorf("sdk: native point version must be 1")
	}
	if err := validateObserved(p.Context); err != nil {
		return err
	}
	if len(p.Siblings) > 1024 {
		return fmt.Errorf("sdk: native sibling limit")
	}
	switch p.State {
	case PointAbsent:
		if p.StateVersion != nil || len(p.Context) != 0 || len(p.Siblings) != 0 {
			return fmt.Errorf("sdk: absent point contains retained state")
		}
	case PointDeleted, PointValue:
		if p.StateVersion == nil || len(p.Siblings) != 1 {
			return fmt.Errorf("sdk: single point requires one sibling and state version")
		}
		kind := "live"
		if p.State == PointDeleted {
			kind = "tombstone"
		}
		if p.Siblings[0].Kind != kind {
			return fmt.Errorf("sdk: point state and sibling disagree")
		}
	case PointConflict:
		if p.StateVersion != nil || len(p.Siblings) < 2 {
			return fmt.Errorf("sdk: conflict must preserve multiple siblings without a CAS version")
		}
	default:
		return fmt.Errorf("sdk: invalid native point state")
	}
	seenOrigins := make(map[WriterIdentity]bool, len(p.Siblings))
	for _, sibling := range p.Siblings {
		if err := sibling.Event.validate(scope); err != nil {
			return err
		}
		if sibling.SchemaGeneration == 0 {
			return fmt.Errorf("sdk: sibling requires positive schema generation")
		}
		origin := WriterIdentity{Replica: sibling.Event.Replica, Incarnation: sibling.Event.Incarnation}
		if seenOrigins[origin] {
			return fmt.Errorf("sdk: duplicate sibling origin")
		}
		seenOrigins[origin] = true
		if err := (WriteValue{Kind: sibling.Kind, Fields: sibling.Fields}).validate(); err != nil {
			return err
		}
		found := false
		for _, dot := range p.Context {
			if dot.Replica == sibling.Event.Replica && dot.Incarnation == sibling.Event.Incarnation && dot.Sequence >= sibling.Event.Sequence {
				found = true
				break
			}
		}
		if !found {
			return fmt.Errorf("sdk: sibling event is missing from causal context")
		}
	}
	return nil
}

type QueryOperation string

const (
	QueryEqual              QueryOperation = "equal"
	QueryLessThan           QueryOperation = "less_than"
	QueryLessThanOrEqual    QueryOperation = "less_than_or_equal"
	QueryGreaterThan        QueryOperation = "greater_than"
	QueryGreaterThanOrEqual QueryOperation = "greater_than_or_equal"
	QueryIsNull             QueryOperation = "is_null"
	QueryIsMissing          QueryOperation = "is_missing"
)

type QueryPredicate struct {
	Operation QueryOperation `json:"op"`
	Path      []uint32       `json:"path"`
	Value     *Value         `json:"value,omitempty"`
}

func (p QueryPredicate) validate() error {
	if len(p.Path) == 0 || len(p.Path) > 16 {
		return fmt.Errorf("sdk: query field path requires 1..16 IDs")
	}
	for _, id := range p.Path {
		if id == 0 {
			return fmt.Errorf("sdk: query path IDs must be positive")
		}
	}
	switch p.Operation {
	case QueryIsNull, QueryIsMissing:
		if p.Value != nil {
			return fmt.Errorf("sdk: null/missing predicate cannot contain a value")
		}
	case QueryEqual, QueryLessThan, QueryLessThanOrEqual, QueryGreaterThan, QueryGreaterThanOrEqual:
		if p.Value == nil {
			return fmt.Errorf("sdk: comparison predicate requires a value")
		}
		switch p.Value.Kind() {
		case ValueBool, ValueI64, ValueUTF8, ValueBytes:
		default:
			return fmt.Errorf("sdk: query comparison requires a native scalar")
		}
		nodes := 0
		_, data, err := p.Value.encode(1, &nodes)
		if err != nil {
			return err
		}
		if len(data) > 1<<20 {
			return fmt.Errorf("sdk: query value byte limit")
		}
	default:
		return fmt.Errorf("sdk: invalid native query operation")
	}
	return nil
}
func (p QueryPredicate) MarshalJSON() ([]byte, error) {
	if err := p.validate(); err != nil {
		return nil, err
	}
	type wire QueryPredicate
	return json.Marshal(wire(p))
}
func (p *QueryPredicate) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, []string{"op", "path"}, []string{"value"})
	if err != nil {
		return err
	}
	var op QueryOperation
	if err := json.Unmarshal(object["op"], &op); err != nil {
		return err
	}
	required := []string{"op", "path"}
	if op != QueryIsNull && op != QueryIsMissing {
		required = append(required, "value")
	}
	if _, err := nativeObject(data, required, nil); err != nil {
		return err
	}
	type wire QueryPredicate
	var value wire
	if err := decodeNativeJSON(data, &value); err != nil {
		return err
	}
	if err := QueryPredicate(value).validate(); err != nil {
		return err
	}
	*p = QueryPredicate(value)
	return nil
}

type QueryRequest struct {
	Version   uint8          `json:"version"`
	Predicate QueryPredicate `json:"predicate"`
	Limit     uint16         `json:"limit"`
	Cursor    string         `json:"cursor,omitempty"`
}

func validCursor(cursor string) bool {
	if len(cursor) == 0 || len(cursor) > 1024 {
		return false
	}
	decoded, err := base64.RawURLEncoding.Strict().DecodeString(cursor)
	return err == nil && base64.RawURLEncoding.EncodeToString(decoded) == cursor
}
func (r QueryRequest) validate() error {
	if r.Version != 1 || r.Limit < 1 || r.Limit > 128 {
		return fmt.Errorf("sdk: native query version or limit")
	}
	if r.Cursor != "" && !validCursor(r.Cursor) {
		return fmt.Errorf("sdk: invalid opaque query cursor")
	}
	return r.Predicate.validate()
}

type DocumentRow struct {
	Document UUID          `json:"document"`
	Point    DocumentPoint `json:"point"`
}
type QueryResponse struct {
	Version    uint8         `json:"version"`
	Rows       []DocumentRow `json:"rows"`
	NextCursor string        `json:"next_cursor,omitempty"`
}

func (r *QueryResponse) UnmarshalJSON(data []byte) error {
	object, err := nativeObject(data, []string{"version", "rows"}, []string{"next_cursor"})
	if err != nil {
		return err
	}
	if raw, ok := object["next_cursor"]; ok {
		cursor, err := nativeString(raw)
		if err != nil || !validCursor(cursor) {
			return fmt.Errorf("sdk: invalid next query cursor")
		}
	}
	type wire QueryResponse
	var value wire
	if err := decodeNativeJSON(data, &value); err != nil {
		return err
	}
	*r = QueryResponse(value)
	return nil
}
func validateRows(scope DocumentScope, rows []DocumentRow) error {
	if len(rows) > 128 {
		return fmt.Errorf("sdk: native query row limit")
	}
	var previous UUID
	for _, row := range rows {
		if _, err := ParseUUID(string(row.Document)); err != nil {
			return err
		}
		if row.Document <= previous {
			return fmt.Errorf("sdk: native query documents must increase")
		}
		previous = row.Document
		if row.Point.State != PointValue && row.Point.State != PointConflict {
			return fmt.Errorf("sdk: native query rows must be live or conflict")
		}
		if err := row.Point.validate(scope); err != nil {
			return err
		}
	}
	return nil
}

// DocumentsClient uses only the native UUID scoped /v1 data contract.
type DocumentsClient struct{ c *Client }

func nativeCollectionPath(scope DocumentScope, collection UUID) (string, error) {
	if err := scope.validate(); err != nil {
		return "", err
	}
	if _, err := ParseUUID(string(collection)); err != nil {
		return "", err
	}
	return scope.path() + "/collections/" + string(collection) + "/documents", nil
}
func (d *DocumentsClient) Get(ctx context.Context, scope DocumentScope, collection, document UUID) (*DocumentPoint, error) {
	path, err := nativeCollectionPath(scope, collection)
	if err != nil {
		return nil, err
	}
	if _, err := ParseUUID(string(document)); err != nil {
		return nil, err
	}
	var point DocumentPoint
	if err := d.c.doNative(ctx, scope, nativeRead, http.MethodGet, path+"/"+string(document), nil, &point, false); err != nil {
		return nil, err
	}
	if err := point.validate(scope); err != nil {
		return nil, err
	}
	return &point, nil
}
func (d *DocumentsClient) Query(ctx context.Context, scope DocumentScope, collection UUID, request QueryRequest) (*QueryResponse, error) {
	path, err := nativeCollectionPath(scope, collection)
	if err != nil {
		return nil, err
	}
	if err := request.validate(); err != nil {
		return nil, err
	}
	request, err = freezeNative(request)
	if err != nil {
		return nil, err
	}
	var result QueryResponse
	if err := d.c.doNative(ctx, scope, nativeRead|nativeQuery, http.MethodPost, path+"/query", request, &result, false); err != nil {
		return nil, err
	}
	if result.Version != 1 || result.NextCursor != "" && !validCursor(result.NextCursor) {
		return nil, fmt.Errorf("sdk: invalid native query response version or cursor")
	}
	if err := validateRows(scope, result.Rows); err != nil {
		return nil, err
	}
	if len(result.Rows) > int(request.Limit) {
		return nil, fmt.Errorf("sdk: query response exceeds requested limit")
	}
	return &result, nil
}
func (d *DocumentsClient) Commit(ctx context.Context, scope DocumentScope, request CommitRequest) (*CommitResponse, error) {
	operation, err := d.PrepareTransaction(scope, request)
	if err != nil {
		return nil, err
	}
	return operation.Commit(ctx)
}
func (d *DocumentsClient) TransactionStatus(ctx context.Context, scope DocumentScope, request TransactionStatusRequest) (*TransactionStatusResponse, error) {
	if err := scope.validate(); err != nil {
		return nil, err
	}
	if request.Version != 1 {
		return nil, fmt.Errorf("sdk: native status version must be 1")
	}
	if err := request.Authority.validate(); err != nil {
		return nil, err
	}
	var err error
	request, err = freezeNative(request)
	if err != nil {
		return nil, err
	}
	if request.Authority.Mode == AuthoritySingleWriter && d.c.cfg.DataBaseURL == "" {
		return nil, &RoutingError{
			Route: "native.status", Method: http.MethodPost, Path: scope.path() + "/transactions/status",
			Err: nativeRoutingFailure("capability_unavailable", fmt.Errorf("authenticated single writer status admission and fence are unavailable")),
		}
	}
	var result TransactionStatusResponse
	if err := d.c.doNative(ctx, scope, nativeWrite, http.MethodPost, scope.path()+"/transactions/status", request, &result, false); err != nil {
		return nil, err
	}
	if err := result.validate(scope, request); err != nil {
		return nil, err
	}
	return &result, nil
}
