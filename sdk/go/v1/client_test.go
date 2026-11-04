package v1_test

// Checks that need no server: configuration and inputs the client rejects
// before sending anything, and the requests of routes the server does not
// serve yet.

import (
	"context"
	"errors"
	"io"
	"net/http"

	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

const (
	testToken      = "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
	testCredential = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
	testProject    = "01J2KE8Z1VFMQ8Y0014D2PF2DB"
	testOrg        = "01J2KE8Z1VFMQ8Y00000000060"
)

func TestClientConfigValidation(t *testing.T) {
	for _, tc := range []struct {
		name    string
		mgmt    bool
		token   string
		baseURL string
		ok      bool
	}{
		{"api key", false, testToken, "https://tb.example", true},
		{"short api key", false, "tb_AAAA", "https://tb.example", false},
		{"api key bad char", false, "tb_" + strings.Repeat("A", 42) + "+", "https://tb.example", false},
		{"credential as api key", false, testCredential, "https://tb.example", false},
		{"project access token", false, "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln", "https://tb.example", true},
		{"project access token with space", false, "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln ", "https://tb.example", false},
		{"two segments", false, "eyJhbGciOiJFUzI1NiJ9.c2ln", "https://tb.example", false},
		{"http loopback", false, testToken, "http://127.0.0.1:9464", true},
		{"http non-loopback", false, testToken, "http://tb.example", false},
		{"credential", true, testCredential, "https://tb.example", true},
		{"no credential", true, "", "https://tb.example", true},
		{"short credential", true, strings.Repeat("a", 63), "https://tb.example", false},
		{"long credential", true, strings.Repeat("a", 65), "https://tb.example", false},
		{"nonhex credential", true, strings.Repeat("g", 64), "https://tb.example", false},
		{"credential with space", true, strings.Repeat("a", 63) + " ", "https://tb.example", false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			cfg := v1.Config{BaseURL: tc.baseURL, Token: tc.token}
			var err error
			if tc.mgmt {
				_, err = v1.NewManagement(cfg)
			} else {
				_, err = v1.New(cfg)
			}
			if (err == nil) != tc.ok {
				t.Fatalf("err = %v, want ok=%v", err, tc.ok)
			}
		})
	}
}

type testResolver struct {
	url   string
	err   error
	calls int
}

func (r *testResolver) Resolve(ctx context.Context) (string, error) {
	r.calls++
	if ctx.Err() != nil {
		return "", ctx.Err()
	}
	return r.url, r.err
}

// failTransport fails the test on any request: these inputs must be
// rejected locally.
type failTransport struct{ t *testing.T }

func (f failTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	f.t.Errorf("unexpected request %s %s", r.Method, r.URL)
	return nil, errors.New("unexpected request")
}

// captureTransport records each request as "METHOD /path body" and fails it
// without answering: it checks what the client sends, never fakes TerraBase.
type captureTransport struct {
	got     []string
	queries []string
}

func (c *captureTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	var body []byte
	if r.Body != nil {
		body, _ = io.ReadAll(r.Body)
	}
	c.queries = append(c.queries, r.URL.RawQuery)
	c.got = append(c.got, r.Method+" "+r.URL.EscapedPath()+" "+string(body))
	return nil, errors.New("captured")
}

// TestDatabasePlacementRequests checks the region placement requests
// (TS-643), which the server does not serve yet.

// Every call fails at the transport; only the requests matter.

// nil clears the replicas
