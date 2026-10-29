package sdk

import (
	"os"
	"strings"
	"testing"
)

// This lane uses an independently deployed provider and issued tokens. The
// ordinary cryptographic fixture suite is not evidence for this lane.
func TestTSAuthOIDCRealProvider(t *testing.T) {
	required := []string{"TERRASCALE_OIDC_ISSUER", "TERRASCALE_OIDC_AUTH_BASE_URL", "TERRASCALE_OIDC_CLIENT_ID", "TERRASCALE_OIDC_AUTH_METHOD", "TERRASCALE_OIDC_REDIRECT_URI", "TERRASCALE_OIDC_ID_TOKEN", "TERRASCALE_OIDC_NONCE", "TERRASCALE_OIDC_ACCESS_TOKEN", "TERRASCALE_OIDC_TEST_ALLOCATION"}
	missing := []string{}
	for _, name := range required {
		if os.Getenv(name) == "" {
			missing = append(missing, name)
		}
	}
	if len(missing) > 0 {
		if os.Getenv("TERRASCALE_OIDC_REQUIRE_PROVIDER") == "1" {
			t.Fatalf("real provider preflight missing %s", strings.Join(missing, ","))
		}
		t.Skipf("real provider NOT RUN: missing %s", strings.Join(missing, ","))
	}
	method := TSAuthOAuthClientAuthenticationMethod(os.Getenv("TERRASCALE_OIDC_AUTH_METHOD"))
	if method != TSAuthClientAuthenticationNone && os.Getenv("TERRASCALE_OIDC_CLIENT_SECRET") == "" {
		t.Fatal("real provider preflight missing confidential client secret")
	}
	client, err := NewClient(Config{TSAuthBaseURL: os.Getenv("TERRASCALE_OIDC_AUTH_BASE_URL")})
	if err != nil {
		t.Fatal("provider client configuration failed")
	}
	issuer := os.Getenv("TERRASCALE_OIDC_ISSUER")
	slug := issuer[strings.LastIndex(issuer, "/")+1:]
	instance, err := client.TSAuth.Instance(slug)
	if err != nil {
		t.Fatal("provider slug configuration failed")
	}
	app, err := instance.OIDC.NewApplication(TSAuthOIDCApplicationConfig{Issuer: issuer, Authentication: TSAuthOAuthClientAuthentication{ClientID: os.Getenv("TERRASCALE_OIDC_CLIENT_ID"), ClientSecret: os.Getenv("TERRASCALE_OIDC_CLIENT_SECRET"), Method: method}, RedirectURI: os.Getenv("TERRASCALE_OIDC_REDIRECT_URI"), RequireSession: true})
	if err != nil {
		t.Fatal("provider application configuration failed")
	}
	identity, err := app.VerifyIDToken(t.Context(), os.Getenv("TERRASCALE_OIDC_ID_TOKEN"), TSAuthIDTokenVerificationOptions{Nonce: os.Getenv("TERRASCALE_OIDC_NONCE"), AccessToken: os.Getenv("TERRASCALE_OIDC_ACCESS_TOKEN")})
	if err != nil {
		t.Fatalf("provider token verification failed: %v", err)
	}
	info, err := instance.OIDC.GetUserInfo(t.Context(), os.Getenv("TERRASCALE_OIDC_ACCESS_TOKEN"))
	if err != nil {
		t.Fatalf("provider userinfo failed: %v", err)
	}
	if info.Sub != identity.Subject {
		t.Fatal("provider userinfo subject mismatch")
	}
	// The provider revokes refresh families, not issued access JWTs.
	if os.Getenv("TERRASCALE_OIDC_TEST_REVOKE") == "1" {
		refresh := os.Getenv("TERRASCALE_OIDC_REFRESH_TOKEN")
		if refresh == "" {
			t.Fatal("refresh family revocation requires allocated refresh token")
		}
		if err := app.Revoke(t.Context(), refresh); err != nil {
			t.Fatalf("provider revoke failed: %v", err)
		}
		if _, err := app.Refresh(t.Context(), refresh); err == nil {
			t.Fatal("provider accepted a revoked refresh token")
		}
	}
}
