package apikeys

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

func TestRotateReturnsTheRotatedKey(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	deps, owner := core.Deps{Store: store}, account.Principal()
	created, first, err := createDemoKey(deps, owner, sdk.CreateProjectAPIKeyRequest{Name: "ci-deploy", Scopes: []string{"database:read"}})
	if err != nil {
		t.Fatal(err)
	}
	rotated, second, err := rotateDemoKey(deps, owner, created.ID)
	if err != nil {
		t.Fatal(err)
	}
	if rotated.ID != created.ID || rotated.Name != "ci-deploy" || rotated.SecretHash != hash(second) || second == first {
		t.Fatalf("rotateDemoKey = %+v with a new secret %t, want ci-deploy with the new secret's hash", rotated, second != first)
	}
}

func TestRedirectPageError(t *testing.T) {
	for _, tc := range []struct {
		name, create string
		err          error
		reopen       string
	}{
		{name: "invalid input reopens its dialog", create: "m2m", err: core.ErrInvalid, reopen: "m2m"},
		{name: "a missing credential does not", create: "key", err: core.ErrNotFound},
		{name: "rotate and revoke have no dialog to reopen", err: core.ErrInvalid},
	} {
		t.Run(tc.name, func(t *testing.T) {
			recorder := httptest.NewRecorder()
			redirectPageError(recorder, httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/credentials/key", nil), tc.err, tc.create)
			location, err := url.Parse(recorder.Header().Get("Location"))
			if err != nil || recorder.Code != http.StatusSeeOther || location.Path != "/api-keys" {
				t.Fatalf("redirect = %d %q", recorder.Code, recorder.Header().Get("Location"))
			}
			if got := location.Query().Get("create"); got != tc.reopen {
				t.Errorf("create = %q, want %q", got, tc.reopen)
			}
			if location.Query().Get("error") == "" {
				t.Error("the redirect must explain the error")
			}
		})
	}
}
