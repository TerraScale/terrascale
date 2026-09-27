package apikeys

import (
	"context"
	"net/http"
	"net/http/httptest"
	"net/url"
	"slices"
	"strings"
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
	form := url.Values{"name": {"ci-deploy"}, "scopes": {"database:read"}, "databaseId": {"db_gone"}}
	for _, tc := range []struct {
		name, create string
		err          error
		reopen       string
		draft        url.Values
	}{
		{name: "invalid input reopens its dialog with the input", create: "m2m", err: core.ErrInvalid, reopen: "m2m", draft: url.Values{"draftName": {"ci-deploy"}, "draftScopes": {"database:read"}, "draftDatabaseId": {"db_gone"}}},
		{name: "a missing database reopens it without the database", create: "key", err: core.ErrNotFound, reopen: "key", draft: url.Values{"draftName": {"ci-deploy"}, "draftScopes": {"database:read"}, "draftDatabaseId": nil}},
		{name: "rotate and revoke have no dialog to reopen", err: core.ErrNotFound, draft: url.Values{"draftName": nil}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/credentials/key?kind=m2m&status=active&q=deploy", strings.NewReader(form.Encode()))
			request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			recorder := httptest.NewRecorder()
			redirectPageError(recorder, request, tc.err, tc.create)
			location, err := url.Parse(recorder.Header().Get("Location"))
			if err != nil || recorder.Code != http.StatusSeeOther || location.Path != "/api-keys" {
				t.Fatalf("redirect = %d %q", recorder.Code, recorder.Header().Get("Location"))
			}
			query := location.Query()
			if got := query.Get("create"); got != tc.reopen {
				t.Errorf("create = %q, want %q", got, tc.reopen)
			}
			if query.Get("error") == "" {
				t.Error("the redirect must explain the error")
			}
			if query.Get("kind") != "m2m" || query.Get("status") != "active" || query.Get("q") != "deploy" {
				t.Errorf("filters = %v, want the list's filters kept", query)
			}
			for name, want := range tc.draft {
				if got := query[name]; !slices.Equal(got, want) {
					t.Errorf("%s = %q, want %q", name, got, want)
				}
			}
		})
	}
}

func TestCreateDialogKeepsTheDraft(t *testing.T) {
	data := pageData{
		CanManage: true,
		Create:    "key",
		Draft:     draft{Name: "ci-deploy", Scopes: "database:admin", DatabaseID: "db_orders"},
		Databases: []databaseChoice{{ID: "db_orders", Name: "orders"}, {ID: "db_users", Name: "users"}},
		Error:     "Enter a name.",
	}
	var body strings.Builder
	if err := apiKeysContent(core.Page{}, data).Render(context.Background(), &body); err != nil {
		t.Fatal(err)
	}
	html := body.String()
	for _, want := range []string{`id="key-name" name="name" value="ci-deploy"`, `id="key-scopes" name="scopes" value="database:admin"`, `<option value="db_orders" selected>`} {
		if !strings.Contains(html, want) {
			t.Errorf("the reopened dialog is missing %s", want)
		}
	}
	if !strings.Contains(html, `id="m2m-name" name="name" value=""`) || !strings.Contains(html, `id="m2m-scopes" name="scopes" value="database:read"`) {
		t.Error("the other create dialog must keep its defaults")
	}
}

func TestListFiltersWith(t *testing.T) {
	filters := readFilters(url.Values{"kind": {"m2m"}, "status": {"bogus"}, "databaseId": {"db_orders"}, "q": {" deploy "}})
	if got, want := filters.with("/api-keys"), "/api-keys?databaseId=db_orders&kind=m2m&q=deploy"; got != want {
		t.Errorf("with = %q, want %q", got, want)
	}
	if got := (listFilters{}).with("/api-keys"); got != "/api-keys" {
		t.Errorf("with no filters = %q, want the bare path", got)
	}
}
