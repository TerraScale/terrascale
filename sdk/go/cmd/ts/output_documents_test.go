package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestJSONDocumentAndSQLFamilies(t *testing.T) {
	t.Parallel()
	const collection = `{"id":"` + jsonAuthID + `","name":"people","created_at":"1"}`
	const document = `{"id":"` + jsonAuthID + `","version":"v1","state":"value","document":{"n":9007199254740993}}`
	const write = `{"id":"` + jsonAuthID + `","version":"v1","offset":"1","durability":"local_durable"}`
	for _, tc := range []struct {
		name, body string
		args       []string
	}{
		{"collection list", `{"items":[],"next_cursor":null}`, []string{"app"}},
		{"collection create", collection, []string{"app", "people"}},
		{"collection drop", collection, []string{"app", "people"}},
		{"doc get", document, []string{"app", "people", jsonAuthID}},
		{"doc put", write, []string{"app", "people", jsonAuthID, `{"n":9007199254740993}`}},
		{"doc delete", write, []string{"app", "people", jsonAuthID}},
		{"sql", `{"matched":1,"truncated":true,"rows":[{"id":"` + jsonAuthID + `","value":{"n":9007199254740993}}]}`, []string{"app", "SELECT * FROM people"}},
		{"exec", `{"results":[{"columns":[{"name":"n","decltype":null}],"rows":[[9007199254740993]],"rows_affected":0,"last_insert_rowid":null}]}`, []string{"app", "SELECT 9007199254740993 AS n"}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				io.WriteString(w, tc.body)
			})
			var buf bytes.Buffer
			err := s.run(t.Context(), tc.name, tc.args, &jsonOutput{Writer: &buf})
			if err != nil || !json.Valid(buf.Bytes()) {
				t.Fatalf("typed document result: %v", err)
			}
			if strings.Contains(tc.body, "9007199254740993") && tc.name != "doc put" && !strings.Contains(buf.String(), "9007199254740993") {
				t.Fatal("integer precision lost")
			}
			if tc.name == "sql" && !strings.Contains(buf.String(), `"truncated":true`) {
				t.Fatal("truncation metadata missing")
			}
		})
	}
}

func TestJSONDocumentListPartialFailure(t *testing.T) {
	t.Parallel()
	s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Query().Get("cursor") != "" {
			w.WriteHeader(http.StatusForbidden)
			io.WriteString(w, `{"error":{"code":"forbidden_scope","message":"private"}}`)
		} else {
			io.WriteString(w, `{"items":[{"id":"`+jsonAuthID+`","version":"v1","state":"value","document":{}}],"next_cursor":"second"}`)
		}
	})
	var buf bytes.Buffer
	out := &jsonOutput{Writer: &buf}
	err := s.run(t.Context(), "doc list", []string{"app", "people"}, out)
	if err == nil {
		t.Fatal("page failure hidden")
	}
	if e := emitJSONError(out, err); e != nil {
		t.Fatal(e)
	}
	if !strings.Contains(buf.String(), `"type":"record"`) || !strings.Contains(buf.String(), `"type":"error"`) || strings.Contains(buf.String(), `"type":"complete"`) {
		t.Fatal("invalid partial stream")
	}
}
