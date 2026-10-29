// Typed schema builder for the terrascale.schema.json contract (D-78).
//
// A schema describes the extracted columns, transactional indexes, TTL, and
// deterministic merge behavior of each SQL collection. Document writes remain
// document operations; this package only declares the schema consumed by push
// and codegen.
package sdk

import (
	"encoding/json"
	"fmt"
	"sort"
	"strings"
)

// FieldType is the declared type of an extracted schema field.
type FieldType string

const (
	FieldString        FieldType = "string"
	FieldInt           FieldType = "int"
	FieldFloat         FieldType = "float"
	FieldBool          FieldType = "bool"
	FieldBytes         FieldType = "bytes"
	FieldJSON          FieldType = "json"
	FieldTimestamp     FieldType = "timestamp"
	FieldText          FieldType = "text"
	FieldCounter       FieldType = "counter-pn"
	FieldSetAddWins    FieldType = "set-add-wins"
	FieldSetRemoveWins FieldType = "set-remove-wins"
	FieldRegisterMV    FieldType = "register-mv"

	// Names used by the other SDKs and by schema authors. They serialize to
	// the same stable contract values as their shorter Go counterparts.
	FieldInteger       = FieldInt
	FieldBoolean       = FieldBool
	FieldPNCounter     = FieldCounter
	FieldAddWinsSet    = FieldSetAddWins
	FieldRemoveWinsSet = FieldSetRemoveWins
	FieldMVRegister    = FieldRegisterMV
)

// MergePolicy is the collection-level policy for fields without a typed CRDT
// kernel. Typed CRDT fields select their own kernel through FieldDef.MergePolicy.
type MergePolicy string

const (
	MergeRemoveWins    MergePolicy = "remove-wins"
	MergeHLCTiebreak   MergePolicy = "hlc-tiebreak"
	MergeLastWriteWins MergePolicy = "last-write-wins"

	// Descriptive aliases shared by SDKs.
	MergePolicyRemoveWins    = MergeRemoveWins
	MergePolicyHLCTiebreak   = MergeHLCTiebreak
	MergePolicyLastWriteWins = MergeLastWriteWins
	MergeHLC                 = MergeHLCTiebreak
	MergeHlcTiebreak         = MergeHLCTiebreak
	MergeLastWrite           = MergeLastWriteWins
)

// IndexKind identifies the index implementation declared in schema JSON.
type IndexKind string

const (
	IndexBTree    IndexKind = "btree"
	IndexFTS      IndexKind = "fts"
	IndexFullText           = IndexFTS
)

// FieldDef is one declared extracted field.
type FieldDef struct {
	Name        string    `json:"name"`
	Type        FieldType `json:"type"`
	Required    bool      `json:"required"`
	Indexed     bool      `json:"indexed"`
	Unique      bool      `json:"unique,omitempty"`
	MergePolicy FieldType `json:"mergePolicy,omitempty"`
}

// Field is the concise descriptor used by typed schema callers.
// It is an alias so generated field values and builder fields remain
// interchangeable without conversion.
type Field = FieldDef

// FieldOption customizes a FieldDef.
type FieldOption func(*FieldDef)

// Required marks a field as required.
func Required() FieldOption { return func(f *FieldDef) { f.Required = true } }

// Indexed marks a field as extracted and indexed.
func Indexed() FieldOption { return func(f *FieldDef) { f.Indexed = true } }

// Unique marks a field as unique within its Region (cross-region uniqueness
// remains best-effort). Unique fields are necessarily indexed.
func Unique() FieldOption {
	return func(f *FieldDef) {
		f.Unique = true
		f.Indexed = true
	}
}

// MergeField selects a typed CRDT kernel for a field.
func MergeField(policy FieldType) FieldOption {
	return func(f *FieldDef) { f.MergePolicy = policy }
}

// WithMergePolicy is a descriptive alias for MergeField.
func WithMergePolicy(policy FieldType) FieldOption { return MergeField(policy) }

// CRDT is a concise alias for MergeField.
func CRDT(policy FieldType) FieldOption { return MergeField(policy) }

