package apikeys

import (
	"net/http"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

type pageData struct {
	ProjectID     string
	CanManage     bool
	Kind          string
	Status        string
	DatabaseID    string
	Search        string
	Create        string
	ActiveKeys    int
	ExpiringKeys  int
	M2MClients    int
	Keys          []keyView
	Databases     []databaseChoice
	CreatedSecret string
	Error         string
}

type keyView struct {
	sdk.ProjectAPIKey
	Kind string
}

type databaseChoice struct{ ID, Name string }

func mountPages(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /api-keys", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, "") })
	mux.HandleFunc("POST /api/v1/dashboard/credentials/key", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		p, project, _, err := selectedPage(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		if !deps.Demo {
			http.Error(w, "live credential issuance is unavailable until a published producer contract exists", http.StatusServiceUnavailable)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		input := sdk.CreateProjectAPIKeyRequest{Name: r.FormValue("name"), DatabaseID: r.FormValue("databaseId"), Scopes: strings.FieldsFunc(r.FormValue("scopes"), func(char rune) bool { return char == ',' || char == ' ' || char == '\n' || char == '\t' })}
		if err := validateInput(project, input); err != nil {
			writeError(w, err)
			return
		}
		_, secret, err := createDemoKey(deps, p, input)
		if err != nil {
			writeError(w, err)
			return
		}
		renderPage(w, r, deps, secret)
	})
	mux.HandleFunc("POST /api/v1/dashboard/credentials/m2m", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		p, project, _, err := selectedPage(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		if !deps.Demo {
			http.Error(w, "live machine credentials are unavailable until a published producer contract exists", http.StatusServiceUnavailable)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		input := sdk.CreateProjectAPIKeyRequest{Name: r.FormValue("name"), DatabaseID: r.FormValue("databaseId"), Scopes: strings.FieldsFunc(r.FormValue("scopes"), func(char rune) bool { return char == ',' || char == ' ' || char == '\n' || char == '\t' })}
		if err := validateInput(project, input); err != nil {
			writeError(w, err)
			return
		}
		_, secret, err := createDemoCredential(deps, p, input, "m2m")
		if err != nil {
			writeError(w, err)
			return
		}
		renderPage(w, r, deps, secret)
	})
	mux.HandleFunc("POST /api/v1/dashboard/credentials/rotate", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		p, _, _, err := selectedPage(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		if !deps.Demo {
			http.Error(w, "live credential rotation is unavailable until a published producer contract exists", http.StatusServiceUnavailable)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		_, secret, err := rotateDemoKey(deps, p, r.FormValue("keyId"))
		if err != nil {
			writeError(w, err)
			return
		}
		renderPage(w, r, deps, secret)
	})
	mux.HandleFunc("POST /api/v1/dashboard/credentials/revoke", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		p, _, _, err := selectedPage(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		if !deps.Demo {
			http.Error(w, "live credential revocation is unavailable until a published producer contract exists", http.StatusServiceUnavailable)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		if err := revokeDemoKey(deps, p, r.FormValue("keyId")); err != nil {
			writeError(w, err)
			return
		}
		http.Redirect(w, r, "/api-keys", http.StatusSeeOther)
	})
}

func selectedPage(deps core.Deps, r *http.Request) (core.Principal, core.Project, core.Account, error) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Project{}, core.Account{}, core.ErrUnauthorized
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Project{}, core.Account{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return p, project, a, err
}

func renderPage(w http.ResponseWriter, r *http.Request, deps core.Deps, secret string) {
	p, project, a, err := selectedPage(deps, r)
	if err != nil {
		writeError(w, err)
		return
	}
	query := r.URL.Query()
	data := pageData{ProjectID: project.ID, CanManage: a.Organizations[p.OrganizationID].CanManage(p.UserID), CreatedSecret: secret, Error: query.Get("error"), Kind: query.Get("kind"), Status: query.Get("status"), DatabaseID: query.Get("databaseId"), Search: strings.TrimSpace(query.Get("q")), Create: query.Get("create")}
	if !slices.Contains([]string{"key", "m2m"}, data.Create) {
		data.Create = ""
	}
	if !slices.Contains([]string{"key", "m2m"}, data.Kind) {
		data.Kind = ""
	}
	if !slices.Contains([]string{"active", "inactive"}, data.Status) {
		data.Status = ""
	}
	if len(data.Search) > 256 {
		data.Search = data.Search[:256]
	}
	now := time.Now()
	expiringBy := now.Add(14 * 24 * time.Hour)
	for _, key := range project.APIKeys {
		item := keyView{ProjectAPIKey: publicKey(key, a.User.Email), Kind: key.Kind}
		if item.Active {
			switch key.Kind {
			case "api-key":
				data.ActiveKeys++
				if !key.ExpiresAt.IsZero() && key.ExpiresAt.After(now) && !key.ExpiresAt.After(expiringBy) {
					data.ExpiringKeys++
				}
			case "m2m":
				data.M2MClients++
			}
		}
		if data.Kind == "key" && item.Kind != "api-key" || data.Kind == "m2m" && item.Kind != "m2m" || data.Status == "active" && !item.Active || data.Status == "inactive" && item.Active || data.DatabaseID != "" && item.DatabaseID != data.DatabaseID || data.Search != "" && !strings.Contains(strings.ToLower(item.Name+" "+item.Prefix), strings.ToLower(data.Search)) {
			continue
		}
		data.Keys = append(data.Keys, item)
	}
	for _, db := range project.Databases {
		data.Databases = append(data.Databases, databaseChoice{ID: db.ID, Name: db.Name})
	}
	slices.SortFunc(data.Keys, func(a, b keyView) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(data.Databases, func(a, b databaseChoice) int { return strings.Compare(a.Name, b.Name) })
	org := a.Organizations[p.OrganizationID]
	page := core.Page{Layout: "shell", Title: "API keys · TerraScale", Principal: p, View: func(page core.Page) templ.Component { return apiKeysContent(page, data) }, CSRFToken: deps.CSRF.Token(w, r), Shell: core.Shell{UserName: a.User.Name, UserEmail: a.User.Email, OrganizationName: org.Name, ProjectName: project.Name}}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render API keys", http.StatusInternalServerError)
	}
}
