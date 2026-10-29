package v1

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
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
