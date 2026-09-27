package apikeys

import (
	"errors"
	"log/slog"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"unicode/utf8"

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
	f := listFilters{Kind: query.Get("kind"), Status: query.Get("status"), DatabaseID: query.Get("databaseId"), Search: clip(strings.TrimSpace(query.Get("q")), 256)}
	if !slices.Contains([]string{"key", "m2m"}, f.Kind) {
		f.Kind = ""
	}
	if !slices.Contains([]string{"active", "inactive"}, f.Status) {
		f.Status = ""
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
// has to be typed again. Each field is clipped so that even the oversized
// input that failed validation fits in the redirect address.
type draft struct {
	Name, Scopes, DatabaseID string
}

func newDraft(name, scopes, databaseID string) draft {
	return draft{Name: clip(name, 256), Scopes: clip(scopes, 2560), DatabaseID: clip(databaseID, 128)}
}

// clip shortens s to at most n bytes without splitting a character.
func clip(s string, n int) string {
	if len(s) <= n {
		return s
	}
	for n > 0 && !utf8.RuneStart(s[n]) {
		n--
	}
	return s[:n]
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

func mountPages(mux *http.ServeMux, deps core.Deps, service Service) {
	mux.HandleFunc("GET /api-keys", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, service, revealed{}) })
	create := func(kind, dialog string) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			p, ok := deps.FormPrincipal(w, r)
			if !ok {
				return
			}
			input := sdk.CreateProjectAPIKeyRequest{Name: r.PostFormValue("name"), DatabaseID: r.PostFormValue("databaseId"), Scopes: formScopes(r.PostFormValue("scopes"))}
			created, err := service.Create(r.Context(), p, kind, input)
			if err != nil {
				redirectPageError(w, r, err, dialog)
				return
			}
			renderPage(w, r, deps, service, revealed{Name: created.APIKey.Name, Secret: created.Secret})
		}
	}
	mux.HandleFunc("POST /api/v1/dashboard/credentials/key", create(kindAPIKey, "key"))
	mux.HandleFunc("POST /api/v1/dashboard/credentials/m2m", create(kindM2M, "m2m"))
	mux.HandleFunc("POST /api/v1/dashboard/credentials/rotate", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		rotated, err := service.Rotate(r.Context(), p, r.PostFormValue("keyId"), "")
		if err != nil {
			redirectPageError(w, r, err, "")
			return
		}
		renderPage(w, r, deps, service, revealed{Name: rotated.APIKey.Name, Secret: rotated.Secret, Rotated: true})
	})
	mux.HandleFunc("POST /api/v1/dashboard/credentials/revoke", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		if _, err := service.Revoke(r.Context(), p, r.PostFormValue("keyId"), ""); err != nil {
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

// redirectPageError returns a failed credential form to the API keys page,
// filtered as before, with an explanation. A create that failed on its input
// reopens its dialog with what was submitted, less a database that no longer
// exists.
func redirectPageError(w http.ResponseWriter, r *http.Request, err error, create string) {
	message := "The credential change could not be completed. Try again."
	reopen := false
	switch {
	case errors.Is(err, core.ErrInvalid):
		message = "Enter a name of up to 128 characters and 1 to 16 scopes of up to 128 characters each, such as database:read."
		reopen = true
	case errors.Is(err, errUnknownDatabase):
		message = "That database no longer exists. Choose another database or all project databases."
		reopen = true
	case errors.Is(err, core.ErrNotFound) && create != "":
		message = "This project is no longer available. Reload the page and try again."
	case errors.Is(err, core.ErrNotFound):
		message = "That credential no longer exists or was already revoked."
	case errors.Is(err, core.ErrForbidden):
		message = "Only organization owners and admins can manage credentials."
	case errors.Is(err, errLiveCredentials):
		message = "Credentials cannot be issued, rotated, or revoked here yet."
	default:
		slog.Error("change credential", "path", r.URL.Path, "error", err)
	}
	query := readFilters(r.URL.Query()).query()
	query.Set("error", message)
	if create != "" && reopen {
		submitted := newDraft(r.PostFormValue("name"), r.PostFormValue("scopes"), r.PostFormValue("databaseId"))
		if errors.Is(err, errUnknownDatabase) {
			submitted.DatabaseID = ""
		}
		query.Set("create", create)
		query.Set("draftName", submitted.Name)
		query.Set("draftScopes", submitted.Scopes)
		if submitted.DatabaseID != "" {
			query.Set("draftDatabaseId", submitted.DatabaseID)
		}
	}
	http.Redirect(w, r, "/api-keys?"+query.Encode(), http.StatusSeeOther)
}

func renderPage(w http.ResponseWriter, r *http.Request, deps core.Deps, service Service, reveal revealed) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		core.RedirectToLogin(w, r)
		return
	}
	a, project, err := service.selected(p)
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
		data.Draft = newDraft(query.Get("draftName"), query.Get("draftScopes"), query.Get("draftDatabaseId"))
	} else {
		data.Create = ""
	}
	filter := credentialFilter{Status: data.Status, DatabaseID: data.DatabaseID, Search: data.Search}
	switch data.Kind {
	case "key":
		filter.Kind = kindAPIKey
	case "m2m":
		filter.Kind = kindM2M
	}
	credentials, err := service.List(p, filter)
	if err != nil {
		writeError(w, err)
		return
	}
	for _, item := range credentials {
		view := keyView{ProjectAPIKey: item.ProjectAPIKey, Type: typeOf(item.Kind, item.Scopes), DatabaseName: item.DatabaseID}
		if db, ok := project.Databases[item.DatabaseID]; ok {
			view.DatabaseName = db.Name
		}
		data.Keys = append(data.Keys, view)
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
