package tsauth_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

func TestProjectAuthProviderRequests(t *testing.T) {
	fixture := projectAuthFixture(t)
	secret, issuer, team, key := "synthetic-provider-secret", "https://oidc.example.test", "TEAM012345", "KEY0123456"
	for _, provider := range []string{tsauth.ProjectProviderGoogle, tsauth.ProjectProviderGitHub, tsauth.ProjectProviderApple, tsauth.ProjectProviderOIDC} {
		t.Run(provider, func(t *testing.T) {
			calls := 0
			request := tsauth.ProjectAuthProviderRequest{ClientID: "customer.vendor-client.test", ClientSecret: &secret}
			if provider == tsauth.ProjectProviderOIDC {
				request.Issuer = &issuer
			}
			if provider == tsauth.ProjectProviderApple {
				request.TeamID = &team
				request.KeyID = &key
			}
			u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
				calls++
				if r.URL.Path != "/proxy/v1/"+projectAuthID+"/providers/"+provider || r.Header.Get("Authorization") != "Bearer platform-sign-in-token" {
					t.Error("invalid provider route/auth")
				}
				var response map[string]json.RawMessage
				_ = json.Unmarshal(fixture, &response)
				var providers []map[string]any
				_ = json.Unmarshal(response["providers"], &providers)
				for _, p := range providers {
					if p["provider"] == provider {
						p["configured"] = calls < 3
						p["client_id"] = request.ClientID
					}
				}
				response["providers"], _ = json.Marshal(providers)
				if calls < 3 {
					if r.Method != http.MethodPut || r.Header.Get("Content-Type") != "application/json" {
						t.Error("invalid provider PUT")
					}
					data, _ := io.ReadAll(r.Body)
					var m map[string]any
					if json.Unmarshal(data, &m) != nil || m["client_id"] != request.ClientID {
						t.Errorf("vendor client ID rejected/changed: %s", data)
					}
					if (calls == 1 && m["client_secret"] != secret) || (calls == 2 && m["client_secret"] != nil) {
						t.Errorf("lost write-only secret omission: %s", data)
					}
					if provider == tsauth.ProjectProviderOIDC && m["issuer"] != issuer {
						t.Error("lost OIDC issuer")
					}
					if provider == tsauth.ProjectProviderApple && (m["team_id"] != team || m["key_id"] != key) {
						t.Error("lost Apple vendor IDs")
					}
					if provider == tsauth.ProjectProviderGoogle || provider == tsauth.ProjectProviderGitHub {
						if len(m) != 2 && calls == 1 || len(m) != 1 && calls == 2 {
							t.Error("invented provider fields")
						}
					}
				} else if r.Method != http.MethodDelete || r.Body != nil {
					t.Error("DELETE must have no request body")
				}
				data, _ := json.Marshal(response)
				return projectAuthResponse(200, string(data)), nil
			})
			for i := 0; i < 2; i++ {
				result, err := u.SetProjectProvider(t.Context(), projectAuthID, provider, request)
				if err != nil {
					t.Fatal(err)
				}
				for _, p := range result.Providers {
					if p.Provider == provider && (!p.Configured || p.ClientID == nil || *p.ClientID != request.ClientID || !strings.HasSuffix(p.CallbackURL, "/social/"+provider+"/callback")) {
						t.Fatalf("lost provider metadata: %+v", p)
					}
				}
				request.ClientSecret = nil
			}
			result, err := u.DeleteProjectProvider(t.Context(), projectAuthID, provider)
			if err != nil || result == nil || calls != 3 {
				t.Fatalf("provider removal: err=%v calls=%d", err, calls)
			}
		})
	}
}

func TestProjectAuthProviderSecretDiagnostics(t *testing.T) {
	secret := "synthetic-do-not-log-secret"
	request := tsauth.ProjectAuthProviderRequest{ClientID: "vendor-id", ClientSecret: &secret}
	for _, value := range []any{request, &request} {
		for _, format := range []string{"%v", "%+v", "%#v", "%s", "%q", "%x"} {
			if got := fmt.Sprintf(format, value); strings.Contains(got, secret) || !strings.Contains(got, "redacted") {
				t.Errorf("unsafe formatting %s: %s", format, got)
			}
		}
	}
	for _, status := range []int{400, 401, 403, 404, 503} {
		calls := 0
		u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
			calls++
			res := projectAuthResponse(status, `{"error":{"code":"invalid_request","message":"`+secret+`"}}`)
			res.Header.Set("Retry-After", "3")
			return res, nil
		})
		_, err := u.SetProjectProvider(t.Context(), projectAuthID, "google", request)
		var api *tsauth.APIError
		if !errors.As(err, &api) || !errors.Is(err, tsauth.ErrInvalidRequest) || api.Status != status || api.RetryAfter != 3*time.Second || calls != 1 || strings.Contains(fmt.Sprintf("%+v %#v", err, api), secret) {
			t.Fatalf("unsafe/untyped refusal: %v", err)
		}
	}
	for _, body := range []string{`{"error":{"code":"` + secret + `","message":"` + secret + `"}}`, `{"project_id":"` + secret + `"}`} {
		u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) { return projectAuthResponse(400, body), nil })
		_, err := u.SetProjectProvider(t.Context(), projectAuthID, "google", request)
		if err == nil || strings.Contains(fmt.Sprintf("%#v", err), secret) {
			t.Fatal("echoed untrusted server diagnostic")
		}
	}
	u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) { return nil, errors.New(secret) })
	if _, err := u.SetProjectProvider(t.Context(), projectAuthID, "google", request); err == nil || strings.Contains(err.Error(), secret) {
		t.Fatal("echoed untrusted transport diagnostic")
	}
	// Unknown response fields, including an accidentally returned secret, cannot
	// enter the public result model or its subsequent JSON serialization.
	data := strings.Replace(string(projectAuthFixture(t)), `"project_id":`, `"client_secret":"`+secret+`","project_id":`, 1)
	u = projectAuthUser(t, func(r *http.Request) (*http.Response, error) { return projectAuthResponse(200, data), nil })
	result, err := u.SetProjectProvider(t.Context(), projectAuthID, "google", request)
	if err != nil {
		t.Fatal(err)
	}
	encoded, _ := json.Marshal(result)
	if strings.Contains(string(encoded), secret) || strings.Contains(string(encoded), "client_secret") {
		t.Fatal("returned write-only secret")
	}
}

