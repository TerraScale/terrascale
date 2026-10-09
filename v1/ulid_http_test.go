package v1_test

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"

	"github.com/oklog/ulid/v2"
	sdk "terrascale.tech/sdk"
	"terrascale.tech/sdk/v1"
)

// Exercise generated IDs through the HTTP request boundary. The peer returns
// only a protocol envelope; no storage or persistence behavior is simulated.
func TestULIDHTTPBoundary(t *testing.T) {
	var previous string
	var mu sync.Mutex
	var calls atomic.Int64
	peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		mu.Lock()
		defer mu.Unlock()
		id := r.URL.Query().Get("id")
		parsed, err := ulid.ParseStrict(id)
		if err != nil || parsed.String() != id || (previous != "" && id <= previous) {
			t.Errorf("noncanonical or nonmonotonic request ID")
		}
		previous = id
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, `{"id":%q,"version":"opaque","state":"value","document":{"natural_key":"user@example.com"}}`, id)
	}))
	t.Cleanup(peer.Close)
	c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken, MaxRetries: -1})
	if err != nil {
		t.Fatal(err)
	}
	users := c.Database(testProject, "main").Collection("users")
	for i := range 128 {
		id := sdk.NewID()
		if i%2 == 0 {
			id = v1.NewID()
		}
		doc, err := users.Get(t.Context(), id)
		if err != nil || doc.ID != id {
			t.Fatalf("generated ID round trip: %v", err)
		}
		var payload map[string]any
		if err := doc.Decode(&payload); err != nil {
			t.Fatal(err)
		}
		if _, repeated := payload["id"]; repeated {
			t.Fatal("payload repeated the entity ID")
		}
	}
	before := calls.Load()
	for _, id := range []string{"", "doc_01ARZ3NDEKTSV4RRFFQ69G5FAV", "01arz3ndektsv4rrffq69g5fav", "0190a6e4-7c3b-7d2e-8f00-0123456789ab", "81ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAI", "01ARZ3NDEKTSV4RRFFQ69G5FA", "legacy id"} {
		if _, err := users.Get(t.Context(), id); err == nil {
			t.Fatal("accepted an invalid document ID")
		}
		if _, err := users.Put(t.Context(), id, map[string]any{}); err == nil {
			t.Fatal("accepted an invalid write ID")
		}
		if _, err := c.Database(testProject, "main").Tx().Delete("users", id).Commit(t.Context()); err == nil {
			t.Fatal("accepted an invalid transaction ID")
		}
	}
	if calls.Load() != before {
		t.Fatal("invalid IDs reached HTTP")
	}
}

func TestULIDMalformedHTTPResponses(t *testing.T) {
	var body atomic.Value
	body.Store(`{"id":"org_legacy","name":"Acme"}`)
	peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, body.Load())
	}))
	t.Cleanup(peer.Close)
	m, err := v1.NewManagement(v1.Config{BaseURL: peer.URL, MaxRetries: -1})
	if err != nil {
		t.Fatal(err)
	}
	u := m.WithToken("test-access-token")
	if _, err := u.Organization(t.Context(), testOrg); err == nil {
		t.Fatal("accepted a prefixed organization ID")
	}
	body.Store(`{"items":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","organizationId":"org_legacy"}]}`)
	if _, err := u.Projects(t.Context(), testOrg, v1.PageOptions{}); err == nil {
		t.Fatal("accepted an invalid nested reference")
	}
	c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken, MaxRetries: -1})
	if err != nil {
		t.Fatal(err)
	}
	users := c.Database(testProject, "main").Collection("users")
	body.Store(`{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","version":"opaque","state":"value","document":{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}}`)
	if _, err := users.Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FAV"); err == nil {
		t.Fatal("accepted a repeated payload ID")
	}
	body.Store(`{"id":"01arz3ndektsv4rrffq69g5fav","version":"opaque","offset":"1","durability":"local_durable"}`)
	if _, err := users.Create(t.Context(), map[string]any{"name": "Ana"}); err == nil {
		t.Fatal("accepted a lowercase generated server ID")
	}
}

func TestULIDWritesRejectPayloadIDsBeforeHTTP(t *testing.T) {
	var calls atomic.Int64
	peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.WriteHeader(http.StatusInternalServerError)
	}))
	t.Cleanup(peer.Close)
	c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken, MaxRetries: -1})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(testProject, "main")
	users := db.Collection("users")
	id := v1.NewID()
	for _, payload := range []any{map[string]any{"id": id}, json.RawMessage(`{"id":null}`)} {
		if _, err := users.Create(t.Context(), payload); err == nil {
			t.Fatal("accepted a repeated create ID")
		}
		if _, err := users.Put(t.Context(), id, payload); err == nil {
			t.Fatal("accepted a repeated put ID")
		}
		if _, err := db.Tx().Put("users", id, payload).Commit(t.Context()); err == nil {
			t.Fatal("accepted a repeated transaction ID")
		}
		if _, err := users.Patch(t.Context(), id, v1.MergePatch(payload)); err == nil {
			t.Fatal("accepted a repeated merge-patch ID")
		}
	}
	if _, err := users.Patch(t.Context(), id, v1.Ops(v1.Set("/id", id))); err == nil {
		t.Fatal("accepted an id patch")
	}
	if calls.Load() != 0 {
		t.Fatal("invalid payloads reached HTTP")
	}
}
