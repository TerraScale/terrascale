package apikeys

import (
	"net/http"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui/icons"
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
	Summary       []summaryFigure
	Keys          []keyView
	Databases     []databaseChoice
	CreatedSecret string
	Error         string
}

type keyView struct {
	sdk.ProjectAPIKey
	Type credentialType
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
	// credentialTypes fixes the order of the per-type summary figures.
	credentialTypes = []credentialType{readWriteKey, readOnlyKey, fullAccessKey, machineClient}
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

// summaryFigure is one compact count in the credential summary row. Figures
// with an icon count a credential type; the others mark status with a dot.
type summaryFigure struct {
	Label string
	Value int
	Tone  string
	Icon  func(size int) templ.Component
}

type databaseChoice struct{ ID, Name string }

func mountPages(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /api-keys", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, "") })
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
		input := sdk.CreateProjectAPIKeyRequest{Name: r.FormValue("name"), DatabaseID: r.FormValue("databaseId"), Scopes: formScopes(r.FormValue("scopes"))}
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
		input := sdk.CreateProjectAPIKeyRequest{Name: r.FormValue("name"), DatabaseID: r.FormValue("databaseId"), Scopes: formScopes(r.FormValue("scopes"))}
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
		_, secret, err := rotateDemoKey(deps, p, r.FormValue("keyId"))
		if err != nil {
			writeError(w, err)
			return
		}
		renderPage(w, r, deps, secret)
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
		if err := revokeDemoKey(deps, p, r.FormValue("keyId")); err != nil {
			writeError(w, err)
			return
		}
		http.Redirect(w, r, "/api-keys", http.StatusSeeOther)
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
	search := strings.ToLower(data.Search)
	now := time.Now()
	expiringBy := now.Add(14 * 24 * time.Hour)
	var active, expiring int
	byType := map[string]int{}
	for _, key := range project.APIKeys {
		item := keyView{ProjectAPIKey: publicKey(key, a.User.Email), Type: typeOf(key.Kind, key.Scopes)}
		byType[item.Type.Label]++
		if item.Active {
			active++
			if !key.ExpiresAt.IsZero() && !key.ExpiresAt.After(expiringBy) {
				expiring++
			}
		}
		if data.Kind == "key" && key.Kind != "api-key" || data.Kind == "m2m" && key.Kind != "m2m" || data.Status == "active" && !item.Active || data.Status == "inactive" && item.Active || data.DatabaseID != "" && item.DatabaseID != data.DatabaseID || data.Search != "" && !strings.Contains(strings.ToLower(item.Name+" "+item.Prefix), search) {
			continue
		}
		data.Keys = append(data.Keys, item)
	}
	total := len(project.APIKeys)
	data.Summary = []summaryFigure{
		{Label: "Total", Value: total, Tone: "blue"},
		{Label: "Active", Value: active, Tone: "emerald"},
		{Label: "Expiring soon", Value: expiring, Tone: "orange"},
		{Label: "Inactive", Value: total - active, Tone: "slate"},
	}
	for _, kind := range credentialTypes {
		data.Summary = append(data.Summary, summaryFigure{Label: kind.Label + "s", Value: byType[kind.Label], Tone: kind.Tone, Icon: kind.Icon})
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
