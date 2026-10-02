package sdk

import (
	"net/url"
	"testing"
	"time"
)

func TestTSAuthOIDCAuthorizationOptions(t *testing.T) {
	f := newOIDCFixture(t)
	app := f.application(t)
	ctx := t.Context()
	seconds := func(n int) *time.Duration { d := time.Duration(n) * time.Second; return &d }

	// Step-up: prompt=login and max_age=0 are sent and stored on the attempt.
	attempt, err := app.BeginAuthorizationWithOptions(ctx, []string{"openid"}, TSAuthAuthorizationOptions{Prompt: "login", MaxAge: seconds(0)})
	if err != nil {
		t.Fatal(err)
	}
	u, _ := url.Parse(attempt.AuthorizationURL)
	if q := u.Query(); q.Get("prompt") != "login" || q.Get("max_age") != "0" || attempt.MaxAge == nil || *attempt.MaxAge != 0 || attempt.StartedAt.IsZero() {
		t.Fatalf("authorization URL %s, attempt %+v", attempt.AuthorizationURL, attempt.MaxAge)
	}
	exchange := func(attempt *TSAuthAuthorizationAttempt, authTime any) (*TSAuthOIDCLogin, error) {
		claims := f.claims(attempt.Nonce)
		if authTime != nil {
			claims["auth_time"] = authTime
		}
		f.token = f.sign(t, claims, nil)
		return app.ExchangeCode(ctx, TSAuthOIDCCallback{Code: "code", State: attempt.State}, *attempt)
	}
	login, err := exchange(attempt, time.Now().Unix())
	if err != nil || login.Identity.AuthTime == nil {
		t.Fatalf("fresh sign-in: %+v %v", login, err)
	}
	_, err = exchange(attempt, attempt.StartedAt.Unix()-60)
	assertOIDCCode(t, err, "invalid_id_token")
	_, err = app.VerifyIDToken(ctx, f.token, TSAuthIDTokenVerificationOptions{Nonce: attempt.Nonce, AccessToken: "access", MaxAge: seconds(0), AuthorizationStartedAt: attempt.StartedAt})
	assertOIDCCode(t, err, "authentication_too_old")
	_, err = exchange(attempt, nil)
	assertOIDCCode(t, err, "invalid_id_token")

	// prompt=login alone also demands a sign-in after the attempt started.
	loginOnly, err := app.BeginAuthorizationWithOptions(ctx, []string{"openid"}, TSAuthAuthorizationOptions{Prompt: "login"})
	if err != nil {
		t.Fatal(err)
	}
	if u, _ := url.Parse(loginOnly.AuthorizationURL); u.Query().Has("max_age") {
		t.Fatal("max_age sent without MaxAge")
	}
	if _, err := exchange(loginOnly, loginOnly.StartedAt.Unix()-3600); err == nil {
		t.Fatal("prompt=login accepted an old authentication")
	}

	// max_age=300 admits an authentication from two minutes ago, not an hour.
	recent, err := app.BeginAuthorizationWithOptions(ctx, []string{"openid"}, TSAuthAuthorizationOptions{MaxAge: seconds(300)})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := exchange(recent, time.Now().Unix()-120); err != nil {
		t.Fatalf("recent authentication: %v", err)
	}
	if _, err := exchange(recent, time.Now().Unix()-3600); err == nil {
		t.Fatal("max_age accepted an hour-old authentication")
	}
	// auth_time in the future is invalid even without max_age.
	plain, err := app.BeginAuthorization(ctx, []string{"openid"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := exchange(plain, time.Now().Unix()+3600); err == nil {
		t.Fatal("future auth_time accepted")
	}
	if login, err := exchange(plain, nil); err != nil || login.Identity.AuthTime != nil {
		t.Fatalf("no constraints: %+v %v", login, err)
	}

	// Invalid options fail before any request.
	f.requests = 0
	for _, bad := range []struct {
		options TSAuthAuthorizationOptions
		code    string
	}{
		{TSAuthAuthorizationOptions{Prompt: "none login"}, "invalid_prompt"},
		{TSAuthAuthorizationOptions{Prompt: "login login"}, "invalid_prompt"},
		{TSAuthAuthorizationOptions{Prompt: "bogus"}, "invalid_prompt"},
		{TSAuthAuthorizationOptions{Prompt: " login"}, "invalid_prompt"},
		{TSAuthAuthorizationOptions{MaxAge: seconds(-1)}, "invalid_max_age"},
		{TSAuthAuthorizationOptions{MaxAge: func() *time.Duration { d := 1500 * time.Millisecond; return &d }()}, "invalid_max_age"},
	} {
		_, err := app.BeginAuthorizationWithOptions(ctx, []string{"openid"}, bad.options)
		assertOIDCCode(t, err, bad.code)
	}
	if ok, err := app.BeginAuthorizationWithOptions(ctx, []string{"openid"}, TSAuthAuthorizationOptions{Prompt: "login consent"}); err != nil || ok == nil {
		t.Fatalf("login consent: %v", err)
	}
	if f.requests != 0 {
		t.Fatal("option validation reached the token endpoint")
	}
}
