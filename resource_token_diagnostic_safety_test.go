package sdk_test

import (
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"

	sdk "terrascale.tech/sdk"
)

func TestTSAuthResourceTokensPublicFormattingSafety(t *testing.T) {
	const origin = "https://auth.example.test"
	const audience = "https://resource.example.test/notes"
	const access = "CANARY_RESOURCE_ACCESS"
	const refresh = "CANARY_RESOURCE_REFRESH"
	const replacement = "CANARY_RESOURCE_REPLACEMENT"
	const clientSecret = "CANARY_RESOURCE_CLIENT_SECRET"
	calls := 0
	transport := resourceTokenSafetyTransport(func(request *http.Request) (*http.Response, error) {
		calls++
		if request.Method != http.MethodPost || request.URL.String() != origin+"/acme/oauth/token" || request.Header.Get("Cookie") != "" {
			t.Fatal("resource token request changed origin, method, or cookie isolation")
		}
		if err := request.ParseForm(); err != nil {
			t.Fatal(err)
		}
		var body string
		switch calls {
		case 1:
			if request.PostForm.Get("grant_type") != "authorization_code" || request.PostForm.Get("resource") != audience {
				t.Fatal("resource code exchange lost its explicit binding")
			}
			body = `{"access_token":"` + access + `","token_type":"Bearer","expires_in":60,"scope":"read","refresh_token":"` + refresh + `"}`
		case 2:
			if request.PostForm.Get("grant_type") != "refresh_token" || request.PostForm.Get("refresh_token") != refresh {
				t.Fatal("resource refresh did not retain its original opaque token")
			}
			body = `{"access_token":"` + access + `","token_type":"Bearer","expires_in":60,"scope":"read","refresh_token":"` + replacement + `"}`
		default:
			t.Fatal("resource token request was replayed")
		}
		return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(strings.NewReader(body)), Request: request}, nil
	})
	client, err := sdk.NewClient(sdk.Config{TSAuthBaseURL: origin, HTTPClient: &http.Client{Transport: transport}})
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	resource, err := instance.NewResourceClient(sdk.TSAuthResourceClientConfig{
		Issuer: origin + "/acme", ResourceAudience: audience,
		Authentication: sdk.TSAuthOAuthClientAuthentication{ClientID: "client", ClientSecret: clientSecret, Method: sdk.TSAuthClientSecretBasic},
	})
	if err != nil {
		t.Fatal(err)
	}
	grant, err := resource.ExchangeAuthorizationCode(t.Context(), "code", "https://app.example.test/callback", strings.Repeat("a", 43), []string{"read"}, true)
	if err != nil {
		t.Fatal(err)
	}
	rotated, err := resource.Refresh(t.Context(), grant, sdk.TSAuthResourceRefreshOptions{})
	if err != nil {
		t.Fatal(err)
	}
	if calls != 2 || grant.Tokens.AccessToken != access || grant.Tokens.RefreshToken == nil || *grant.Tokens.RefreshToken != refresh ||
		rotated.Tokens.AccessToken != access || rotated.Tokens.RefreshToken == nil || *rotated.Tokens.RefreshToken != replacement ||
		rotated.Audience() != audience || len(rotated.GrantedScopes()) != 1 || rotated.GrantedScopes()[0] != "read" {
		t.Fatal("public resource exchange or refresh lost its credentials or binding")
	}
	for _, result := range []struct {
		name  string
		value any
	}{
		{"exchange pointer", grant}, {"exchange value", *grant},
		{"refresh pointer", rotated}, {"refresh value", *rotated},
	} {
		t.Run(result.name, func(t *testing.T) {
			for _, format := range []string{"%v", "%+v", "%#v", "%#+v"} {
				formatted := fmt.Sprintf(format, result.value)
				if !strings.Contains(formatted, "[REDACTED]") {
					t.Fatalf("format %q omitted resource credential redaction", format)
				}
				for _, secret := range []string{access, refresh, replacement, clientSecret} {
					if strings.Contains(formatted, secret) {
						t.Fatalf("format %q exposed resource credentials", format)
					}
				}
			}
		})
	}
}

type resourceTokenSafetyTransport func(*http.Request) (*http.Response, error)

func (transport resourceTokenSafetyTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	return transport(request)
}