func TestProjectAuthProviderRequestLimitAndCancellation(t *testing.T) {
	secret := ""
	request := tsauth.ProjectAuthProviderRequest{ClientID: "vendor-id", ClientSecret: &secret}
	empty, err := json.Marshal(request)
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
		calls++
		data, err := io.ReadAll(r.Body)
		if err != nil || len(data) != 16<<10 {
			t.Errorf("provider request bound: bytes=%d err=%v", len(data), err)
		}
		return projectAuthResponse(200, string(projectAuthFixture(t))), nil
	})
	secret = strings.Repeat("x", (16<<10)-len(empty))
	if _, err := u.SetProjectProvider(t.Context(), projectAuthID, "google", request); err != nil {
		t.Fatal(err)
	}
	secret += "x"
	if _, err := u.SetProjectProvider(t.Context(), projectAuthID, "google", request); err == nil || calls != 1 {
		t.Fatalf("oversize provider request sent: calls=%d", calls)
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := u.SetProjectProvider(ctx, projectAuthID, "google", request); !errors.Is(err, context.Canceled) {
		t.Fatalf("provider PUT lost cancellation: %v", err)
	}
	if _, err := u.DeleteProjectProvider(ctx, projectAuthID, "google"); !errors.Is(err, context.Canceled) || calls != 1 {
		t.Fatalf("provider DELETE lost cancellation: calls=%d err=%v", calls, err)
	}
}

func TestProjectAuthProviderValidationAndOperationParity(t *testing.T) {
	calls := 0
	u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
		calls++
		return projectAuthResponse(200, string(projectAuthFixture(t))), nil
	})
	for _, provider := range []string{"", "Google", "unknown", "../settings", "google?escape=1"} {
		if _, err := u.SetProjectProvider(t.Context(), projectAuthID, provider, tsauth.ProjectAuthProviderRequest{}); err == nil {
			t.Fatal("accepted invalid provider")
		}
		if _, err := u.DeleteProjectProvider(t.Context(), projectAuthID, provider); err == nil {
			t.Fatal("accepted invalid provider")
		}
	}
	for _, project := range []string{"legacy", strings.ToLower(projectAuthID)} {
		if _, err := u.SetProjectProvider(t.Context(), project, "google", tsauth.ProjectAuthProviderRequest{}); err == nil {
			t.Fatal("accepted invalid point project ID")
		}
		if _, err := u.DeleteProjectProvider(t.Context(), project, "google"); err == nil {
			t.Fatal("accepted invalid point project ID")
		}
	}
	if calls != 0 {
		t.Fatal("invalid paths reached network")
	}
	data, err := os.ReadFile("testdata/project-auth-contract.json")
	if err != nil {
		t.Fatal(err)
	}
	var parity struct {
		Operations []struct {
			Method, Path, Role string
			ID                 string `json:"operation_id"`
		}
	}
	if err = json.Unmarshal(data, &parity); err != nil {
		t.Fatal(err)
	}
	for _, op := range parity.Operations {
		invoked := false
		u = projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
			invoked = true
			want := strings.ReplaceAll(strings.ReplaceAll(op.Path, "{project}", projectAuthID), "{provider}", "google")
			if r.Method != op.Method || r.URL.Path != "/proxy"+want {
				t.Errorf("generated route mismatch %s: %s %s", op.ID, r.Method, r.URL.Path)
			}
			return projectAuthResponse(200, string(projectAuthFixture(t))), nil
		})
		switch op.ID {
		case "get_project_settings":
			_, err = u.ProjectSettings(t.Context(), projectAuthID)
		case "update_project_settings":
			_, err = u.UpdateProjectSettings(t.Context(), projectAuthID, tsauth.ProjectAuthSettingsPatch{})
		case "set_project_provider":
			_, err = u.SetProjectProvider(t.Context(), projectAuthID, "google", tsauth.ProjectAuthProviderRequest{ClientID: "vendor-id"})
		case "delete_project_provider":
			_, err = u.DeleteProjectProvider(t.Context(), projectAuthID, "google")
		default:
			t.Fatalf("unrecognized owning operation %s", op.ID)
		}
		if !invoked || err != nil {
			t.Fatalf("missing operation %s: %v", op.ID, err)
		}
	}
}
