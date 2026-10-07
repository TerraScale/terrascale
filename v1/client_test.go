package v1_test

// Checks that need no server: configuration and inputs the client rejects
// before sending anything. Everything that talks to TerraBase is in
// integration_test.go, against the real executable.

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

const (
	testToken      = "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
	testCredential = "dashboard-credential-0123456789abcdef"
	testProject    = "0190a6e4-7c3b-7d2e-8f00-0123456789ab"
	testOrg        = "0190a6e4-7c3b-7d2e-8f00-0000000000c0"
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
		{"http loopback", false, testToken, "http://127.0.0.1:9464", true},
		{"http non-loopback", false, testToken, "http://tb.example", false},
		{"credential", true, testCredential, "https://tb.example", true},
		{"no credential", true, "", "https://tb.example", true},
		{"short credential", true, strings.Repeat("a", 31), "https://tb.example", false},
		{"credential with space", true, strings.Repeat("a", 31) + " ", "https://tb.example", false},
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

func TestResolverRejection(t *testing.T) {
	failure := errors.New("resolver unavailable")
	for _, tc := range []struct {
		name     string
		resolver *testResolver
		want     error
	}{
		{"resolution error", &testResolver{err: failure}, failure},
		{"plain remote HTTP", &testResolver{url: "http://tb.example"}, nil},
		{"credentials in URL", &testResolver{url: "https://user:secret@tb.example"}, nil},
		{"empty URL", &testResolver{}, nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			cfg := v1.Config{Resolver: tc.resolver, Token: testToken, HTTPClient: &http.Client{Transport: failTransport{t}}}
			c, err := v1.New(cfg)
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database(testProject, "main").Create(t.Context())
			if err == nil || (tc.want != nil && !errors.Is(err, tc.want)) || tc.resolver.calls != 1 {
				t.Fatalf("resolution: calls=%d, error=%v", tc.resolver.calls, err)
			}
			cfg.BaseURL = "https://fallback.example"
			if _, err := v1.New(cfg); err == nil {
				t.Fatal("accepted both Resolver and BaseURL")
			}
		})
	}
}

// failTransport fails the test on any request: these inputs must be
// rejected locally.
type failTransport struct{ t *testing.T }

func (f failTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	f.t.Errorf("unexpected request %s %s", r.Method, r.URL)
	return nil, errors.New("unexpected request")
}

func TestLocalRejection(t *testing.T) {
	hc := &http.Client{Transport: failTransport{t}}
	resolver := &testResolver{url: "https://tb.example"}
	c, err := v1.New(v1.Config{Resolver: resolver, Token: testToken, HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	m, err := v1.NewManagement(v1.Config{BaseURL: "https://tb.example", Token: testCredential, HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	anonymous, err := v1.NewManagement(v1.Config{BaseURL: "https://tb.example", HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	users := c.Database(testProject, "main").Collection("users")
	ctx := t.Context()
	for name, call := range map[string]func() error{
		"non-UUID project": func() error { _, err := c.Database("My-Project", "main").Create(ctx); return err },
		"database name":    func() error { _, err := c.Database(testProject, "Main").Create(ctx); return err },
		"collection name":  func() error { _, err := c.Database(testProject, "main").CreateCollection(ctx, "a/b"); return err },
		"empty id":         func() error { _, err := users.Get(ctx, ""); return err },
		"slash id":         func() error { _, err := users.Get(ctx, "a/b"); return err },
		"control id":       func() error { _, err := users.Get(ctx, "a\nb"); return err },
		"long id":          func() error { _, err := users.Get(ctx, strings.Repeat("x", 257)); return err },
		"long Unicode id":  func() error { _, err := users.Get(ctx, strings.Repeat("界", 257)); return err },
		"long astral id":   func() error { _, err := users.Get(ctx, strings.Repeat("😀", 129)); return err },
		"invalid UTF-8 id": func() error { _, err := users.Get(ctx, "\xff"); return err },
		"quoted version":   func() error { _, err := users.Delete(ctx, testProject, v1.IfMatch(`a"b`)); return err },
		"spaced version":   func() error { _, err := users.Get(ctx, testProject, v1.IfNoneMatch("a b")); return err },
		"control version":  func() error { _, err := users.Delete(ctx, testProject, v1.IfMatch("\x00")); return err },
		"create precondition": func() error {
			_, err := users.Create(ctx, map[string]any{}, v1.IfNoneMatchAny())
			return err
		},
		"not an object": func() error { _, err := users.Put(ctx, testProject, []int{1}); return err },
		"empty tx":      func() error { _, err := c.Database(testProject, "main").Tx().Commit(ctx); return err },
		"slash member":  func() error { return m.As("u").RemoveMember(ctx, testOrg, "a/b") },
		"dot member":    func() error { return m.As("u").RemoveMember(ctx, testOrg, "..") },
		"empty org":     func() error { _, err := m.As("u").Organization(ctx, ""); return err },
		"As without credential": func() error {
			_, err := anonymous.As("u").Organizations(ctx, v1.PageOptions{})
			return err
		},
		"empty token":      func() error { _, err := anonymous.WithToken("").Organizations(ctx, v1.PageOptions{}); return err },
		"token with space": func() error { _, err := anonymous.WithToken("a b").Organizations(ctx, v1.PageOptions{}); return err },
	} {
		if err := call(); err == nil {
			t.Errorf("%s: no error", name)
		}
	}
	if resolver.calls != 0 {
		t.Fatalf("invalid input triggered %d resolutions", resolver.calls)
	}
}
