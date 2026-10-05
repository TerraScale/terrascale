package apikeys

import (
	"context"
	"net/url"
	"slices"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestCredentialTabControls(t *testing.T) {
	for _, tc := range []struct{ kind, search, create, dialog string }{
		{"", "Search credentials", "Create credential", "create-credential"},
		{"key", "Search API keys", "Create key", "create-key"},
		{"m2m", "Search M2M clients", "Create client", "create-m2m"},
	} {
		t.Run(tc.search, func(t *testing.T) {
			data := pageData{listFilters: listFilters{Kind: tc.kind, Status: "inactive", DatabaseID: "db_orders", Search: "deploy"}, CanManage: true, Databases: []databaseChoice{{ID: "db_orders", Name: "orders"}}}
			var body strings.Builder
			if err := apiKeysContent(core.Page{}, data).Render(context.Background(), &body); err != nil {
				t.Fatal(err)
			}
			html := body.String()
			header := strings.Index(html, `<header class="ts-card-head">`)
			filters := strings.Index(html, `<form class="ts-filters ts-apikeys-filters"`)
			if header < 0 || filters < header || strings.Count(html, `role="search"`) != 1 {
				t.Error("one shared filter bar must remain in the listing header, including for an empty list")
			}
			for _, want := range []string{
				`placeholder="` + tc.search + `" aria-label="` + tc.search + `"`,
				`commandfor="` + tc.dialog + `"`, `<span>` + tc.create + `</span>`,
				`<option value="active">Active</option>`, `<option value="inactive" selected>Inactive</option>`,
				`<option value="db_orders" selected>orders</option>`,
				`No credentials match these filters.`,
				`href="` + strings.ReplaceAll(data.clearURL(), "&", "&amp;") + `">Clear</a>`,
			} {
				if !strings.Contains(html, want) {
					t.Errorf("controls missing %s", want)
				}
			}
			for _, unsupported := range []string{`value="revoked"`, `value="expired"`} {
				if strings.Contains(html, unsupported) {
					t.Errorf("unsupported status: %s", unsupported)
				}
			}
		})
	}
}

func TestCredentialTabNavigationKeepsCompatibleFilters(t *testing.T) {
	filters := readFilters(url.Values{"kind": {"m2m"}, "status": {"inactive"}, "databaseId": {"db_orders"}, "q": {" deploy "}})
	for _, kind := range []string{"", "key", "m2m"} {
		u, err := url.Parse(filters.tabURL(kind))
		if err != nil {
			t.Fatal(err)
		}
		q := u.Query()
		if q.Get("kind") != kind || q.Get("status") != "inactive" || q.Get("databaseId") != "db_orders" || q.Get("q") != "deploy" {
			t.Errorf("tab %q: filters = %v", kind, q)
		}
		clear, err := url.Parse(filters.forTab(kind).clearURL())
		if err != nil {
			t.Fatal(err)
		}
		if clear.Query().Get("kind") != kind || clear.Query().Get("status") != "" || clear.Query().Get("databaseId") != "" || clear.Query().Get("q") != "" {
			t.Errorf("clear must reset filters without leaving tab %q: %s", kind, clear)
		}
	}
	invalid := readFilters(url.Values{"kind": {"unknown"}, "status": {"expired"}})
	if invalid.Kind != "" || invalid.Status != "" {
		t.Errorf("unsupported selections remain active: %+v", invalid)
	}
}

func TestCredentialDatabaseFilterBelongsToSelectedProject(t *testing.T) {
	project := core.Project{Databases: map[string]core.Database{"db_orders": {ID: "db_orders"}}}
	for _, kind := range []string{"", "key", "m2m"} {
		filters := listFilters{Kind: kind, Status: "active", DatabaseID: "db_foreign", Search: "deploy"}.forProject(project)
		if filters.DatabaseID != "" || filters.Kind != kind || filters.Status != "active" || filters.Search != "deploy" {
			t.Errorf("foreign database filter was not cleared independently: %+v", filters)
		}
		filters.DatabaseID = "db_orders"
		if got := filters.forProject(project).DatabaseID; got != "db_orders" {
			t.Errorf("compatible project database selection = %q", got)
		}
	}
}

func TestCredentialControlsRespectManagementPermission(t *testing.T) {
	for _, tab := range credentialTabs {
		var body strings.Builder
		if err := apiKeysContent(core.Page{}, pageData{listFilters: listFilters{Kind: tab.Kind}}).Render(context.Background(), &body); err != nil {
			t.Fatal(err)
		}
		html := body.String()
		if strings.Contains(html, `commandfor="create-`) || strings.Contains(html, `<dialog id="create-`) {
			t.Errorf("tab %q exposes credential creation without management permission", tab.Kind)
		}
		if !strings.Contains(html, `role="search"`) {
			t.Errorf("tab %q hides list controls from a read-only member", tab.Kind)
		}
	}
}

func TestCredentialFiltersCombineWithinSelectedProjectAndKind(t *testing.T) {
	ctx := context.Background()
	store := memstore.New()
	account, err := store.CreateAccount(ctx, core.User{Name: "Owner", Email: "filters@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	owner := account.Principal()
	now := time.Now()
	err = store.UpdateAs(ctx, owner, func(a *core.Account) error {
		org := a.Organizations[owner.OrganizationID]
		project := org.Projects[owner.ProjectID]
		project.APIKeys = map[string]core.APIKey{
			"key":          {ID: "key", Kind: kindAPIKey, Name: "Deploy key", DatabaseID: "db_orders"},
			"client":       {ID: "client", Kind: kindM2M, Name: "Deploy client", DatabaseID: "db_orders"},
			"revoked":      {ID: "revoked", Kind: kindM2M, Name: "Deploy revoked", DatabaseID: "db_orders", RevokedAt: now},
			"expired":      {ID: "expired", Kind: kindAPIKey, Name: "Deploy expired", DatabaseID: "db_orders", ExpiresAt: now.Add(-time.Hour)},
			"other-db":     {ID: "other-db", Kind: kindAPIKey, Name: "Deploy elsewhere", DatabaseID: "db_users"},
			"other-name":   {ID: "other-name", Kind: kindAPIKey, Name: "Read only", DatabaseID: "db_orders"},
			"project-wide": {ID: "project-wide", Kind: kindM2M, Name: "Deploy project"},
		}
		org.Projects[owner.ProjectID] = project
		org.Projects["other-project"] = core.Project{ID: "other-project", OrganizationID: owner.OrganizationID, APIKeys: map[string]core.APIKey{"foreign": {ID: "foreign", Kind: kindAPIKey, Name: "Deploy foreign", DatabaseID: "db_orders"}}}
		a.Organizations[owner.OrganizationID] = org
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	service := Service{deps: core.Deps{Store: store, Demo: true}}
	for _, tc := range []struct {
		kind, status string
		want         []string
	}{
		{"", "active", []string{"client", "key"}},
		{"key", "active", []string{"key"}},
		{"m2m", "active", []string{"client"}},
		{"", "inactive", []string{"expired", "revoked"}},
		{"key", "inactive", []string{"expired"}},
		{"m2m", "inactive", []string{"revoked"}},
	} {
		items, err := service.List(ctx, owner, credentialFilter{Kind: controlsFor(tc.kind).CredentialKind, Status: tc.status, DatabaseID: "db_orders", Search: "DEPLOY"})
		if err != nil {
			t.Fatal(err)
		}
		ids := make([]string, 0, len(items))
		for _, item := range items {
			ids = append(ids, item.ID)
		}
		slices.Sort(ids)
		if !slices.Equal(ids, tc.want) {
			t.Errorf("tab %q status %q: got %v, want %v", tc.kind, tc.status, ids, tc.want)
		}
	}
}