// CollectionSchema is the declared schema of one SQL collection.
type CollectionSchema struct {
	Name                 string              `json:"name"`
	Version              *uint64             `json:"version,omitempty"`
	Renames              []SchemaFieldRename `json:"renames,omitempty"`
	FTS                  *SchemaFTS          `json:"fts,omitempty"`
	Fields               []FieldDef          `json:"fields"`
	Indexes              []IndexDef          `json:"indexes,omitempty"`
	TTL                  *TTLDef             `json:"ttl,omitempty"`
	HistoryRetentionDays int                 `json:"historyRetentionDays"`
	MergePolicy          MergePolicy         `json:"mergePolicy"`
}

// SchemaFieldRename carries a migration from a previous stored field name.
type SchemaFieldRename struct {
	From string `json:"from"`
	To   string `json:"to"`
}
type SchemaFTS struct {
	Fields []string `json:"fields"`
}

// TTLDef declares the root field containing an expiry timestamp.
type TTLDef struct {
	Field string `json:"field"`
}

// NewTTL creates a TTL declaration for a root timestamp field.
func NewTTL(field string) TTLDef { return TTLDef{Field: strings.TrimSpace(field)} }

// TTL is the descriptive constructor spelling used by the other SDKs.
func TTL(field string) TTLDef { return NewTTL(field) }

// IndexDef is one explicit transactional index. A full-text index must have
// exactly one string or text field and uses kind "fts" in schema JSON.
type IndexDef struct {
	Name   string    `json:"name"`
	Fields []string  `json:"fields"`
	Unique bool      `json:"unique,omitempty"`
	Kind   IndexKind `json:"kind,omitempty"`
}

// NewIndex declares an ordinary b-tree index over one or more fields.
func NewIndex(name string, fields ...any) IndexDef {
	var names []string
	for _, value := range fields {
		switch value := value.(type) {
		case string:
			names = append(names, value)
		case []string:
			names = append(names, value...)
		}
	}
	return IndexDef{Name: strings.TrimSpace(name), Fields: trimStrings(names), Kind: IndexBTree}
}

// NewFTSIndex declares a full-text index over one field.
func NewFTSIndex(name, field string) IndexDef {
	return IndexDef{Name: strings.TrimSpace(name), Fields: []string{strings.TrimSpace(field)}, Kind: IndexFTS}
}

// On declares an index whose name is derived from its sole field.
func (i IndexDef) On(field string) IndexDef {
	field = strings.TrimSpace(field)
	i.Name, i.Fields, i.Kind = field, []string{field}, IndexBTree
	return i
}

// UniqueIndex marks an index as unique within its Region.
func (i IndexDef) UniqueIndex() IndexDef { i.Unique = true; return i }

// UniqueKey is a fluent alias for UniqueIndex.
func (i IndexDef) UniqueKey() IndexDef { return i.UniqueIndex() }

// WithUnique is a fluent alias for UniqueIndex.
func (i IndexDef) WithUnique() IndexDef { return i.UniqueIndex() }

// FullText converts an index declaration to an FTS index.
func (i IndexDef) FullText() IndexDef { i.Kind = IndexFTS; i.Unique = false; return i }

// FTS converts an index declaration to an FTS index.
func (i IndexDef) FTS() IndexDef { return i.FullText() }

// CollectionBuilder fluently declares one collection.
type CollectionBuilder struct {
	name         string
	fields       []FieldDef
	indexes      []IndexDef
	ttl          *TTLDef
	retention    int
	retentionSet bool
	mergePolicy  MergePolicy
}

// NewCollection starts declaring a collection with D-25's seven-day history default.
func NewCollection(name string) *CollectionBuilder {
	return &CollectionBuilder{name: strings.TrimSpace(name), retention: DefaultHistoryRetentionDays, mergePolicy: MergeRemoveWins}
}

// NewCollectionSchema starts a directly composed collection schema. The
// fluent CollectionBuilder returned by NewCollection is preferred when
// validation errors should be reported at Build time.
func NewCollectionSchema(name string) CollectionSchema {
	return CollectionSchema{
		Name:                 strings.TrimSpace(name),
		HistoryRetentionDays: DefaultHistoryRetentionDays,
		MergePolicy:          MergeRemoveWins,
	}
}

