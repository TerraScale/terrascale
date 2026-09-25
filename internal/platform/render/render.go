package render

import (
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"net/http"
	"os"
	"slices"
	"strings"
	"unicode/utf8"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui"
)

type Engine struct {
	store *core.Store
	csrf  core.CSRF
}

func New(store *core.Store, csrf core.CSRF) *Engine {
	return &Engine{store: store, csrf: csrf}
}

func (e *Engine) Page(w http.ResponseWriter, r *http.Request, page core.Page) error {
	if (page.Layout != "shell" && page.Layout != "auth" && page.Layout != "admin") || page.View == nil {
		return core.ErrInvalid
	}
	page.Title = documentTitle(page.Title)
	if page.CSRFToken == "" && e.csrf != nil {
		page.CSRFToken = e.csrf.Token(w, r)
	}
	page.DevMode = os.Getenv("DASHBOARD_DEV") == "1"
	if page.Nonce == "" {
		var nonce [24]byte
		if _, err := rand.Read(nonce[:]); err != nil {
			return err
		}
		page.Nonce = base64.RawURLEncoding.EncodeToString(nonce[:])
	}
	if page.Layout != "auth" {
		if e.store == nil {
			return core.ErrUnauthorized
		}
		account, err := e.store.SnapshotAs(page.Principal)
		if err != nil {
			return err
		}
		if page.Layout == "admin" && !account.User.PlatformAdmin {
			return core.ErrForbidden
		}
		page.Shell = shellFor(account, page.Principal, page.Layout, r)
	}
	var output bytes.Buffer
	body := page.View(page)
	if body == nil {
		return core.ErrInvalid
	}
	var document templ.Component
	switch page.Layout {
	case "auth":
		document = ui.Auth(page, body)
	case "shell":
		document = ui.Shell(page, body)
	case "admin":
		document = ui.Admin(page, body)
	}
	if err := document.Render(templ.WithNonce(r.Context(), page.Nonce), &output); err != nil {
		return err
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "private, no-store")
	w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self' 'nonce-"+page.Nonce+"'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'")
	_, err := w.Write(output.Bytes())
	return err
}

// Page titles are plain text; tolerate feature callers that still include the
// brand suffix so the shared layouts render it exactly once.
func documentTitle(title string) string {
	title = strings.TrimSpace(title)
	for {
		trimmed := strings.TrimSuffix(title, " · TerraScale")
		if trimmed == title {
			trimmed = strings.TrimSuffix(title, " | TerraScale")
		}
		if trimmed == title {
			break
		}
		title = strings.TrimSpace(trimmed)
	}
	if title == "TerraScale" {
		return ""
	}
	return title
}

func (e *Engine) Fragment(w http.ResponseWriter, r *http.Request, fragment core.Fragment) error {
	if fragment.View == nil {
		return core.ErrInvalid
	}
	if fragment.CSRFToken == "" && e.csrf != nil {
		fragment.CSRFToken = e.csrf.Token(w, r)
	}
	var output bytes.Buffer
	body := fragment.View(core.Page{CSRFToken: fragment.CSRFToken})
	if body == nil {
		return core.ErrInvalid
	}
	if err := body.Render(r.Context(), &output); err != nil {
		return err
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "private, no-store")
	_, err := w.Write(output.Bytes())
	return err
}

// navAliases lists routes that belong to a navigation section without being
// nested under its URL.
var navAliases = map[string][]string{"/data": {"/schema", "/indexes", "/history"}}

func shellFor(account core.Account, p core.Principal, layout string, r *http.Request) core.Shell {
	shell := core.Shell{UserName: account.User.Name, UserEmail: account.User.Email}
	nameParts := strings.Fields(account.User.Name)
	if len(nameParts) > 0 {
		first, _ := utf8.DecodeRuneInString(nameParts[0])
		shell.UserInitials = strings.ToUpper(string(first))
		if len(nameParts) > 1 {
			last, _ := utf8.DecodeRuneInString(nameParts[len(nameParts)-1])
			shell.UserInitials += strings.ToUpper(string(last))
		}
	}
	returnURL := r.URL.RequestURI()
	if strings.HasPrefix(returnURL, "/") && !strings.HasPrefix(returnURL, "//") {
		shell.ReturnURL = returnURL
	} else {
		shell.ReturnURL = "/"
	}
	for id, org := range account.Organizations {
		if _, err := account.OrganizationFor(p, id); err != nil {
			continue
		}
		shell.Organizations = append(shell.Organizations, core.OrganizationOption{ID: id, Name: org.Name, Selected: id == p.OrganizationID})
		if id == p.OrganizationID {
			shell.OrganizationName = org.Name
			for projectID, project := range org.Projects {
				shell.Projects = append(shell.Projects, core.ProjectOption{ID: projectID, Name: project.Name, Selected: projectID == p.ProjectID})
				if projectID == p.ProjectID {
					shell.ProjectName = project.Name
				}
			}
		}
	}
	byName := func(a, b string) int { return strings.Compare(strings.ToLower(a), strings.ToLower(b)) }
	slices.SortFunc(shell.Organizations, func(a, b core.OrganizationOption) int { return byName(a.Name, b.Name) })
	slices.SortFunc(shell.Projects, func(a, b core.ProjectOption) int { return byName(a.Name, b.Name) })
	if layout == "admin" {
		shell.Navigation = []core.NavGroup{
			{Label: "Accounts", Items: []core.NavItem{
				{Label: "Users", URL: "/admin/users"}, {Label: "Organizations", URL: "/admin/organizations"},
				{Label: "Projects", URL: "/admin/projects"},
			}},
			{Label: "Platform", Items: []core.NavItem{
				{Label: "Databases", URL: "/admin/databases"}, {Label: "Estate", URL: "/admin/estate"},
				{Label: "Usage", URL: "/admin/usage"}, {Label: "Plans", URL: "/admin/plans"},
			}},
		}
	} else {
		shell.Navigation = []core.NavGroup{
			{Label: "Build", Items: []core.NavItem{
				{Label: "Overview", URL: "/"}, {Label: "Databases", URL: "/databases"},
				{Label: "Tables", URL: "/data"}, {Label: "Backups", URL: "/backups"},
				{Label: "Engine", URL: "/engine"},
			}},
			{Label: "Secure", Items: []core.NavItem{
				{Label: "API keys", URL: "/api-keys"}, {Label: "Team", URL: "/access"},
				{Label: "Authentication", URL: "/auth"}, {Label: "Audit", URL: "/audit"},
			}},
			{Label: "Manage", Items: []core.NavItem{
				{Label: "Usage", URL: "/usage"}, {Label: "Subscriptions", URL: "/subscriptions"},
				{Label: "Billing", URL: "/billing"},
				{Label: "Organization", URL: "/organization", BadgeCount: len(shell.Organizations)},
				{Label: "Settings", URL: "/settings"},
			}},
		}
	}
	for _, group := range shell.Navigation {
		for i := range group.Items {
			group.Items[i].Active = slices.ContainsFunc(append([]string{group.Items[i].URL}, navAliases[group.Items[i].URL]...), func(url string) bool {
				return r.URL.Path == url || (url != "/" && strings.HasPrefix(r.URL.Path, url+"/"))
			})
		}
	}
	return shell
}
