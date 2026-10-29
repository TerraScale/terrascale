package sdk

import (
	"net/http"
	"testing"
	"time"
)

type pinningRoundTripperFunc func(*http.Request) (*http.Response, error)

func (f pinningRoundTripperFunc) RoundTrip(r *http.Request) (*http.Response, error) {
	return f(r)
}

func TestErrorFromResponseDecodesBoundedNestedDiagnostic(t *testing.T) {
	headers := make(http.Header)
	headers.Set("X-Request-ID", "request-1")
	err := errorFromResponse("documents.get", http.MethodGet, "/api/v1/documents/1", http.StatusConflict, headers, []byte(`{"error":{"code":"conflict","message":"document changed","fix":"refresh and retry"}}`))
	if err.Code != "conflict" || err.Message != "document changed" || err.Fix != "refresh and retry" || err.RequestID != "request-1" {
		t.Fatalf("decoded error = %+v", err)
	}
}

func TestRetryAfterIsBoundedAndRejectsInvalidValues(t *testing.T) {
	for _, test := range []struct {
		value string
		want  time.Duration
		ok    bool
	}{
		{"2", 2 * time.Second, true},
		{"999999999999999999999999", time.Duration((1<<63-1)/int64(time.Second)) * time.Second, true},
		{"-1", 0, false},
		{"1.5", 0, false},
	} {
		got, ok := parseRetryAfter(test.value)
		if got != test.want || ok != test.ok {
			t.Errorf("parseRetryAfter(%q) = (%s, %v), want (%s, %v)", test.value, got, ok, test.want, test.ok)
		}
	}
}

func TestNewClientRejectsInsecureOrCredentialBearingEndpoints(t *testing.T) {
	for _, endpoint := range []string{
		"http://api.terrascale.tech",
		"https://user:secret@api.terrascale.tech",
		"https://api.terrascale.tech/?access_token=leaked",
	} {
		if _, err := NewClient(Config{APIBaseURL: endpoint}); err == nil {
			t.Fatalf("unsafe endpoint %q was accepted", endpoint)
		}
	}
	if _, err := NewClient(Config{APIBaseURL: "http://api.example.test", HTTPClient: &http.Client{}}); err == nil {
		t.Fatal("injected transport accepted non-loopback HTTP")
	}
}