// Field appends a field to a directly composed collection schema.
func (c CollectionSchema) Field(name string, typ FieldType, opts ...FieldOption) CollectionSchema {
	def := FieldDef{Name: strings.TrimSpace(name), Type: typ}
	for _, opt := range opts {
		if opt != nil {
			opt(&def)
		}
	}
	c.Fields = append(c.Fields, def)
	return c
}

// Index appends an explicit index to a directly composed collection schema.
func (c CollectionSchema) Index(index IndexDef) CollectionSchema {
	c.Indexes = append(c.Indexes, index)
	return c
}

// TTLField configures expiry on a directly composed collection schema.
func (c CollectionSchema) TTLField(field string) CollectionSchema {
	ttl := NewTTL(field)
	c.TTL = &ttl
	return c
}

// HistoryWindow configures history on a directly composed collection schema.
func (c CollectionSchema) HistoryWindow(days int) CollectionSchema {
	c.HistoryRetentionDays = days
	return c
}

// Merge configures the default conflict policy on a directly composed schema.
func (c CollectionSchema) Merge(policy MergePolicy) CollectionSchema {
	c.MergePolicy = policy
	return c
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

// Index adds an ordinary b-tree index. The index is transactional with writes.
func (b *CollectionBuilder) Index(name string, fields ...string) *CollectionBuilder {
	b.indexes = append(b.indexes, IndexDef{Name: strings.TrimSpace(name), Fields: trimStrings(fields), Kind: IndexBTree})
	return b
}

// AddIndex appends a fully specified index declaration.
func (b *CollectionBuilder) AddIndex(index IndexDef) *CollectionBuilder {
	index.Name = strings.TrimSpace(index.Name)
	index.Fields = trimStrings(index.Fields)
	if index.Kind == "" {
		index.Kind = IndexBTree
	}
	b.indexes = append(b.indexes, index)
	return b
}

// UniqueIndex adds a unique ordinary index.
func (b *CollectionBuilder) UniqueIndex(name string, fields ...string) *CollectionBuilder {
	return b.Index(name, fields...).markLastIndexUnique()
}

func (b *CollectionBuilder) markLastIndexUnique() *CollectionBuilder {
	if len(b.indexes) != 0 {
		b.indexes[len(b.indexes)-1].Unique = true
	}
	return b
}

// FTS adds a full-text index. With one argument the index name is derived as
// field+"_fts"; with two arguments they are interpreted as name and field.
func (b *CollectionBuilder) FTS(args ...string) *CollectionBuilder {
	if len(args) == 0 {
		b.indexes = append(b.indexes, IndexDef{Kind: IndexFTS})
		return b
	}
	if len(args) > 2 {
		b.indexes = append(b.indexes, IndexDef{Kind: IndexFTS, Name: strings.TrimSpace(args[0]), Fields: trimStrings(args[1:])})
		return b
	}
	name, field := args[0]+"_fts", args[0]
	if len(args) > 1 {
		name, field = args[0], args[1]
	}
	b.indexes = append(b.indexes, NewFTSIndex(name, field))
	return b
}

// FullTextIndex is an explicit-name alias for FTS.
func (b *CollectionBuilder) FullTextIndex(name, field string) *CollectionBuilder {
	return b.FTS(name, field)
}

// TTL configures document expiry using a timestamp field.
func (b *CollectionBuilder) TTL(field string) *CollectionBuilder {
	ttl := NewTTL(field)
	b.ttl = &ttl
	return b
}

// HistoryRetention sets per-collection history retention in days (1-30).
func (b *CollectionBuilder) HistoryRetention(days int) *CollectionBuilder {
	b.retention, b.retentionSet = days, true
	return b
}

// HistoryWindow and HistoryRetentionDays are terminology aliases.
func (b *CollectionBuilder) HistoryWindow(days int) *CollectionBuilder {
	return b.HistoryRetention(days)
}
func (b *CollectionBuilder) HistoryRetentionDays(days int) *CollectionBuilder {
	return b.HistoryRetention(days)
}

// Merge sets the default collection conflict policy.
func (b *CollectionBuilder) Merge(policy MergePolicy) *CollectionBuilder {
	b.mergePolicy = policy
	return b
}

// MergePolicy sets the default collection conflict policy.
func (b *CollectionBuilder) MergePolicy(policy MergePolicy) *CollectionBuilder {
	return b.Merge(policy)
}

// Build validates and returns the collection schema.
func (b *CollectionBuilder) Build() (CollectionSchema, error) {
	if b == nil {
		return CollectionSchema{}, fmt.Errorf("sdk: collection builder cannot be nil")
	}
	retention := b.retention
	if !b.retentionSet && retention == 0 {
		retention = DefaultHistoryRetentionDays
	}
	cs := CollectionSchema{
		Name:                 b.name,
		Fields:               append([]FieldDef(nil), b.fields...),
		Indexes:              cloneIndexes(b.indexes),
		TTL:                  cloneTTL(b.ttl),
		HistoryRetentionDays: retention,
		MergePolicy:          b.mergePolicy,
	}
	if cs.MergePolicy == "" {
		cs.MergePolicy = MergeRemoveWins
	}
	if err := cs.Validate(); err != nil {
		return CollectionSchema{}, err
	}
	return cs, nil
}

// Validate checks the collection's identifiers, references, merge policies,
// index kinds, TTL type, and history bounds.
func (c CollectionSchema) Validate() error {
	if c.Name == "" {
		return fmt.Errorf("sdk: collection name cannot be empty")
	}
	if !validIdentifier(c.Name) {
		return fmt.Errorf("sdk: invalid collection name %q", c.Name)
	}
	if c.HistoryRetentionDays < MinHistoryRetentionDays || c.HistoryRetentionDays > MaxHistoryRetentionDays {
		return fmt.Errorf("sdk: history retention must be between %d and %d days", MinHistoryRetentionDays, MaxHistoryRetentionDays)
	}
	if !validMergePolicy(c.MergePolicy) {
		return fmt.Errorf("sdk: unknown merge policy %q (collection %q)", c.MergePolicy, c.Name)
	}
	seen := make(map[string]struct{}, len(c.Fields))
	for _, field := range c.Fields {
		if field.Name == "" {
			return fmt.Errorf("sdk: field names cannot be empty (collection %q)", c.Name)
		}
		if !validIdentifier(field.Name) {
			return fmt.Errorf("sdk: invalid field name %q (collection %q)", field.Name, c.Name)
		}
		if _, ok := seen[field.Name]; ok {
			return fmt.Errorf("sdk: duplicate field %q (collection %q)", field.Name, c.Name)
		}
		seen[field.Name] = struct{}{}
		if !validFieldType(field.Type) {
			return fmt.Errorf("sdk: unknown field type %q (collection %q)", field.Type, c.Name)
		}
		if field.MergePolicy != "" && !validCRDTType(field.MergePolicy) {
			return fmt.Errorf("sdk: field %q has invalid CRDT merge policy %q", field.Name, field.MergePolicy)
		}
	}
	seenIndexes := make(map[string]struct{}, len(c.Indexes))
	for _, index := range c.Indexes {
		if index.Name == "" || len(index.Fields) == 0 {
			return fmt.Errorf("sdk: index names and fields cannot be empty (collection %q)", c.Name)
		}
		if !validIdentifier(index.Name) {
			return fmt.Errorf("sdk: invalid index name %q (collection %q)", index.Name, c.Name)
		}
		if _, ok := seenIndexes[index.Name]; ok {
			return fmt.Errorf("sdk: duplicate index %q (collection %q)", index.Name, c.Name)
		}
		seenIndexes[index.Name] = struct{}{}
		kind := index.Kind
		if kind == "" {
			kind = IndexBTree
		}
		if kind != IndexBTree && kind != IndexFTS {
			return fmt.Errorf("sdk: unknown index kind %q (index %q)", kind, index.Name)
		}
		if kind == IndexFTS && (len(index.Fields) != 1 || index.Unique) {
			return fmt.Errorf("sdk: FTS index %q must have one non-unique field", index.Name)
		}
		for _, fieldName := range index.Fields {
			if !validIdentifier(fieldName) {
				return fmt.Errorf("sdk: invalid index field %q (index %q)", fieldName, index.Name)
			}
			field, ok := fieldByName(c.Fields, fieldName)
			if !ok {
				return fmt.Errorf("sdk: index %q references unknown field %q", index.Name, fieldName)
			}
			if kind == IndexFTS && field.Type != FieldString && field.Type != FieldText {
				return fmt.Errorf("sdk: FTS field %q must have type string or text", fieldName)
			}
		}
	}
	if c.TTL != nil {
		if !validIdentifier(c.TTL.Field) {
			return fmt.Errorf("sdk: invalid TTL field %q", c.TTL.Field)
		}
		field, ok := fieldByName(c.Fields, c.TTL.Field)
		if !ok {
			return fmt.Errorf("sdk: TTL references unknown field %q", c.TTL.Field)
		}
		if field.Type != FieldTimestamp {
			return fmt.Errorf("sdk: TTL field %q must have type timestamp", c.TTL.Field)
		}
	}
	return nil
}

// ToJSON serializes a validated collection with stable encoding/json ordering.
func (c CollectionSchema) ToJSON() (string, error) {
	if err := c.Validate(); err != nil {
		return "", err
	}
	b, err := json.Marshal(c)
	return string(b), err
}

// MarshalJSON emits the canonical index kind even when callers composed an
// IndexDef as a struct literal and left Kind empty.
func (i IndexDef) MarshalJSON() ([]byte, error) {
	kind := i.Kind
	if kind == "" {
		kind = IndexBTree
	}
	type wireIndex struct {
		Name   string    `json:"name"`
		Fields []string  `json:"fields"`
		Unique bool      `json:"unique,omitempty"`
		Kind   IndexKind `json:"kind"`
	}
	return json.Marshal(wireIndex{Name: i.Name, Fields: i.Fields, Unique: i.Unique, Kind: kind})
}

// MarshalJSON preflights a collection before allowing it onto the wire. This
// keeps direct json.Marshal calls subject to the same validation as ToJSON
// and Apply.
func (c CollectionSchema) MarshalJSON() ([]byte, error) {
	if err := c.Validate(); err != nil {
		return nil, err
	}
	type wireCollection CollectionSchema
	return json.Marshal(wireCollection(c))
}

// JSON is an alias for ToJSON.
func (c CollectionSchema) JSON() (string, error) { return c.ToJSON() }

func validFieldType(t FieldType) bool {
	switch t {
	case FieldString, FieldInt, FieldFloat, FieldBool, FieldBytes, FieldJSON,
		FieldTimestamp, FieldText, FieldCounter, FieldSetAddWins,
		FieldSetRemoveWins, FieldRegisterMV:
		return true
	default:
		return false
	}
}

func validCRDTType(t FieldType) bool {
	switch t {
	case FieldCounter, FieldSetAddWins, FieldSetRemoveWins, FieldRegisterMV:
		return true
	default:
		return false
	}
}

func validMergePolicy(p MergePolicy) bool {
	return p == MergeRemoveWins || p == MergeHLCTiebreak || p == MergeLastWriteWins
}

// Schema is the full declared database schema.
type Schema struct {
	Collections []CollectionSchema `json:"collections"`
}

// MarshalJSON emits collections in canonical name order. This keeps direct
// json.Marshal calls and request bodies identical to Schema.ToJSON.
func (s Schema) MarshalJSON() ([]byte, error) {
	if err := s.Validate(); err != nil {
		return nil, err
	}
	collections := cloneCollections(s.Collections)
	sort.SliceStable(collections, func(i, j int) bool { return collections[i].Name < collections[j].Name })
	type wireSchema struct {
		Collections []CollectionSchema `json:"collections"`
	}
	return json.Marshal(wireSchema{Collections: collections})
}

// Validate checks all collections and rejects duplicate collection names.
func (s Schema) Validate() error {
	seen := make(map[string]struct{}, len(s.Collections))
	for _, collection := range s.Collections {
		if _, ok := seen[collection.Name]; ok {
			return fmt.Errorf("sdk: duplicate collection %q", collection.Name)
		}
		seen[collection.Name] = struct{}{}
		if err := collection.Validate(); err != nil {
			return err
		}
	}
	return nil
}

// ToJSON serializes a validated root schema. Collection order is canonicalized
// by name, making equivalent builder calls produce identical JSON.
func (s Schema) ToJSON() (string, error) {
	if err := s.Validate(); err != nil {
		return "", err
	}
	collections := append([]CollectionSchema(nil), s.Collections...)
	sort.SliceStable(collections, func(i, j int) bool { return collections[i].Name < collections[j].Name })
	b, err := json.Marshal(Schema{Collections: collections})
	return string(b), err
}

// JSON is an alias for ToJSON.
func (s Schema) JSON() (string, error) { return s.ToJSON() }

// SchemaBuilder collects collection schemas into one Schema.
type SchemaBuilder struct{ collections []CollectionSchema }

// NewSchemaBuilder returns an empty schema builder.
func NewSchemaBuilder() *SchemaBuilder { return &SchemaBuilder{} }

// AddCollection adds an already-built collection schema.
func (b *SchemaBuilder) AddCollection(cs CollectionSchema) *SchemaBuilder {
	b.collections = append(b.collections, cloneCollection(cs))
	return b
}

// Add builds cb and adds it, returning the first error encountered.
func (b *SchemaBuilder) Add(cb *CollectionBuilder) (*SchemaBuilder, error) {
	if b == nil {
		return b, fmt.Errorf("sdk: schema builder cannot be nil")
	}
	cs, err := cb.Build()
	if err != nil {
		return b, err
	}
	return b.AddCollection(cs), nil
}

// Build returns the assembled schema for compatibility with the original API.
// Use TryBuild or ToJSON when validation errors must be surfaced.
func (b *SchemaBuilder) Build() Schema {
	if b == nil {
		return Schema{}
	}
	return Schema{Collections: cloneCollections(b.collections)}
}

// TryBuild validates and returns the assembled schema.
func (b *SchemaBuilder) TryBuild() (Schema, error) {
	s := b.Build()
	if err := s.Validate(); err != nil {
		return Schema{}, err
	}
	return s, nil
}

// Validate validates all collections currently in the builder.
func (b *SchemaBuilder) Validate() error {
	_, err := b.TryBuild()
	return err
}

// ToJSON serializes the complete schema document used by terrascale push.
func (b *SchemaBuilder) ToJSON() (string, error) {
	s, err := b.TryBuild()
	if err != nil {
		return "", err
	}
	return s.ToJSON()
}

// JSON is an alias for ToJSON.
func (b *SchemaBuilder) JSON() (string, error) { return b.ToJSON() }

const (
	DefaultHistoryRetentionDays = 7
	MinHistoryRetentionDays     = 1
	MaxHistoryRetentionDays     = 30
)

func validIdentifier(value string) bool {
	if value == "" || len(value) > 255 {
		return false
	}
	for i, r := range value {
		if i == 0 {
			if !(r == '_' || r >= 'A' && r <= 'Z' || r >= 'a' && r <= 'z') {
				return false
			}
			continue
		}
		if !(r == '_' || r >= 'A' && r <= 'Z' || r >= 'a' && r <= 'z' || r >= '0' && r <= '9') {
			return false
		}
	}
	return true
}

func trimStrings(values []string) []string {
	result := make([]string, len(values))
	for i, value := range values {
		result[i] = strings.TrimSpace(value)
	}
	return result
}

func fieldByName(fields []FieldDef, name string) (FieldDef, bool) {
	for _, field := range fields {
		if field.Name == name {
			return field, true
		}
	}
	return FieldDef{}, false
}

func cloneTTL(ttl *TTLDef) *TTLDef {
	if ttl == nil {
		return nil
	}
	copy := *ttl
	return &copy
}

func cloneIndexes(indexes []IndexDef) []IndexDef {
	result := make([]IndexDef, len(indexes))
	for i, index := range indexes {
		result[i] = index
		result[i].Fields = append([]string(nil), index.Fields...)
	}
	return result
}

func cloneCollection(collection CollectionSchema) CollectionSchema {
	collection.Fields = append([]FieldDef(nil), collection.Fields...)
	collection.Indexes = cloneIndexes(collection.Indexes)
	collection.TTL = cloneTTL(collection.TTL)
	return collection
}

func cloneCollections(collections []CollectionSchema) []CollectionSchema {
	result := make([]CollectionSchema, len(collections))
	for i, collection := range collections {
		result[i] = cloneCollection(collection)
	}
	return result
}
