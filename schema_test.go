package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestSchemaBuilderEmitsCanonicalD78JSON(t *testing.T) {
	users, err := NewCollection("users").
		Field("name", FieldString, Required(), Indexed()).
		Field("bio", FieldText).
		Field("visits", FieldCounter, MergeField(FieldCounter)).
		Field("expires_at", FieldTimestamp).
		Index("users_by_name", "name").
		FTS("bio").
		TTL("expires_at").
		HistoryRetention(30).
		Merge(MergeHLCTiebreak).
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}

	schema := NewSchemaBuilder().AddCollection(users).Build()
	got, err := schema.ToJSON()
	if err != nil {
		t.Fatalf("ToJSON: %v", err)
	}
	want := `{"collections":[{"name":"users","fields":[{"name":"name","type":"string","required":true,"indexed":true},{"name":"bio","type":"text","required":false,"indexed":false},{"name":"visits","type":"counter-pn","required":false,"indexed":false,"mergePolicy":"counter-pn"},{"name":"expires_at","type":"timestamp","required":false,"indexed":false}],"indexes":[{"name":"users_by_name","fields":["name"],"kind":"btree"},{"name":"bio_fts","fields":["bio"],"kind":"fts"}],"ttl":{"field":"expires_at"},"historyRetentionDays":30,"mergePolicy":"hlc-tiebreak"}]}`
	if got != want {
		t.Fatalf("schema JSON differs\n got: %s\nwant: %s", got, want)
	}
	if !json.Valid([]byte(got)) {
		t.Fatal("schema JSON is invalid")
	}
}

func TestSchemaBuilderDefaultsAndSortsCollections(t *testing.T) {
	first, err := NewCollection("zebra").Build()
	if err != nil {
		t.Fatal(err)
	}
	second, err := NewCollection("accounts").Build()
	if err != nil {
		t.Fatal(err)
	}
	schema := NewSchemaBuilder().AddCollection(first).AddCollection(second).Build()
	if schema.Collections[0].HistoryRetentionDays != DefaultHistoryRetentionDays {
		t.Fatalf("default history = %d", schema.Collections[0].HistoryRetentionDays)
	}
	jsonText, err := schema.ToJSON()
	if err != nil {
		t.Fatal(err)
	}
	accountsAt := strings.Index(jsonText, `"name":"accounts"`)
	zebraAt := strings.Index(jsonText, `"name":"zebra"`)
	if accountsAt < 0 || zebraAt < 0 || accountsAt >= zebraAt {
		t.Fatalf("collections are not in canonical order: %s", jsonText)
	}
	// Direct marshaling is canonical too, which is what Apply uses for the
	// request body.
	direct, err := json.Marshal(schema)
	if err != nil {
		t.Fatal(err)
	}
	if string(direct) != jsonText {
		t.Fatalf("MarshalJSON differs from ToJSON\n direct: %s\n toJSON: %s", direct, jsonText)
	}
}

func TestSchemaBuilderValidationRejectsInvalidReferencesAndPolicies(t *testing.T) {
	cases := []struct {
		name  string
		build func() (CollectionSchema, error)
	}{
		{"history below minimum", func() (CollectionSchema, error) {
			return NewCollection("events").HistoryRetention(0).Build()
		}},
		{"history above maximum", func() (CollectionSchema, error) {
			return NewCollection("events").HistoryRetention(31).Build()
		}},
		{"unknown index field", func() (CollectionSchema, error) {
			return NewCollection("events").Index("by_missing", "missing").Build()
		}},
		{"TTL must reference timestamp", func() (CollectionSchema, error) {
			return NewCollection("events").Field("expires_at", FieldString).TTL("expires_at").Build()
		}},
		{"invalid merge kernel", func() (CollectionSchema, error) {
			return NewCollection("events").Field("value", FieldString, MergeField(FieldString)).Build()
		}},
		{"invalid collection identifier", func() (CollectionSchema, error) {
			return NewCollection("bad-name").Build()
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := tc.build(); err == nil {
				t.Fatal("expected validation error")
			}
		})
	}
}

func TestSchemaBuilderRejectsDuplicateCollectionsAndSupportsIndexSlice(t *testing.T) {
	collection, err := NewCollection("events").
		Field("tenant", FieldString).
		Build()
	if err != nil {
		t.Fatal(err)
	}
	collection.Indexes = append(collection.Indexes, NewIndex("events_by_tenant", []string{"tenant"}))
	if err := collection.Validate(); err != nil {
		t.Fatalf("slice index should validate: %v", err)
	}
	builder := NewSchemaBuilder().AddCollection(collection).AddCollection(collection)
	if _, err := builder.TryBuild(); err == nil {
		t.Fatal("expected duplicate collection error")
	}
}

func TestSchemaBuilderSupportsDirectCollectionComposition(t *testing.T) {
	collection := NewCollectionSchema("events").
		Field("expires_at", FieldTimestamp, Required()).
		Index(NewIndex("events_by_expiry", "expires_at")).
		TTLField("expires_at").
		HistoryWindow(1).
		Merge(MergeRemoveWins)
	if err := collection.Validate(); err != nil {
		t.Fatalf("direct schema composition: %v", err)
	}
}

func TestSchemaJSONPreflightsInvalidDirectSchemas(t *testing.T) {
	invalid := Schema{Collections: []CollectionSchema{{
		Name:                 "events",
		HistoryRetentionDays: DefaultHistoryRetentionDays,
		MergePolicy:          MergeRemoveWins,
		Fields:               []FieldDef{{Name: "title", Type: FieldString}},
		Indexes:              []IndexDef{{Name: "search", Fields: []string{"title", "missing"}, Kind: IndexFTS}},
	}}}
	if _, err := json.Marshal(invalid); err == nil {
		t.Fatal("expected direct schema marshaling to preflight validation")
	}
}

func TestSchemaJSONCanonicalizesBTreeAndFTSIndexes(t *testing.T) {
	collection := CollectionSchema{
		Name:                 "docs",
		Fields:               []FieldDef{{Name: "title", Type: FieldText}},
		Indexes:              []IndexDef{{Name: "docs_by_title", Fields: []string{"title"}}, {Name: "docs_fts", Fields: []string{"title"}, Kind: IndexFTS}},
		HistoryRetentionDays: DefaultHistoryRetentionDays,
		MergePolicy:          MergeRemoveWins,
	}
	got, err := collection.ToJSON()
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(got, `"kind":"btree"`) || !strings.Contains(got, `"kind":"fts"`) {
		t.Fatalf("index kinds were not canonicalized: %s", got)
	}
}

func TestSchemaApplyFailsClosedWithoutRequest(t *testing.T) {
	var requests int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests++
		http.Error(w, "unexpected schema request", http.StatusInternalServerError)
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}
	schema, err := NewCollection("users").Field("name", FieldString).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	var gap *ContractGapError
	_, err = Schema{Collections: []CollectionSchema{schema}}.Apply(context.Background(), client, "db1")
	if !errors.As(err, &gap) {
		t.Fatalf("Apply error = %T: %v, want ContractGapError", err, err)
	}
	if gap.Operation != "schema.apply" || gap.Method != http.MethodPut || gap.Path != "/api/v1/databases/{name}/schema" {
		t.Fatalf("Apply gap metadata = %+v", gap)
	}
	if requests != 0 {
		t.Fatalf("Apply issued %d request(s) for an unpublished operation", requests)
	}
}
