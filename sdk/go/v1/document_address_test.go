package v1_test

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/sdk/v1"
)

func TestDocumentIDsAtHTTPBoundary(t *testing.T) {
	const base = "/v1/projects/" + testProject + "/databases/main/collections/users/documents"
	peer := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if strings.HasSuffix(r.URL.Path, "/transactions") {
			var body struct {
				Ops []struct {
					ID string `json:"id"`
				} `json:"ops"`
			}
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				t.Error(err)
			}
			fmt.Fprintf(w, `{"offset":"1","durability":"local_durable","results":[{"id":%q,"version":"opaque"}]}`, body.Ops[0].ID)
			return
		}
		query := r.URL.Query()
		if r.URL.Path != base+"/by-key" || len(query) != 1 || len(query["id"]) != 1 {
			t.Errorf("point request must carry exactly one query key: %s", r.URL)
		}
		id := query.Get("id")
		switch r.Method {
		case http.MethodGet:
			if r.Header.Get("If-None-Match") != `"previous"` {
				t.Error("lost read precondition")
			}
		case http.MethodPut:
			if r.Header.Get("If-None-Match") != "*" {
				t.Error("lost create precondition")
			}
		case http.MethodPatch, http.MethodDelete:
			if r.Header.Get("If-Match") != `"opaque"` {
				t.Error("lost write precondition")
			}
		}
		w.Header().Set("Location", r.URL.RequestURI())
		fmt.Fprintf(w, `{"id":%q,"version":"opaque","offset":"1","durability":"local_durable","state":"value","document":{"name":"Ana"}}`, id)
	}))
	t.Cleanup(peer.Close)
	c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken, MaxRetries: -1, HTTPClient: peer.Client()})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(testProject, "main")
	for _, key := range []string{v1.NewID(), testProject} {
		doc, err := db.Collection("users").Get(t.Context(), key, v1.IfNoneMatch("previous"))
		if err != nil || doc.ID != key {
			t.Fatalf("get document ID %q: %v", key, err)
		}
		res, err := db.Collection("users").Put(t.Context(), key, map[string]any{"name": "Ana"}, v1.IfNoneMatchAny())
		if err != nil || res.ID != key {
			t.Fatalf("put document ID %q: %v", key, err)
		}
		patched, err := db.Collection("users").Patch(t.Context(), key, v1.MergePatch(map[string]any{"name": "Bia"}), v1.IfMatch(res.Version))
		if err != nil || patched.ID != key {
			t.Fatalf("patch document ID %q: %v", key, err)
		}
		deleted, err := db.Collection("users").Delete(t.Context(), key, v1.IfMatch(patched.Version))
		if err != nil || deleted.ID != key {
			t.Fatalf("delete document ID %q: %v", key, err)
		}
		tx, err := db.Tx().Create("users", key, map[string]any{"name": "Ana"}).Commit(t.Context())
		if err != nil || tx.Results[0].ID != key {
			t.Fatalf("transaction document ID: %v", err)
		}
	}
	if _, err := c.Database("0190a6e4-7c3b-7d2e-8f00-0123456789ab", "main").Collection("users").Get(t.Context(), "ana"); err == nil {
		t.Fatal("accepted a non-ULID entity selector")
	}
}

func TestTransactionGeneratedKeysAreULIDs(t *testing.T) {
	for _, key := range []string{v1.NewID(), "01arz3ndektsv4rrffq69g5fav", "0190a6e4-7c3b-7d2e-8f00-0123456789ab"} {
		t.Run(key, func(t *testing.T) {
			peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				fmt.Fprintf(w, `{"offset":"1","durability":"local_durable","results":[{"id":%q,"version":"opaque"}]}`, key)
			}))
			t.Cleanup(peer.Close)
			c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken, MaxRetries: -1})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database(testProject, "main").Tx().Create("users", "", map[string]any{}).Commit(t.Context())
			if (err == nil) != (v1.ValidateID(key) == nil) {
				t.Fatalf("generated key contract: %v", err)
			}
		})
	}
}
