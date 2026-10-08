package databases

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/session"
)

func TestDeleteDatabaseRevokesItsKeysAndDropsItsConfigurations(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "delete-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	if err := store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases["7WTDQ8PZGQGTHSXG2Z6S4YT68Q"] = core.Database{ID: "7WTDQ8PZGQGTHSXG2Z6S4YT68Q", ProjectID: project.ID, Name: "tmp", Status: "ready"}
		project.APIKeys["3WJQ9SNFJWXEFTDHDAK4JHT6H1"] = core.APIKey{ID: "3WJQ9SNFJWXEFTDHDAK4JHT6H1", Kind: "api-key", ProjectID: project.ID, DatabaseID: "7WTDQ8PZGQGTHSXG2Z6S4YT68Q", Name: "scoped"}
		project.APIKeys["6J3PEQTTERR7271PVFZZB4P4VQ"] = core.APIKey{ID: "6J3PEQTTERR7271PVFZZB4P4VQ", Kind: "api-key", ProjectID: project.ID, Name: "project"}
		project.SharedResultConfigs["742GE4NGG0F0TABM8NCSJFZNFD"] = core.SharedResultConfig{ID: "742GE4NGG0F0TABM8NCSJFZNFD", DatabaseID: "7WTDQ8PZGQGTHSXG2Z6S4YT68Q", Name: "orders_view"}
		project.BackupPolicies["7WTDQ8PZGQGTHSXG2Z6S4YT68Q"] = core.BackupPolicy{RetentionDays: 7}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, CSRF: guard, Tools: core.NewTools(store), Demo: true})
	signIn := httptest.NewRecorder()
	if err := sessions.Start(signIn, httptest.NewRequest(http.MethodGet, "/", nil), p); err != nil {
		t.Fatal(err)
	}
	token := guard.Token(signIn, httptest.NewRequest(http.MethodGet, "/", nil))
	form := url.Values{"databaseId": {"7WTDQ8PZGQGTHSXG2Z6S4YT68Q"}, "confirmName": {"tmp"}, "_csrf": {token}}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/databases/delete", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	for _, cookie := range signIn.Result().Cookies() {
		request.AddCookie(cookie)
	}
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusNoContent {
		t.Fatalf("delete = %d %s, want 204", response.Code, response.Body)
	}

	snapshot, err := store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	project := snapshot.Organizations[p.OrganizationID].Projects[p.ProjectID]
	if _, ok := project.Databases["7WTDQ8PZGQGTHSXG2Z6S4YT68Q"]; ok {
		t.Error("the database is still stored")
	}
	if revoked := project.APIKeys["3WJQ9SNFJWXEFTDHDAK4JHT6H1"].RevokedAt; revoked.IsZero() || time.Since(revoked) > time.Minute {
		t.Errorf("key limited to the database revoked at %v, want now", revoked)
	}
	if revoked := project.APIKeys["6J3PEQTTERR7271PVFZZB4P4VQ"].RevokedAt; !revoked.IsZero() {
		t.Errorf("project-wide key revoked at %v, want it active", revoked)
	}
	if _, ok := project.SharedResultConfigs["742GE4NGG0F0TABM8NCSJFZNFD"]; ok {
		t.Error("the database's shared result configuration is still stored")
	}
	if _, ok := project.BackupPolicies["7WTDQ8PZGQGTHSXG2Z6S4YT68Q"]; ok {
		t.Error("the database's backup policy is still stored")
	}
}
