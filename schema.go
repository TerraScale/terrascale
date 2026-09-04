// Typed schema builder stub (GOAL D-48): schema JSON is the source of truth,
// authored through each SDK's typed schema builder and applied with push to a
// branch. Field names/types feed codegen; merge-schema fields (D-36) declare
// typed CRDT behavior per collection.
package sdk

import (
	"context"
	"fmt"
	"net/url"
	"strings"
)

// FieldType is the declared type of a schema field.
type FieldType string

const (
	FieldString        FieldType = "string"
	FieldInt           FieldType = "int"
	FieldFloat         FieldType = "float"
	FieldBool          FieldType = "bool"
	FieldBytes         FieldType = "bytes"
	FieldJSON          FieldType = "json"
	FieldText          FieldType = "text"
	FieldCounter       FieldType = "counter-pn"
	FieldSetAddWins    FieldType = "set-add-wins"
	FieldSetRemoveWins FieldType = "set-remove-wins"
	FieldRegisterMV    FieldType = "register-mv"
)

// FieldDef is one declared field.
type FieldDef struct {
	Name     string    `json:"name"`
	Type     FieldType `json:"type"`
	Required bool      `json:"required,omitempty"`
	Indexed  bool      `json:"indexed,omitempty"`
	Unique   bool      `json:"unique,omitempty"`
}

// FieldOption customizes a FieldDef.
type FieldOption func(*FieldDef)

// Required marks a field as required.
func Required() FieldOption { return func(f *FieldDef) { f.Required = true } }

// Indexed marks a field as extracted and indexed.
func Indexed() FieldOption { return func(f *FieldDef) { f.Indexed = true } }

// Unique marks a field as unique within its region (D-37: cross-region
// uniqueness is best-effort).
func Unique() FieldOption { return func(f *FieldDef) { f.Unique = true } }

// CollectionSchema is the declared schema of one collection.
type CollectionSchema struct {
	Name                 string     `json:"name"`
	Fields               []FieldDef `json:"fields"`
	HistoryRetentionDays int        `json:"historyRetentionDays,omitempty"`
}

// Schema is the full declared database schema.
type Schema struct {
	Collections []CollectionSchema `json:"collections"`
}

// CollectionBuilder fluently declares one collection.
type CollectionBuilder struct {
	name      string
	fields    []FieldDef
	retention int
}

// NewCollection starts declaring a collection.
func NewCollection(name string) *CollectionBuilder {
	return &CollectionBuilder{name: strings.TrimSpace(name)}
}

// Field adds a field to the collection.
func (b *CollectionBuilder) Field(name string, typ FieldType, opts ...FieldOption) *CollectionBuilder {
	def := FieldDef{Name: strings.TrimSpace(name), Type: typ}
	for _, opt := range opts {
		if opt != nil {
			opt(&def)
		}
	}
	b.fields = append(b.fields, def)
	return b
}

// HistoryRetention sets per-collection history retention in days (1-30, D-25).
func (b *CollectionBuilder) HistoryRetention(days int) *CollectionBuilder {
	b.retention = days
	return b
}

// Build validates and returns the collection schema.
func (b *CollectionBuilder) Build() (CollectionSchema, error) {
	if b.name == "" {
		return CollectionSchema{}, fmt.Errorf("sdk: collection name cannot be empty")
	}
	if b.retention < 0 || b.retention > 30 {
		return CollectionSchema{}, fmt.Errorf("sdk: history retention must be between 0 and 30 days")
	}
	seen := map[string]bool{}
	for _, f := range b.fields {
		if f.Name == "" {
			return CollectionSchema{}, fmt.Errorf("sdk: field names cannot be empty (collection %q)", b.name)
		}
		if seen[f.Name] {
			return CollectionSchema{}, fmt.Errorf("sdk: duplicate field %q (collection %q)", f.Name, b.name)
		}
		seen[f.Name] = true
		if !validFieldType(f.Type) {
			return CollectionSchema{}, fmt.Errorf("sdk: unknown field type %q (collection %q)", f.Type, b.name)
		}
	}
	fields := append([]FieldDef(nil), b.fields...)
	return CollectionSchema{Name: b.name, Fields: fields, HistoryRetentionDays: b.retention}, nil
}

func validFieldType(t FieldType) bool {
	switch t {
	case FieldString, FieldInt, FieldFloat, FieldBool, FieldBytes,
		FieldJSON, FieldText, FieldCounter, FieldSetAddWins,
		FieldSetRemoveWins, FieldRegisterMV:
		return true
	default:
		return false
	}
}

// SchemaBuilder collects collection schemas into one Schema.
type SchemaBuilder struct {
	collections []CollectionSchema
}

// NewSchemaBuilder returns an empty schema builder.
func NewSchemaBuilder() *SchemaBuilder { return &SchemaBuilder{} }

// AddCollection adds an already-built collection schema.
func (b *SchemaBuilder) AddCollection(cs CollectionSchema) *SchemaBuilder {
	b.collections = append(b.collections, cs)
	return b
}

// Add builds cb and adds it, returning the first error encountered.
func (b *SchemaBuilder) Add(cb *CollectionBuilder) (*SchemaBuilder, error) {
	cs, err := cb.Build()
	if err != nil {
		return b, err
	}
	return b.AddCollection(cs), nil
}

// Build returns the assembled schema.
func (b *SchemaBuilder) Build() Schema {
	return Schema{Collections: append([]CollectionSchema(nil), b.collections...)}
}

// Apply pushes the built schema to a database branch (terrascale push).
func (b *SchemaBuilder) Apply(ctx context.Context, c *Client, databaseID string, opts ...RequestOption) (*Schema, error) {
	return b.Build().Apply(ctx, c, databaseID, opts...)
}

// Apply pushes schema s to a database branch.
func (s Schema) Apply(ctx context.Context, c *Client, databaseID string, opts ...RequestOption) (*Schema, error) {
	if c == nil {
		return nil, fmt.Errorf("sdk: client cannot be nil")
	}
	if strings.TrimSpace(databaseID) == "" {
		return nil, fmt.Errorf("sdk: database id cannot be empty")
	}
	var out Schema
	path := "/api/v1/databases/" + url.PathEscape(databaseID) + "/schema"
	if err := c.do(ctx, "PUT", path, nil, s, &out, "schema.apply", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
