package v1_test

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
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
	c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken})
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
	for _, id := range []string{"", "a\nb", "a\u0085b", strings.Repeat("😀", 129), "\xff"} {
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
	m, err := v1.NewManagement(v1.Config{BaseURL: peer.URL})
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
	c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	users := c.Database(testProject, "main").Collection("users")
	body.Store(`{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","version":"opaque","state":"value","document":{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}}`)
	if _, err := users.Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FAV"); err != nil {
		t.Fatal("rejected application payload ID")
	}
	body.Store(`{"id":"01arz3ndektsv4rrffq69g5fav","version":"opaque","offset":"1","durability":"local_durable"}`)
	if _, err := users.Create(t.Context(), map[string]any{"name": "Ana"}); err == nil {
		t.Fatal("accepted a lowercase generated server ID")
	}
}

func TestPayloadIDsReachHTTP(t *testing.T) {
	users, db, transport := scriptedUsers(t,
		scripted{status: http.StatusOK, body: `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD4","version":"opaque","offset":"1","durability":"local_durable"}`},
		scripted{status: http.StatusOK, body: `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD4","version":"opaque","offset":"1","durability":"local_durable","document":{"id":null}}`},
		scripted{status: http.StatusOK, body: `{"offset":"1","durability":"local_durable","results":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD4","version":"opaque","document":{"id":null}}]}`})
	payload := json.RawMessage(`{"i\u0064":null}`)
	if _, err := users.Put(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD4", payload); err != nil {
		t.Fatal(err)
	}
	if _, err := users.Patch(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD4", v1.Ops(v1.Set("/id", nil))); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Tx().Patch("users", "01ARZ3NDEKTSV4RRFFQ69G5FD4", v1.MergePatch(payload)).Commit(t.Context()); err != nil {
		t.Fatal(err)
	}
	if len(transport.requests) != 3 {
		t.Fatal("application payload id did not reach HTTP")
	}
}

func TestULIDRootSetAcceptsPayloadWithoutDuplicateID(t *testing.T) {
	users, _, transport := scriptedUsers(t, scripted{status: http.StatusOK, body: `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","version":"opaque","offset":"1","durability":"local_durable","document":{"profile":{"id":"application data"}}}`})
	patch := v1.Ops(v1.Set("", map[string]any{"profile": map[string]any{"id": "application data"}}))
	if _, err := users.Patch(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FAV", patch); err != nil {
		t.Fatal(err)
	}
	if len(transport.requests) != 1 {
		t.Fatal("root replacement without a duplicate ID must reach HTTP")
	}
}
