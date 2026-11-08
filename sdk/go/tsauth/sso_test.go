package tsauth

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
)

// The SSO connection contract of the auth producer, replayed from the shared
// fixture (contracts/auth/sso.json, values synthetic): one put including the
// OIDC client secret, then one get, one verify and one delete against the
// same fixture's methods, paths and statuses. The secret never appears in
// formatted values.
func TestSsoConnectionMatchFixture(t *testing.T) {
	t.Parallel()
	data, err := os.ReadFile("../contracts/auth/sso.json")
	if err != nil {
		t.Skipf("contract fixture unavailable: %v", err)
	}
	var fixture struct {
		PutSsoConnection struct {
			Method   string          `json:"method"`
			Path     string          `json:"path"`
			Status   int             `json:"status"`
			Request  json.RawMessage `json:"request"`
			Response json.RawMessage `json:"response"`
		} `json:"put_sso_connection"`
		GetSsoConnection struct {
			Method   string          `json:"method"`
			Path     string          `json:"path"`
			Status   int             `json:"status"`
			Response json.RawMessage `json:"response"`
		} `json:"get_sso_connection"`
		VerifySsoDomain struct {
			Method string `json:"method"`
			Path   string `json:"path"`
			Status int    `json:"status"`
		} `json:"verify_sso_domain"`
		DeleteSsoConnection struct {
			Method string `json:"method"`
			Path   string `json:"path"`
			Status int    `json:"status"`
		} `json:"delete_sso_connection"`
	}
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	const org = "01ARZ3NDEKTSV4RRFFQ69G5FA6"
	const secret = "oidc-secret-fixture-value"
	var requests []string
	var seenBodies []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests = append(requests, r.Method+" "+r.URL.Path)
		if r.Header.Get("Authorization") != "Bearer fixture-sign-in" {
			t.Errorf("authorization = %q", r.Header.Get("Authorization"))
		}
		if r.Body != nil {
			if raw, readErr := io.ReadAll(r.Body); readErr == nil {
				seenBodies = append(seenBodies, string(raw))
			}
		}
		switch r.Method {
		case http.MethodPut:
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(fixture.PutSsoConnection.Status)
			_, _ = w.Write([]byte(strings.Replace(string(fixture.PutSsoConnection.Response), "{org}", org, 1)))
		case http.MethodGet, http.MethodPost:
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(fixture.GetSsoConnection.Status)
			_, _ = w.Write([]byte(strings.Replace(string(fixture.GetSsoConnection.Response), "{org}", org, 1)))
		default:
			w.WriteHeader(fixture.DeleteSsoConnection.Status)
		}
	}))
	defer server.Close()

	client, err := New(Config{Issuer: server.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	u := client.WithToken("fixture-sign-in")
	created, err := u.PutSsoConnection(t.Context(), org, SsoConnectionRequest{
		Domain: "Example.com.", Protocol: "oidc", DefaultRole: RoleMember,
		Oidc: &SsoOidcRequest{Issuer: "https://issuer.example.test", ClientID: "ts-client", ClientSecret: secret},
	})
	if err != nil {
		t.Fatal(err)
	}
	if created.OrganizationID != org || created.Domain != "example.com" || created.DomainVerified ||
		created.Verification == nil || created.Verification.Name != "_terrascale-sso.example.com" ||
		created.Oidc == nil || !created.Oidc.ClientSecretSet || created.Oidc.RedirectURI == "" || created.CreatedAt.IsZero() {
		t.Fatalf("created = %+v", created)
	}
	stored, err := u.GetSsoConnection(t.Context(), org)
	if err != nil {
		t.Fatal(err)
	}
	if stored.Domain != created.Domain || !stored.DomainVerified || stored.VerifiedAt == nil ||
		stored.Oidc == nil || stored.Oidc.Issuer != created.Oidc.Issuer || stored.Oidc.ClientID != created.Oidc.ClientID {
		t.Fatalf("stored = %+v", stored)
	}
	verified, err := u.VerifySsoDomain(t.Context(), org)
	if err != nil {
		t.Fatal(err)
	}
	if !verified.DomainVerified || verified.VerifiedAt == nil {
		t.Fatalf("verified = %+v", verified)
	}
	if err := u.DeleteSsoConnection(t.Context(), org); err != nil {
		t.Fatal(err)
	}
	wantRequests := []string{
		fmt.Sprintf("PUT %s", strings.Replace(fixture.PutSsoConnection.Path, "{org}", org, 1)),
		fmt.Sprintf("GET %s", strings.Replace(fixture.GetSsoConnection.Path, "{org}", org, 1)),
		fmt.Sprintf("POST %s", strings.Replace(fixture.VerifySsoDomain.Path, "{org}", org, 1)),
		fmt.Sprintf("DELETE %s", strings.Replace(fixture.DeleteSsoConnection.Path, "{org}", org, 1)),
	}
	if len(requests) != len(wantRequests) {
		t.Fatalf("requests = %v", requests)
	}
	for i := range wantRequests {
		if requests[i] != wantRequests[i] {
			t.Errorf("request %d = %q, want %q", i, requests[i], wantRequests[i])
		}
	}
	if len(seenBodies) != 4 || !strings.Contains(seenBodies[0], secret) {
		t.Fatalf("request bodies = %q", seenBodies)
	}
	for _, body := range seenBodies[1:] {
		if strings.Contains(body, secret) {
			t.Errorf("request body leaks the client secret: %q", body)
		}
	}
	for _, formatted := range []string{
		fmt.Sprintf("%v", created), fmt.Sprintf("%+v", created), fmt.Sprintf("%#v", created),
		fmt.Sprintf("%v", SsoConnectionRequest{Oidc: &SsoOidcRequest{ClientSecret: secret}}),
		fmt.Sprintf("%#v", SsoConnectionRequest{Oidc: &SsoOidcRequest{ClientSecret: secret}}),
		fmt.Sprintf("%v", SsoOidcRequest{ClientSecret: secret}),
		fmt.Sprintf("%#v", SsoOidcRequest{ClientSecret: secret}),
	} {
		if strings.Contains(formatted, secret) {
			t.Errorf("formatted value leaks the client secret: %q", formatted)
		}
	}
}
