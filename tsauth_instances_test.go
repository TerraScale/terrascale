package sdk

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

// tsAuthBootstrapServer mirrors ts-auth's POST /v1/instances
// (crates/auth-app/src/features/instances/mod.rs): operator bearer, a
// 16-128 visible ASCII Idempotency-Key, payload-bound replay, slug
// uniqueness and 503 while the outcome is not visible.
type tsAuthBootstrapServer struct {
	mu          sync.Mutex
	operatorKey string
	requests    []string // Idempotency-Key per request
	byKey       map[string]tsAuthBootstrapRecord
	bySlug      map[string]string // slug -> instance id
	unavailable int               // answer 503 to this many requests
}

type tsAuthBootstrapRecord struct {
	payloadHash, instanceID, slug, name, apiKey string
}

func newTSAuthBootstrapServer(t *testing.T) (*tsAuthBootstrapServer, *httptest.Server) {
	s := &tsAuthBootstrapServer{operatorKey: "operator-secret", byKey: map[string]tsAuthBootstrapRecord{}, bySlug: map[string]string{}}
	srv := httptest.NewServer(http.HandlerFunc(s.serve))
	t.Cleanup(srv.Close)
	return s, srv
}

func (s *tsAuthBootstrapServer) serve(w http.ResponseWriter, r *http.Request) {
	s.mu.Lock()
	defer s.mu.Unlock()
	reply := func(status int, body any) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		json.NewEncoder(w).Encode(body)
	}
	fail := func(status int, code string) {
		reply(status, map[string]any{"error": map[string]string{"code": code, "message": code}})
	}
	if r.Method != http.MethodPost || r.URL.Path != "/v1/instances" {
		fail(http.StatusNotFound, "not_found")
		return
	}
	key := strings.TrimSpace(r.Header.Get("Idempotency-Key"))
	s.requests = append(s.requests, key)
	if r.Header.Get("Authorization") != "Bearer "+s.operatorKey {
		fail(http.StatusUnauthorized, "unauthorized")
		return
	}
	if len(key) < 16 || len(key) > 128 {
		fail(http.StatusBadRequest, "idempotency_key_required")
		return
	}
	var body struct{ Slug, Name *string }
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Slug == nil || body.Name == nil {
		fail(http.StatusBadRequest, "invalid_request")
		return
	}
	if s.unavailable > 0 {
		s.unavailable--
		fail(http.StatusServiceUnavailable, "temporarily_unavailable")
		return
	}
	slug, name := strings.ToLower(strings.TrimSpace(*body.Slug)), strings.TrimSpace(*body.Name)
	sum := sha256.Sum256([]byte(slug + "\x00" + name))
	hash := hex.EncodeToString(sum[:])
	rec, ok := s.byKey[key]
	if !ok {
		n := len(s.byKey) + 1
		rec = tsAuthBootstrapRecord{payloadHash: hash, instanceID: "ins_" + strings.Repeat("a", n), slug: slug, name: name, apiKey: "sk_" + strings.Repeat("b", n) + "_secret"}
		s.byKey[key] = rec
	}
	if rec.payloadHash != hash {
		fail(http.StatusConflict, "idempotency_key_reused")
		return
	}
	if existing, ok := s.bySlug[slug]; ok && existing != rec.instanceID {
		fail(http.StatusConflict, "instance_exists")
		return
	}
	s.bySlug[slug] = rec.instanceID
	reply(http.StatusOK, map[string]any{"instance": map[string]string{"id": rec.instanceID, "slug": rec.slug, "name": rec.name}, "api_key": rec.apiKey})
}

func TestTSAuthCreateInstance(t *testing.T) {
	server, srv := newTSAuthBootstrapServer(t)
	client, err := NewClient(Config{TSAuthBaseURL: srv.URL, AccessToken: server.operatorKey, RetryDelay: time.Millisecond})
	if err != nil {
		t.Fatal(err)
	}
	ctx := t.Context()
	const key = "dashboard-project-0001"

	// The first attempts hit 503 while the outcome is not visible; the
	// client retries with the same key.
	server.unavailable = 2
	created, err := client.TSAuth.CreateInstance(ctx, "Acme-Prod", "Acme", key)
	if err != nil {
		t.Fatal(err)
	}
	if created.Instance.Slug != "acme-prod" || created.Instance.ID == "" || !strings.HasPrefix(created.APIKey, "sk_") {
		t.Fatalf("created: %+v", created.Instance)
	}
	if len(server.requests) != 3 || server.requests[0] != key || server.requests[2] != key {
		t.Fatalf("requests: %v", server.requests)
	}
	if strings.Contains(created.String(), created.APIKey) {
		t.Fatal("String leaked the API key")
	}

	// A replay returns the same instance and key.
	again, err := client.TSAuth.CreateInstance(ctx, "acme-prod", "Acme", key)
	if err != nil || again.Instance != created.Instance || again.APIKey != created.APIKey {
		t.Fatalf("replay: %+v (%v)", again, err)
	}

	cases := []struct {
		name, slug, label, key string
		want                   error
		code                   string
	}{
		{"key reused with another name", "acme-prod", "Other", key, ErrTSAuthIdempotencyKeyReused, "idempotency_key_reused"},
		{"slug taken under another key", "acme-prod", "Acme", "dashboard-project-0002", ErrTSAuthInstanceExists, "instance_exists"},
	}
	for _, tc := range cases {
		before := len(server.requests)
		_, err := client.TSAuth.CreateInstance(ctx, tc.slug, tc.label, tc.key)
		var apiErr *APIError
		if !errors.Is(err, tc.want) || !errors.As(err, &apiErr) || apiErr.Code != tc.code || apiErr.Status != http.StatusConflict {
			t.Fatalf("%s: %v", tc.name, err)
		}
		if len(server.requests) != before+1 {
			t.Fatalf("%s: a 409 was retried", tc.name)
		}
	}

	// Persistent 503: the retries run out and the caller learns to retry.
	server.unavailable = 10
	before := len(server.requests)
	_, err = client.TSAuth.CreateInstance(ctx, "beta", "Beta", "dashboard-project-0003")
	if !errors.Is(err, ErrTSAuthUnavailable) || len(server.requests) != before+1+tsAuthKeyedRetries {
		t.Fatalf("unavailable: %v after %d requests", err, len(server.requests)-before)
	}
	server.unavailable = 0

	// A wrong operator key is rejected without retries.
	wrong, err := NewClient(Config{TSAuthBaseURL: srv.URL, AccessToken: "not-the-operator", RetryDelay: time.Millisecond})
	if err != nil {
		t.Fatal(err)
	}
	before = len(server.requests)
	if _, err := wrong.TSAuth.CreateInstance(ctx, "gamma", "Gamma", "dashboard-project-0004"); !errors.Is(err, ErrTSAuthUnauthorized) || len(server.requests) != before+1 {
		t.Fatalf("unauthorized: %v", err)
	}

	// Invalid input never reaches the server.
	before = len(server.requests)
	for _, call := range []struct{ slug, name, key string }{
		{"acme", "Acme", "short"},
		{"acme", "Acme", strings.Repeat("k", 129)},
		{"acme", "Acme", "has a space in key!"},
		{"a", "Acme", key},
		{"acme_prod", "Acme", key},
		{"acme", " ", key},
	} {
		var validation *ValidationError
		if _, err := client.TSAuth.CreateInstance(ctx, call.slug, call.name, call.key); !errors.As(err, &validation) {
			t.Fatalf("%+v: want a ValidationError, got %v", call, err)
		}
	}
	if len(server.requests) != before {
		t.Fatal("invalid input reached the server")
	}
}
