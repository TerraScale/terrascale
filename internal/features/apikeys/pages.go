package apikeys

import (
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strings"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui/icons"
	sdk "terrascale.tech/sdk"
)

type pageData struct {
	listFilters
	ProjectID string
	CanManage bool
	Create    string
	Draft     draft
	Keys      []keyView
	Databases []databaseChoice
	Revealed  revealed
	Error     string
}

// listFilters narrow the credential list. Forms that change a credential
// carry them in their action URL, so the page comes back filtered the same way.
type listFilters struct {
	Kind, Status, DatabaseID, Search string
}

func readFilters(query url.Values) listFilters {
	f := listFilters{Kind: query.Get("kind"), Status: query.Get("status"), DatabaseID: query.Get("databaseId"), Search: strings.TrimSpace(query.Get("q"))}
	if !slices.Contains([]string{"key", "m2m"}, f.Kind) {
		f.Kind = ""
	}
	if !slices.Contains([]string{"active", "inactive"}, f.Status) {
		f.Status = ""
	}
	if len(f.Search) > 256 {
		f.Search = f.Search[:256]
	}
	return f
}

func (f listFilters) query() url.Values {
	query := url.Values{}
	for name, value := range map[string]string{"kind": f.Kind, "status": f.Status, "databaseId": f.DatabaseID, "q": f.Search} {
		if value != "" {
			query.Set(name, value)
		}
	}
	return query
}

// with returns path followed by the filters as its query.
func (f listFilters) with(path string) string {
	if query := f.query(); len(query) > 0 {
		return path + "?" + query.Encode()
	}
	return path
}

// draft is what a create form submitted, returned with its error so nothing
// has to be typed again.
type draft struct {
	Name, Scopes, DatabaseID string
}

// draftFor returns the draft of the create dialog for kind, if it reopened.
func (d pageData) draftFor(kind string) draft {
	if d.Create != kind {
		return draft{}
	}
	return d.Draft
}

// revealed is a secret shown once, in the response that issued it.
type revealed struct {
	Name, Secret string
	Rotated      bool
}

type keyView struct {
	sdk.ProjectAPIKey
	Type         credentialType
	DatabaseName string
}

// credentialType classifies a credential by kind and scope breadth, so every
// type reads at a glance with its own icon and navigation tone.
type credentialType struct {
	Label string
	Tone  string
	Icon  func(size int) templ.Component
}

var (
	readWriteKey  = credentialType{Label: "Read-write key", Tone: "amber", Icon: icons.KeyRound}
	readOnlyKey   = credentialType{Label: "Read-only key", Tone: "sky", Icon: icons.Eye}
	fullAccessKey = credentialType{Label: "Full-access key", Tone: "rose", Icon: icons.ShieldAlert}
	machineClient = credentialType{Label: "M2M client", Tone: "violet", Icon: icons.Bot}
)

func typeOf(kind string, scopes []string) credentialType {
	switch {
	case kind == "m2m":
		return machineClient
	case slices.Contains(scopes, "*"):
		return fullAccessKey
	case slices.ContainsFunc(scopes, func(scope string) bool { return strings.HasSuffix(scope, ":write") }):
		return readWriteKey
	default:
		return readOnlyKey
	}
}

type databaseChoice struct{ ID, Name string }

func mountPages(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /api-keys", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, revealed{}) })
	mux.HandleFunc("POST /api/v1/dashboard/credentials/key", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, project, _, err := selectedPage(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
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
		input := sdk.CreateProjectAPIKeyRequest{Name: r.PostFormValue("name"), DatabaseID: r.PostFormValue("databaseId"), Scopes: formScopes(r.PostFormValue("scopes"))}
		if err := validateInput(project, input); err != nil {
			redirectPageError(w, r, err, "key")
			return
		}
		key, secret, err := createDemoKey(deps, p, input)
		if err != nil {
			redirectPageError(w, r, err, "key")
			return
		}
		renderPage(w, r, deps, revealed{Name: key.Name, Secret: secret})
	})
	mux.HandleFunc("POST /api/v1/dashboard/credentials/m2m", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, project, _, err := selectedPage(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
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
		input := sdk.CreateProjectAPIKeyRequest{Name: r.PostFormValue("name"), DatabaseID: r.PostFormValue("databaseId"), Scopes: formScopes(r.PostFormValue("scopes"))}
		if err := validateInput(project, input); err != nil {
			redirectPageError(w, r, err, "m2m")
			return
		}
		key, secret, err := createDemoCredential(deps, p, input, "m2m")
		if err != nil {
			redirectPageError(w, r, err, "m2m")
			return
		}
		renderPage(w, r, deps, revealed{Name: key.Name, Secret: secret})
	})
	mux.HandleFunc("POST /api/v1/dashboard/credentials/rotate", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, _, _, err := selectedPage(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
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
		key, secret, err := rotateDemoKey(deps, p, r.PostFormValue("keyId"))
		if err != nil {
			redirectPageError(w, r, err, "")
			return
		}
		renderPage(w, r, deps, revealed{Name: key.Name, Secret: secret, Rotated: true})
	})
	mux.HandleFunc("POST /api/v1/dashboard/credentials/revoke", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, _, _, err := selectedPage(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
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
		if err := revokeDemoKey(deps, p, r.PostFormValue("keyId")); err != nil {
			redirectPageError(w, r, err, "")
			return
		}
		http.Redirect(w, r, readFilters(r.URL.Query()).with("/api-keys"), http.StatusSeeOther)
	})
}

