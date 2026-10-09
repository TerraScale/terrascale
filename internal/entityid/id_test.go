package entityid

import (
	"encoding/json"
	"testing"
)

func TestModelMapsAndPayloadTags(t *testing.T) {
	type entity struct {
		ID string `ulid:"required"`
	}
	type payload struct {
		Document json.RawMessage `entity:"payload"`
	}
	for _, tc := range []struct {
		name  string
		model any
		valid bool
	}{
		{"map of entities", map[string]entity{"user": {ID: "01ARZ3NDEKTSV4RRFFQ69G5FAV"}}, true},
		{"invalid map entity", map[string]entity{"user": {ID: "legacy"}}, false},
		{"interface map entity", map[string]any{"user": entity{ID: "legacy"}}, false},
		{"untagged map data", map[string]any{"id": "application data"}, true},
		{"payload", payload{Document: json.RawMessage(`{"name":"Ana"}`)}, true},
		{"duplicate payload ID", payload{Document: json.RawMessage(`{"id":null}`)}, false},
		{"mistagged payload", struct {
			Document string `entity:"payload"`
		}{Document: `{"name":"Ana"}`}, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if err := Model(tc.model); (err == nil) != tc.valid {
				t.Fatalf("valid=%v: %v", tc.valid, err)
			}
		})
	}
}
