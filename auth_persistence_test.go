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
		if r.Header.Get("Idempotency-Key") == "" && r.Method != "GET" {
			t.Error("missing stable identity")
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/v1/databases/demo/auth/insertUnique":
			if r.Header.Get("X-TerraScale-Auth-Transaction") != "transaction-1" {
				t.Error("transaction binding missing")
			}
			raw, _ := io.ReadAll(r.Body)
			assertProducerJSON(t, raw, []byte(`{"model":"users","record":{"id":"u1","counter":9007199254740993}}`))
			_, _ = w.Write([]byte(`{"record":{"counter":9007199254740993},"matched":true}`))
		case "/api/v1/databases/demo/auth/transactions/begin":
			raw, _ := io.ReadAll(r.Body)
			assertProducerJSON(t, raw, []byte(`{"transactionId":"transaction-1"}`))
			_, _ = w.Write([]byte(`{"record":null,"matched":true,"transactionId":"transaction-1","expiresAtMs":18446744073709551615}`))
		case "/api/v1/databases/demo/auth/transactions/commit":
			w.WriteHeader(503)
			_, _ = w.Write([]byte(`{"error":{"code":"auth_outcome_unknown","message":"uncertain","fix":"resolve same identity"}}`))
		case "/api/v1/databases/demo/auth/records/users/u1":
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
