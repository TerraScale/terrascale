package sdk

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestNativeDefaultRoutingFailsClosed(t *testing.T) {
	calls := 0
	transport := pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) { calls++; return nil, errors.New("unexpected dispatch") })
	client, err := NewClient(Config{HTTPClient: &http.Client{Transport: transport}})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.Documents.Get(t.Context(), nativeTestScope, nativeTestCollection, nativeTestDocument)
	var routeErr *RoutingError
	if !errors.As(err, &routeErr) || calls != 0 {
		t.Fatalf("default fell back: %v calls=%d", err, calls)
	}
	for _, origin := range []string{
		"https://api.terrascale.tech", "https://API.TERRASCALE.TECH.",
		"https://gru.api.terrascale.tech", "https://gru.api.terrascale.tech.",
		"https://api。terrascale。tech", "https://api．terrascale．tech",
		"https://ａｐｉ.terrascale.tech", "https://gru.ａｐｉ.terrascale.tech",
		"https://gru.terrascale.tech/path", "https://gru.terrascale.tech?", "https://:",
		"http://gru.terrascale.tech",
	} {
		if _, err := NewClient(Config{DataBaseURL: origin}); err == nil {
			t.Errorf("unsafe native origin accepted: %s", origin)
		}
	}
}

func TestNativeCredentialsStaySeparateFromManagement(t *testing.T) {
	for _, test := range []struct {
		name     string
		static   string
		provider func() string
		want     string
	}{
		{name: "no native credential"},
		{name: "native static", static: "native-static", want: "Bearer native-static"},
		{name: "native provider", static: "native-static", provider: func() string { return "native-provider" }, want: "Bearer native-provider"},
		{name: "empty native provider", static: "native-static", provider: func() string { return "" }},
	} {
		t.Run(test.name, func(t *testing.T) {
			managementCalls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if got := r.Header.Get("Authorization"); got != test.want {
					t.Errorf("native authorization = %q, want %q", got, test.want)
				}
				w.Header().Set("Content-Type", "application/json")
				_, _ = w.Write([]byte(nativeAbsentPoint))
			}))
			t.Cleanup(server.Close)
			client, err := NewClient(Config{
				DataBaseURL: server.URL, DataAccessToken: test.static, DataTokenProvider: test.provider,
				APIBaseURL: server.URL, TSAuthBaseURL: server.URL, AccessToken: "management-static",
				TokenProvider: func() string { managementCalls++; return "management-provider" },
			})
			if err != nil {
				t.Fatal(err)
			}
			if _, err := client.Documents.Get(t.Context(), nativeTestScope, nativeTestCollection, nativeTestDocument); err != nil {
				t.Fatal(err)
			}
			if managementCalls != 0 {
				t.Fatal("native request consulted management token provider")
			}
		})
	}
}

func TestNativeUnexpectedCommitStatusNeverReplays(t *testing.T) {
	request, err := NewCommitRequest(nativeTestScope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{nativeTestWrite()})
	if err != nil {
		t.Fatal(err)
	}
	for _, test := range []struct {
		status    int
		code      string
		uncertain bool
	}{{202, "precondition_failed", true}, {400, "invalid_request", false}, {409, "schema_changed", false}, {408, "precondition_failed", true}, {307, "precondition_failed", true}, {429, "rate_limited", true}, {503, "precondition_failed", true}, {503, "commit_uncertain", true}, {503, "authority_unavailable", false}, {412, "precondition_failed", false}, {409, "idempotency_conflict", false}} {
		t.Run(http.StatusText(test.status)+test.code, func(t *testing.T) {
			calls := 0
			client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
				calls++
				w.Header().Set("Content-Type", "application/json")
				w.Header().Set("Location", "https://gru.terrascale.tech")
				w.WriteHeader(test.status)
				_, _ = w.Write([]byte(`{"error":{"code":"` + test.code + `"}}`))
			})
			_, err := client.Documents.Commit(t.Context(), nativeTestScope, request)
			var commitErr *CommitError
			if !errors.As(err, &commitErr) || commitErr.Uncertain != test.uncertain || calls != 1 {
				t.Fatalf("wrong uncertainty/replay: %v calls=%d", err, calls)
			}
		})
	}
	client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(412)
		_, _ = w.Write([]byte(`{"error":{"Code":"precondition_failed"}}`))
	})
	_, err = client.Documents.Commit(t.Context(), nativeTestScope, request)
	var commitErr *CommitError
	if !errors.As(err, &commitErr) || !commitErr.Uncertain {
		t.Fatal("ambiguous rejection body was definitive")
	}
}

func TestNativeUncertainRecoveryOwnsOriginalOperation(t *testing.T) {
	request, err := NewCommitRequest(nativeTestScope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{nativeTestWrite()})
	if err != nil {
		t.Fatal(err)
	}
	receipt := nativeTestReceipt(request)
	var first []byte
	commits := 0
	client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		body, err := io.ReadAll(r.Body)
		if err != nil {
			t.Error(err)
		}
		if strings.HasSuffix(r.URL.Path, "/status") {
			var status TransactionStatusRequest
			if err := decodeNativeJSON(body, &status); err != nil {
				t.Error(err)
			}
			if status.EffectDigest != receipt.EffectDigest || status.TransactionID != receipt.TransactionID {
				t.Error("recovery status identity changed")
			}
			_ = json.NewEncoder(w).Encode(TransactionStatusResponse{Version: 1, State: TransactionUnknown, TransactionID: receipt.TransactionID, Authority: receipt.Authority, EffectDigest: receipt.EffectDigest})
			return
		}
		commits++
		if commits == 1 {
			first = bytes.Clone(body)
			w.WriteHeader(503)
			_, _ = w.Write([]byte(`{"error":{"code":"commit_uncertain"}}`))
			return
		}
		if !bytes.Equal(first, body) {
			t.Error("recovery replay changed exact request bytes")
		}
		_ = json.NewEncoder(w).Encode(receipt)
	})
	_, err = client.Documents.Commit(t.Context(), nativeTestScope, request)
	var commitErr *CommitError
	if !errors.As(err, &commitErr) || !commitErr.Uncertain || commitErr.Operation == nil {
		t.Fatalf("recovery handle missing: %v", err)
	}
	request.Writes[0].Value.Fields[0].Value = I64Value(99)
	request.TransactionID.Nonce[0] ^= 1
	copy, err := commitErr.Operation.Request()
	if err != nil {
		t.Fatal(err)
	}
	copy.Writes[0].Value.Fields[0].Value = BoolValue(false)
	status, err := commitErr.Operation.Status(t.Context())
	if err != nil || status.Definitive() {
		t.Fatalf("unknown status lost: %+v %v", status, err)
	}
	if _, err := commitErr.Operation.Retry(t.Context()); err != nil {
		t.Fatal(err)
	}
	if commits != 2 {
		t.Fatal("automatic replay occurred")
	}
}

func TestNativeSingleWriterRequiresAuthenticatedFence(t *testing.T) {
	writer := WriterIdentity{Replica: nativeTestCollection, Incarnation: Hex16{1}}
	request, err := NewCommitRequest(nativeTestScope, nativeTestID, MutationAuthority{Mode: AuthoritySingleWriter, Generation: 1, Writer: &writer}, []DocumentWrite{nativeTestWrite()})
	if err != nil {
		t.Fatal(err)
	}
	client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) { t.Error("single writer dispatched without admission") })
	_, err = client.Documents.Commit(t.Context(), nativeTestScope, request)
	if err == nil || !strings.Contains(err.Error(), "fence") {
		t.Fatalf("missing fence accepted: %v", err)
	}
}
