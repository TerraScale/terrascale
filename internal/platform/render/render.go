package render

import (
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"net/http"
	"os"
	"slices"
	"strings"
	"sync"
	"unicode/utf8"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui"
)

type Engine struct {
	store *core.Store
	csrf  core.CSRF
	dev   bool
}

func New(store *core.Store, csrf core.CSRF) *Engine {
	return &Engine{store: store, csrf: csrf, dev: os.Getenv("DASHBOARD_DEV") == "1"}
}

// buffers holds render buffers across requests; a page is rendered fully
// before any byte is written so a template error can still produce a 500.
var buffers = sync.Pool{New: func() any { return new(bytes.Buffer) }}

// maxPooledBuffer keeps an unusually large page from pinning its buffer.
const maxPooledBuffer = 1 << 20

const (
	cspPrefix = "default-src 'self'; script-src 'self' 'nonce-"
	cspSuffix = "'; style-src 'self'; img-src 'self' data: https://flagcdn.com; font-src 'self'; connect-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'"
)

func (e *Engine) Page(w http.ResponseWriter, r *http.Request, page core.Page) error {
	if (page.Layout != "shell" && page.Layout != "auth" && page.Layout != "admin") || page.View == nil {
		return core.ErrInvalid
	}
	page.Title = documentTitle(page.Title)
	if page.CSRFToken == "" && e.csrf != nil {
		page.CSRFToken = e.csrf.Token(w, r)
	}
	page.DevMode = e.dev
	if page.Nonce == "" {
		var nonce [24]byte
		rand.Read(nonce[:])
		page.Nonce = base64.RawURLEncoding.EncodeToString(nonce[:])
	}
	if page.Layout != "auth" {
		if e.store == nil {
			return core.ErrUnauthorized
		}
		if err := e.store.ViewAs(page.Principal, func(account core.Account) error {
			if page.Layout == "admin" && !account.User.PlatformAdmin {
				return core.ErrForbidden
			}
			page.Shell = shellFor(account, page.Principal, page.Layout, r)
			return nil
		}); err != nil {
			return err
		}
	}
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
	output := buffers.Get().(*bytes.Buffer)
	defer func() {
		if output.Cap() <= maxPooledBuffer {
			output.Reset()
			buffers.Put(output)
		}
	}()
	if err := document.Render(templ.WithNonce(r.Context(), page.Nonce), output); err != nil {
		return err
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "private, no-store")
	w.Header().Set("Content-Security-Policy", cspPrefix+page.Nonce+cspSuffix)
	_, err := w.Write(output.Bytes())
	return err
}

// Page titles are plain text; tolerate feature callers that still include the
// brand suffix so the shared layouts render it exactly once.
func documentTitle(title string) string {
	title = strings.TrimSpace(title)
	for {
		trimmed, ok := strings.CutSuffix(title, " · TerraScale")
		if !ok {
			trimmed, ok = strings.CutSuffix(title, " | TerraScale")
		}
		if !ok {
			break
		}
		title = strings.TrimSpace(trimmed)
	}
	if title == "TerraScale" {
		return ""
	}
	return title
}

var (
	shellNavigation = []core.NavGroup{
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
			{Label: "Billing", URL: "/billing"}, {Label: "Organization", URL: "/organization"},
			{Label: "Settings", URL: "/settings"},
		}},
	}
	adminNavigation = []core.NavGroup{
		{Label: "Accounts", Items: []core.NavItem{
			{Label: "Users", URL: "/admin/users"}, {Label: "Organizations", URL: "/admin/organizations"},
			{Label: "Projects", URL: "/admin/projects"},
		}},
		{Label: "Platform", Items: []core.NavItem{
			{Label: "Databases", URL: "/admin/databases"}, {Label: "Estate", URL: "/admin/estate"},
			{Label: "Usage", URL: "/admin/usage"}, {Label: "Plans", URL: "/admin/plans"},
		}},
	}
)

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
	groups := shellNavigation
	if layout == "admin" {
		groups = adminNavigation
	}
	shell.Navigation = make([]core.NavGroup, len(groups))
	for i, group := range groups {
		items := slices.Clone(group.Items)
		for j := range items {
			items[j].Active = navActive(items[j].URL, r.URL.Path)
			if items[j].URL == "/organization" {
				items[j].BadgeCount = len(shell.Organizations)
			}
		}
		shell.Navigation[i] = core.NavGroup{Label: group.Label, Items: items}
	}
	return shell
}

// navActive reports whether path is the section at url, nested under it, or
// one of its aliases.
func navActive(url, path string) bool {
	within := func(url string) bool {
		return path == url || (url != "/" && strings.HasPrefix(path, url+"/"))
	}
	return within(url) || slices.ContainsFunc(navAliases[url], within)
}
