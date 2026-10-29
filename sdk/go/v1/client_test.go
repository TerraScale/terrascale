package v1_test

// Checks that need no server: configuration and inputs the client rejects
// before sending anything, and the requests of routes the server does not
// serve yet.

import (
	"context"
	"errors"
	"io"
	"net/http"
	"slices"
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
		"organization ID": func() error { _, err := m.WithToken("access-token").Organization(ctx, "org_legacy"); return err },
		"member ID":       func() error { return m.WithToken("access-token").RemoveMember(ctx, testOrg, "legacy-user") },
		"body organization ID": func() error {
			_, err := m.WithToken("access-token").SetCurrentOrganization(ctx, "org_legacy")
			return err
		},
		"body user ID": func() error {
			_, err := m.WithToken("access-token").StartOwnershipTransfer(ctx, testOrg, "legacy-user")
			return err
		},
		"non-ULID project": func() error { _, err := c.Database("My-Project", "main").Create(ctx); return err },
		"database name":    func() error { _, err := c.Database(testProject, "Main").Create(ctx); return err },
		"collection name":  func() error { _, err := c.Database(testProject, "main").CreateCollection(ctx, "a/b"); return err },
		"empty id":         func() error { _, err := users.Get(ctx, ""); return err },
		"DEL in id":        func() error { _, err := users.Get(ctx, "a\x7fb"); return err },
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
func TestDatabasePlacementRequests(t *testing.T) {
	capture := &captureTransport{}
	m, err := v1.NewManagement(v1.Config{BaseURL: "https://tb.example", HTTPClient: &http.Client{Transport: capture}})
	if err != nil {
		t.Fatal(err)
	}
	u, ctx := m.WithToken("access-token"), t.Context()
	// Every call fails at the transport; only the requests matter.
	u.CreateDatabase(ctx, testOrg, testProject, "orders")
	u.CreateDatabaseIn(ctx, testOrg, testProject, "orders", v1.Placement{HomeRegion: "gru-1", ReplicaRegions: []string{"sfo-1"}})
	u.SetDatabaseRegions(ctx, testOrg, testProject, "01ARZ3NDEKTSV4RRFFQ69G5FA5", []string{"sfo-1"})
	u.SetDatabaseRegions(ctx, testOrg, testProject, "01ARZ3NDEKTSV4RRFFQ69G5FA5", nil)
	u.CreateDatabaseIn(ctx, testOrg, testProject, "orders", v1.Placement{AllRegions: true})
	u.SetDatabaseAllRegions(ctx, testOrg, testProject, "01ARZ3NDEKTSV4RRFFQ69G5FA5")
	const databases = "/v1/management/organizations/" + testOrg + "/projects/" + testProject + "/databases"
	want := []string{
		"POST " + databases + ` {"name":"orders"}`,
		"POST " + databases + ` {"name":"orders","homeRegion":"gru-1","replicaRegions":["sfo-1"]}`,
		"PATCH " + databases + `/01ARZ3NDEKTSV4RRFFQ69G5FA5 {"replicaRegions":["sfo-1"]}`,
		"PATCH " + databases + `/01ARZ3NDEKTSV4RRFFQ69G5FA5 {"replicaRegions":[]}`, // nil clears the replicas
		"POST " + databases + ` {"name":"orders","allRegions":true}`,
		"PATCH " + databases + `/01ARZ3NDEKTSV4RRFFQ69G5FA5 {"allRegions":true}`,
	}
	if !slices.Equal(capture.got, want) {
		t.Fatalf("sent\n%s\nwant\n%s", strings.Join(capture.got, "\n"), strings.Join(want, "\n"))
	}
}