// formScopes splits the scopes field on commas and whitespace.
func formScopes(value string) []string {
	return strings.FieldsFunc(value, func(char rune) bool { return char == ',' || char == ' ' || char == '\n' || char == '\t' })
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

// redirectPageError returns a failed credential form to the API keys page,
// filtered as before, with an explanation. A failed create reopens its dialog
// with what was submitted; a database that no longer exists is left out.
func redirectPageError(w http.ResponseWriter, r *http.Request, err error, create string) {
	message := "The credential change could not be completed. Try again."
	switch {
	case errors.Is(err, core.ErrInvalid):
		message = "Enter a name of up to 128 characters and 1 to 16 scopes of up to 128 characters each, such as database:read."
	case errors.Is(err, core.ErrNotFound) && create != "":
		message = "That database no longer exists. Choose another database or all project databases."
	case errors.Is(err, core.ErrNotFound):
		message = "That credential no longer exists or was already revoked."
	case errors.Is(err, core.ErrForbidden):
		message = "Only organization owners and admins can manage credentials."
	}
	query := readFilters(r.URL.Query()).query()
	query.Set("error", message)
	if create != "" && (errors.Is(err, core.ErrInvalid) || errors.Is(err, core.ErrNotFound)) {
		query.Set("create", create)
		query.Set("draftName", r.PostFormValue("name"))
		query.Set("draftScopes", r.PostFormValue("scopes"))
		if !errors.Is(err, core.ErrNotFound) {
			query.Set("draftDatabaseId", r.PostFormValue("databaseId"))
		}
	}
	http.Redirect(w, r, "/api-keys?"+query.Encode(), http.StatusSeeOther)
}

func renderPage(w http.ResponseWriter, r *http.Request, deps core.Deps, reveal revealed) {
	p, project, a, err := selectedPage(deps, r)
	if err != nil {
		if errors.Is(err, core.ErrUnauthorized) {
			core.RedirectToLogin(w, r)
			return
		}
		writeError(w, err)
		return
	}
	query := r.URL.Query()
	data := pageData{listFilters: readFilters(query), ProjectID: project.ID, CanManage: a.Organizations[p.OrganizationID].CanManage(p.UserID), Revealed: reveal, Error: query.Get("error"), Create: query.Get("create")}
	if slices.Contains([]string{"key", "m2m"}, data.Create) {
		data.Draft = draft{Name: query.Get("draftName"), Scopes: query.Get("draftScopes"), DatabaseID: query.Get("draftDatabaseId")}
	} else {
		data.Create = ""
	}
	search := strings.ToLower(data.Search)
	for _, key := range project.APIKeys {
		item := keyView{ProjectAPIKey: publicKey(key, a.User.Email), Type: typeOf(key.Kind, key.Scopes), DatabaseName: key.DatabaseID}
		if db, ok := project.Databases[key.DatabaseID]; ok {
			item.DatabaseName = db.Name
		}
		if data.Kind == "key" && key.Kind != "api-key" || data.Kind == "m2m" && key.Kind != "m2m" || data.Status == "active" && !item.Active || data.Status == "inactive" && item.Active || data.DatabaseID != "" && item.DatabaseID != data.DatabaseID || data.Search != "" && !strings.Contains(strings.ToLower(item.Name+" "+item.Prefix), search) {
			continue
		}
		data.Keys = append(data.Keys, item)
	}
	for _, db := range project.Databases {
		data.Databases = append(data.Databases, databaseChoice{ID: db.ID, Name: db.Name})
	}
	slices.SortFunc(data.Keys, func(a, b keyView) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(data.Databases, func(a, b databaseChoice) int { return strings.Compare(a.Name, b.Name) })
	page := core.Page{Layout: "shell", Title: "API keys · TerraScale", Principal: p, View: func(page core.Page) templ.Component { return apiKeysContent(page, data) }, CSRFToken: deps.CSRF.Token(w, r)}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render API keys", http.StatusInternalServerError)
	}
}
