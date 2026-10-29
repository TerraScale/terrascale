package v1

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
)

// InferredCollectionSchema describes sampled fields and declared unique keys.
type InferredCollectionSchema struct {
	Collection        string        `json:"collection"`
	Documents         int64         `json:"documents"`
	SampledDocuments  int           `json:"sampledDocuments"`
	Fields            []SchemaField `json:"fields"`
	UniqueConstraints []UniqueKey   `json:"uniqueConstraints"`
}

// SchemaField counts observed JSON types at a dotted path.
type SchemaField struct {
	Path    string         `json:"path"`
	Present int            `json:"present"`
	Types   map[string]int `json:"types"`
}

// DeclaredCollectionSchema contains the validation schema, or JSON null when
// absent. The producer omits Dialect when no schema is attached.
type DeclaredCollectionSchema struct {
	Schema  json.RawMessage `json:"schema"`
	Dialect string          `json:"dialect,omitempty"`
}

// Schema lists collection document counts and storage bytes.
func (d *Database) Schema(ctx context.Context) ([]CollectionStats, error) {
	out, err := databaseCall[struct {
		Items []CollectionStats `json:"items"`
	}](ctx, d, http.MethodGet, nil, nil, "schema")
	if err != nil {
		return nil, err
	}
	return out.Items, nil
}

// CollectionSchema infers fields independently of the declared JSON Schema.
func (d *Database) CollectionSchema(ctx context.Context, collection string) (*InferredCollectionSchema, error) {
	if err := checkName("collection", collection); err != nil {
		return nil, err
	}
	return databaseCall[InferredCollectionSchema](ctx, d, http.MethodGet, nil, nil, "schema", collection)
}

// Schema reads this collection's declared validation schema.
func (col *Collection) Schema(ctx context.Context) (*DeclaredCollectionSchema, error) {
	return col.schemaCall(ctx, http.MethodGet, nil)
}

// SetSchema replaces the Draft 2020-12 JSON Schema used for future writes.
// It does not validate existing documents; the producer validates the schema.
func (col *Collection) SetSchema(ctx context.Context, schema any) (*DeclaredCollectionSchema, error) {
	body, err := json.Marshal(schema)
	if err != nil {
		return nil, err
	}
	if len(body) > 64<<10 {
		return nil, errors.New("terrabase: schema exceeds 64 KiB")
	}
	return col.schemaCall(ctx, http.MethodPut, body)
}

// DeleteSchema removes validation while preserving documents and unique keys.
func (col *Collection) DeleteSchema(ctx context.Context) (*DeclaredCollectionSchema, error) {
	return col.schemaCall(ctx, http.MethodDelete, nil)
}

func (col *Collection) schemaCall(ctx context.Context, method string, body []byte) (*DeclaredCollectionSchema, error) {
	p, err := col.path("schema")
	if err != nil {
		return nil, err
	}
	return publicCall[DeclaredCollectionSchema](ctx, col.db.c, request{method: method, path: p, body: body, replaySafe: method == http.MethodGet}, nil)
}

type collectionTTL struct {
	Path *string `json:"path"`
}

// TTL returns the JSON Pointer whose RFC 3339 time expires documents, or ""
// when the collection has no TTL.
func (col *Collection) TTL(ctx context.Context) (string, error) {
	var out collectionTTL
	if err := col.ttlCall(ctx, http.MethodGet, nil, &out); err != nil || out.Path == nil {
		return "", err
	}
	return *out.Path, nil
}

// SetTTL expires documents whose member at path (an RFC 6901 JSON Pointer
// such as "/expiresAt") holds a past RFC 3339 UTC time. An hourly sweep
// deletes them, so removal can lag expiry by up to an hour.
func (col *Collection) SetTTL(ctx context.Context, path string) error {
	if !strings.HasPrefix(path, "/") {
		return fmt.Errorf("terrabase: TTL path %q must be a JSON Pointer such as /expiresAt", path)
	}
	return col.ttlCall(ctx, http.MethodPut, collectionTTL{Path: &path}, &collectionTTL{})
}

// DeleteTTL stops expiring documents in the collection.
func (col *Collection) DeleteTTL(ctx context.Context) error {
	return col.ttlCall(ctx, http.MethodDelete, nil, nil)
}

func (col *Collection) ttlCall(ctx context.Context, method string, in, out any) error {
	p, err := col.path("ttl")
	if err != nil {
		return err
	}
	_, err = col.db.c.call(ctx, request{method: method, path: p, replaySafe: method == http.MethodGet}, in, out)
	return err
}
