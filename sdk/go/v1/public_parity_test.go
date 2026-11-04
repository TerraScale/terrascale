package v1_test

import (
	"encoding/json"

	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const parityProject = "01ARZ3NDEKTSV4RRFFQ69G5FAV"

const parityDatabase = "/v1/projects/" + parityProject + "/databases/main"

// These fixtures assert the producer's wire contract without running TerraBase.
func parityClient(t *testing.T, method, path, query, body, response string, status int) *tb.Client {
	t.Helper()
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.Method != method || r.URL.Path != "/sdk-test"+path || r.URL.RawQuery != query {
			t.Errorf("request = %s %s; want %s %s?%s", r.Method, r.URL, method, path, query)
		}
		if r.Header.Get("Authorization") != "Bearer header.project.signature" || r.Header.Get("X-Terrabase-User-Id") != "" {
			t.Error("incorrect public authorization")
		}
		raw, err := io.ReadAll(r.Body)
		if err != nil {
			t.Error(err)
		}
		if body == "" {
			if len(raw) != 0 {
				t.Error("unexpected request body")
			}
		} else {
			var got, want any
			if json.Unmarshal(raw, &got) != nil || json.Unmarshal([]byte(body), &want) != nil || !reflect.DeepEqual(got, want) {
				t.Errorf("request body = %s; want %s", raw, body)
			}
			if r.Header.Get("Content-Type") != "application/json" {
				t.Error("incorrect JSON content type")
			}
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		io.WriteString(w, response)
	}))
	t.Cleanup(server.Close)
	t.Cleanup(func() {
		if calls != 1 {
			t.Errorf("requests = %d; want 1", calls)
		}
	})
	c, err := tb.New(tb.Config{BaseURL: server.URL + "/sdk-test", Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	return c
}
