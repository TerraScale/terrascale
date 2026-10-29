package v1_test

import (
	"net/http"
	"net/http/httptest"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestManagementAuthenticationStaysAtItsBoundary(t *testing.T) {
	for _, operator := range []bool{false, true} {
		mode := "WithToken"
		if operator {
			mode = "As"
		}
		t.Run(mode, func(t *testing.T) {
			for _, status := range []int{http.StatusNoContent, http.StatusUnauthorized, http.StatusForbidden, http.StatusTemporaryRedirect} {
				t.Run(http.StatusText(status), func(t *testing.T) {
					redirected := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
						t.Error("credentials reached a redirect target")
					}))
					t.Cleanup(redirected.Close)
					calls := 0
					peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
						calls++
						if r.Method != http.MethodDelete || r.URL.Path != "/v1/management/organizations/"+testOrg {
							t.Error("wrong management request")
						}
						if operator {
							if len(r.Header.Values("Authorization")) != 0 || r.Header.Get("X-TerraBase-Peer-Credential") != testCredential || r.Header.Get("X-TerraBase-User-Id") != testProject {
								t.Error("operator request must use only the peer credential and acting user")
							}
						} else if r.Header.Get("Authorization") != "Bearer user-access-token" || r.Header.Get("X-TerraBase-Peer-Credential") != "" || r.Header.Get("X-TerraBase-User-Id") != "" {
							t.Error("user request must use only its access token")
						}
						w.Header().Set("Location", redirected.URL)
						w.WriteHeader(status)
					}))
					t.Cleanup(peer.Close)
					m, err := v1.NewManagement(v1.Config{BaseURL: peer.URL, Token: testCredential})
					if err != nil {
						t.Fatal(err)
					}
					u := m.WithToken("user-access-token")
					if operator {
						u = m.As(testProject)
					}
					err = u.DeleteOrganization(t.Context(), testOrg)
					if (err == nil) != (status == http.StatusNoContent) || calls != 1 {
						t.Fatalf("status=%d calls=%d error=%v", status, calls, err)
					}
				})
			}
		})
	}
}
