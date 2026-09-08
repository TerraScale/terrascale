package sdk

import (
	"encoding/json"
	"errors"
	"io"
	"math"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestAuthPersistenceWireAndUnknownCommit(t *testing.T) {
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/v1/databases/demo/auth/insertUnique":
			if got := r.Header.Get("Idempotency-Key"); got != "insert-1" {
				t.Errorf("insert idempotency key = %q, want %q", got, "insert-1")
			}
			if got := r.Header.Get("X-TerraScale-Auth-Transaction"); got != "transaction-1" {
				t.Errorf("insert transaction = %q, want %q", got, "transaction-1")
			}
			raw, _ := io.ReadAll(r.Body)
			assertProducerJSON(t, raw, []byte(`{"model":"users","record":{"id":"u1","counter":9007199254740993}}`))
			_, _ = w.Write([]byte(`{"record":{"counter":9007199254740993},"matched":true}`))
		case "/api/v1/databases/demo/auth/transactions/begin":
			if got := r.Header.Get("Idempotency-Key"); got != "begin-1" {
				t.Errorf("begin idempotency key = %q, want %q", got, "begin-1")
			}
			if got := r.Header.Get("X-TerraScale-Auth-Transaction"); got != "" {
				t.Errorf("begin transaction = %q, want empty", got)
			}
			raw, _ := io.ReadAll(r.Body)
			assertProducerJSON(t, raw, []byte(`{"transactionId":"transaction-1"}`))
			_, _ = w.Write([]byte(`{"record":null,"matched":true,"transactionId":"transaction-1","expiresAtMs":18446744073709551615}`))
		case "/api/v1/databases/demo/auth/transactions/commit":
			if got := r.Header.Get("Idempotency-Key"); got != "commit-1" {
				t.Errorf("commit idempotency key = %q, want %q", got, "commit-1")
			}
			if got := r.Header.Get("X-TerraScale-Auth-Transaction"); got != "" {
				t.Errorf("commit transaction = %q, want empty", got)
			}
			w.WriteHeader(503)
			_, _ = w.Write([]byte(`{"error":{"code":"auth_outcome_unknown","message":"uncertain","fix":"resolve same identity"}}`))
		case "/api/v1/databases/demo/auth/records/users/u1":
			if got := r.Header.Get("Idempotency-Key"); got != "" {
				t.Errorf("find one idempotency key = %q, want empty", got)
			}
			if got := r.Header.Get("X-TerraScale-Auth-Transaction"); got != "" {
				t.Errorf("find one transaction = %q, want empty", got)
			}
			_, _ = w.Write([]byte(`{"record":null,"matched":false}`))
		default:
			t.Errorf("unexpected auth path %s", r.URL)
			w.WriteHeader(404)
		}
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, MaxRetryAttempts: 3})
	if err != nil {
		t.Fatal(err)
	}
	begin, err := client.AuthPersistence.Begin(t.Context(), "demo", "transaction-1", "begin-1")
	if err != nil {
		t.Fatal(err)
	}
	if begin.Record != nil || begin.ExpiresAtMS == nil || *begin.ExpiresAtMS != math.MaxUint64 {
		t.Fatalf("transaction metadata lost: %+v", begin)
	}
	inserted, err := client.AuthPersistence.InsertUnique(t.Context(), "demo", AuthUniqueInsert{Model: "users", Record: AuthRecord{"id": json.RawMessage(`"u1"`), "counter": json.RawMessage(`9007199254740993`)}}, AuthPersistenceOptions{IdempotencyKey: "insert-1", TransactionID: "transaction-1"})
	if err != nil {
		t.Fatal(err)
	}
	if string(inserted.Record["counter"]) != "9007199254740993" {
		t.Fatal("record integer rounded")
	}
	_, err = client.AuthPersistence.Commit(t.Context(), "demo", "transaction-1", "commit-1")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Code != "auth_outcome_unknown" {
		t.Fatalf("unknown commit hidden: %v", err)
	}
	found, err := client.AuthPersistence.FindOne(t.Context(), "demo", "users", "u1")
	if err != nil {
		t.Fatal(err)
	}
	if found.Matched || found.Record != nil {
		t.Fatal("missing record became present")
	}
	if calls != 4 {
		t.Fatalf("uncertain commit replayed: %d calls", calls)
	}
}

func TestAuthPersistenceGeneratedMutationRoutes(t *testing.T) {
	var path string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path = r.URL.Path
		if got := r.Header.Get("Idempotency-Key"); got != "auth-key" {
			t.Errorf("idempotency key = %q, want %q", got, "auth-key")
		}
		if got := r.Header.Get("X-TerraScale-Auth-Transaction"); got != "" {
			t.Errorf("transaction = %q, want empty", got)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"matched":true}`))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, MaxRetryAttempts: 1})
	if err != nil {
		t.Fatal(err)
	}
	predicate := AuthPredicateOnly{Model: "users", Predicates: []AuthPredicate{}}
	update := AuthPredicateUpdate{Model: "users", Predicates: []AuthPredicate{}, Update: AuthRecord{"active": json.RawMessage(`true`)}}
	increment := AuthIncrement{Model: "users", Predicates: []AuthPredicate{}, Increment: map[string]float64{"logins": 1}}
	options := AuthPersistenceOptions{IdempotencyKey: "auth-key"}
	cases := []struct {
		name string
		call func() error
		want string
	}{
		{"consume matching", func() error {
			_, err := client.AuthPersistence.ConsumeMatching(t.Context(), "demo", predicate, options)
			return err
		}, "/api/v1/databases/demo/auth/consumeMatching"},
		{"count", func() error {
			_, err := client.AuthPersistence.Count(t.Context(), "demo", predicate, options)
			return err
		}, "/api/v1/databases/demo/auth/count"},
		{"delete many", func() error {
			_, err := client.AuthPersistence.DeleteMany(t.Context(), "demo", predicate, options)
			return err
		}, "/api/v1/databases/demo/auth/deleteMany"},
		{"delete one", func() error {
			_, err := client.AuthPersistence.DeleteOne(t.Context(), "demo", predicate, options)
			return err
		}, "/api/v1/databases/demo/auth/deleteOne"},
		{"increment one", func() error {
			_, err := client.AuthPersistence.IncrementOne(t.Context(), "demo", increment, options)
			return err
		}, "/api/v1/databases/demo/auth/incrementOne"},
		{"update many", func() error {
			_, err := client.AuthPersistence.UpdateMany(t.Context(), "demo", update, options)
			return err
		}, "/api/v1/databases/demo/auth/updateMany"},
		{"update one", func() error {
			_, err := client.AuthPersistence.UpdateOne(t.Context(), "demo", update, options)
			return err
		}, "/api/v1/databases/demo/auth/updateOne"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if err := tc.call(); err != nil {
				t.Fatal(err)
			}
			if path != tc.want {
				t.Fatalf("route = %q, want %q", path, tc.want)
			}
		})
	}
}
