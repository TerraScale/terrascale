package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func jsonDatabaseSession(t *testing.T, handler http.HandlerFunc) *session {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	client, err := v1.New(v1.Config{BaseURL: server.URL, Token: "fixture.jwt.signature"})
	if err != nil {
		t.Fatal(err)
	}
	return &session{tb: client, project: jsonAuthID}
}

func TestJSONMetadataFamilies(t *testing.T) {
	t.Parallel()
	const object = `{"id":"` + jsonAuthID + `","databaseId":"` + jsonAuthID + `","projectId":"` + jsonAuthID + `","project":"` + jsonAuthID + `","name":"app"}`
	for _, tc := range []struct {
		name, body, member string
		args               []string
	}{
		{"backup list", `{"items":[],"next":""}`, "", []string{"app"}},
		{"backup create", object, "id", []string{"app"}},
		{"backup restore", object, "name", []string{"app", jsonAuthID, "copy"}},
		{"backup restore-at", object, "name", []string{"app", "2026-10-26T00:00:00Z", "copy"}},
		{"schema get", `{"schema":null}`, "schema", []string{"app", "people"}},
		{"schema set", `{"schema":{"type":"object"}}`, "schema", []string{"app", "people", `{"type":"object"}`}},
		{"schema delete", `{"schema":null}`, "schema", []string{"app", "people"}},
		{"ttl get", `{"path":null}`, "path", []string{"app", "people"}},
		{"ttl set", `{"path":"/expiresAt"}`, "path", []string{"app", "people", "/expiresAt"}},
		{"ttl delete", ``, "path", []string{"app", "people"}},
		{"index list", `{"items":[]}`, "", []string{"app"}},
		{"index create", `{"name":"idx","collection":"people","fields":["name"],"state":"ready"}`, "name", []string{"app", "people", "name"}},
		{"index drop", ``, "dropped", []string{"app", "idx"}},
		{"usage", `{"storageBytes":0,"documents":0,"reads":0,"writes":0}`, "", nil},
		{"import status", object, "id", []string{"app"}},
		{"import cancel", object, "id", []string{"app"}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				if tc.body == "" {
					w.WriteHeader(http.StatusNoContent)
				} else {
					io.WriteString(w, tc.body)
				}
			})
			var buf bytes.Buffer
			err := s.run(t.Context(), tc.name, tc.args, &jsonOutput{Writer: &buf})
			if err != nil || !json.Valid(buf.Bytes()) {
				t.Fatalf("typed metadata result: %v", err)
			}
			if tc.member != "" && !strings.Contains(buf.String(), `"`+tc.member+`":`) {
				t.Fatal("expected result field missing")
			}
			if tc.name == "backup list" || tc.name == "index list" {
				if buf.String() != "[]\n" {
					t.Fatal("empty list must be []")
				}
			}
		})
	}
}
