package tsauth

import (
	"context"
	"net/http"
	"net/url"
	"testing"
)

// The ts-auth -> TerraBase token contract: one global issuer, its token
// endpoint, and exactly four scopes.
func TestContractConstants(t *testing.T) {
	t.Parallel()
	if DefaultIssuer != "https://auth.terrascale.tech/terrascale" {
		t.Fatalf("DefaultIssuer = %q", DefaultIssuer)
	}
	if endpoint, err := tokenEndpoint(""); err != nil || endpoint != "https://auth.terrascale.tech/terrascale/oauth/token" {
		t.Fatalf("default token endpoint = %q, %v", endpoint, err)
	}
	got := []string{ScopeDatabasesRead, ScopeDatabasesWrite, ScopeDocumentsRead, ScopeDocumentsWrite}
	want := []string{"databases:read", "databases:write", "documents:read", "documents:write"}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("scope %d = %q, want %q", i, got[i], want[i])
		}
	}
	if checkScopes(want) != nil {
		t.Fatal("a contract scope was rejected")
	}
}

// Token requests carry the caller's scopes as one space-separated string,
// and no scope parameter at all when none were asked for: the SDK never
// widens a request.
func TestContractScopeParameter(t *testing.T) {
	t.Parallel()
	all := []string{ScopeDatabasesRead, ScopeDatabasesWrite, ScopeDocumentsRead, ScopeDocumentsWrite}
	for name, tc := range map[string]struct {
		scopes []string
		want   []string // nil: no scope parameter
	}{
		"none": {nil, nil},
		"one":  {[]string{ScopeDocumentsRead}, []string{"documents:read"}},
		"all":  {all, []string{"databases:read databases:write documents:read documents:write"}},
	} {
		for _, grant := range []string{"client_credentials", "token-exchange"} {
			t.Run(name+"/"+grant, func(t *testing.T) {
				t.Parallel()
				srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
					if r.URL.Path != "/terrascale/oauth/token" || len(form["scope"]) != len(tc.want) || (tc.want != nil && form["scope"][0] != tc.want[0]) {
						t.Errorf("token request %s: %v", r.URL.Path, form)
					}
					answer(w, "token", 900)
				})
				var s *TokenSource
				var err error
				if grant == "client_credentials" {
					s, err = NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey, Scopes: tc.scopes})
				} else {
					s, err = NewExchangeSource(ExchangeConfig{Issuer: srv.URL + "/terrascale", ClientID: "ts-cli", Organization: "01ARZ3NDEKTSV4RRFFQ69G5FA6",
						SubjectToken: func(context.Context) (string, error) { return "user-token", nil }, Scopes: tc.scopes})
				}
				if err != nil {
					t.Fatal(err)
				}
				if _, err := s.Token(t.Context()); err != nil {
					t.Fatal(err)
				}
			})
		}
	}
}
