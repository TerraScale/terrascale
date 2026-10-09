package apikeys

import (
	"context"
	"net/http"
	"net/http/httptest"
	"net/url"
	"slices"
	"strings"
	"testing"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestDatabaseFilterKeepsFullSelectedName(t *testing.T) {
	name := "production-orders-primary-north-america-very-long-name"
	data := pageData{
		listFilters: listFilters{DatabaseID: "1XWADXKQ90Y51GFKA6WTWK64FT"},
		Databases:   []databaseChoice{{ID: "1XWADXKQ90Y51GFKA6WTWK64FT", Name: name}},
	}
	var html strings.Builder
	if err := apiKeysFilters(data).Render(t.Context(), &html); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(html.String(), `title="`+name+`"`) {
		t.Fatal("the selected database's full name must be available in the title")
	}
	if got := databaseFilterTitle(pageData{}); got != "All databases" {
		t.Fatalf("unfiltered title = %q, want All databases", got)
	}
}

func TestRotateReturnsTheRotatedKey(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	service, owner := Service{deps: core.Deps{Store: store, Demo: true}}, account.Principal()
	created, err := service.Create(t.Context(), owner, kindAPIKey, CreateProjectAPIKeyRequest{Name: "ci-deploy", Scopes: []string{"database:read"}})
	if err != nil {
		t.Fatal(err)
	}
	rotated, err := service.Rotate(t.Context(), owner, created.APIKey.ID, "")
	if err != nil {
		t.Fatal(err)
	}
	if rotated.APIKey.ID != created.APIKey.ID || rotated.APIKey.Name != "ci-deploy" || rotated.Secret == "" || rotated.Secret == created.Secret {
		t.Fatalf("Rotate = %+v, want ci-deploy with a new secret", rotated)
	}
}

func TestRedirectPageError(t *testing.T) {
	form := url.Values{"name": {"ci-deploy"}, "scopes": {"database:read"}, "databaseId": {"5M9HQK3SHZJNB4SF7HKQKKEE46"}}
	for _, tc := range []struct {
		name, create string
		err          error
		reopen       string
		draft        url.Values
	}{
		{name: "invalid input reopens its dialog with the input", create: "m2m", err: core.ErrInvalid, reopen: "m2m", draft: url.Values{"draftName": {"ci-deploy"}, "draftScopes": {"database:read"}, "draftDatabaseId": {"5M9HQK3SHZJNB4SF7HKQKKEE46"}}},
		{name: "a missing database reopens it without the database", create: "key", err: errUnknownDatabase, reopen: "key", draft: url.Values{"draftName": {"ci-deploy"}, "draftScopes": {"database:read"}, "draftDatabaseId": nil}},
		{name: "a project gone mid-request does not blame the database", create: "key", err: core.ErrNotFound, draft: url.Values{"draftName": nil}},
		{name: "an unexpected failure does not reopen", create: "key", err: core.ErrConflict, draft: url.Values{"draftName": nil}},
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

func TestRedirectPageErrorClipsOversizedInput(t *testing.T) {
	form := url.Values{"name": {strings.Repeat("é", 4096)}, "scopes": {strings.Repeat("database:read,", 100000)}}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/credentials/key", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	recorder := httptest.NewRecorder()
	redirectPageError(recorder, request, core.ErrInvalid, "key")
	location := recorder.Header().Get("Location")
	if len(location) > 8192 {
		t.Fatalf("redirect address is %d bytes; oversized input must be clipped", len(location))
	}
	parsed, err := url.Parse(location)
	if err != nil {
		t.Fatal(err)
	}
	if name := parsed.Query().Get("draftName"); name == "" || !utf8.ValidString(name) {
		t.Errorf("draftName = %q, want a valid clipped prefix of the input", name)
	}
}

func TestCreateDialogKeepsTheDraft(t *testing.T) {
	data := pageData{
		CanManage: true,
		Create:    "key",
		Draft:     draft{Name: "ci-deploy", Scopes: "database:admin", DatabaseID: "16KDJ28DR0XG7QGWEZ4VJ9Z1VX"},
		Databases: []databaseChoice{{ID: "16KDJ28DR0XG7QGWEZ4VJ9Z1VX", Name: "orders"}, {ID: "51ZVM7GAJHERHSP6VTQCFHMDVJ", Name: "users"}},
		Error:     "Enter a name.",
	}
	var body strings.Builder
	if err := apiKeysContent(core.Page{}, data).Render(context.Background(), &body); err != nil {
		t.Fatal(err)
	}
	html := body.String()
	for _, want := range []string{`id="key-name" name="name" value="ci-deploy"`, `id="key-scopes" name="scopes" value="database:admin"`, `<option value="16KDJ28DR0XG7QGWEZ4VJ9Z1VX" selected>`} {
		if !strings.Contains(html, want) {
			t.Errorf("the reopened dialog is missing %s", want)
		}
	}
	if !strings.Contains(html, `id="m2m-name" name="name" value=""`) || !strings.Contains(html, `id="m2m-scopes" name="scopes" value="database:read"`) {
		t.Error("the other create dialog must keep its defaults")
	}
}

func TestListFiltersWith(t *testing.T) {
	filters := readFilters(url.Values{"kind": {"m2m"}, "status": {"bogus"}, "databaseId": {"16KDJ28DR0XG7QGWEZ4VJ9Z1VX"}, "q": {" deploy "}})
	if got, want := filters.with("/api-keys"), "/api-keys?databaseId=16KDJ28DR0XG7QGWEZ4VJ9Z1VX&kind=m2m&q=deploy"; got != want {
		t.Errorf("with = %q, want %q", got, want)
	}
	if got := (listFilters{}).with("/api-keys"); got != "/api-keys" {
		t.Errorf("with no filters = %q, want the bare path", got)
	}
}
