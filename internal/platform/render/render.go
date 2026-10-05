package render

import (
	"bytes"
	"cmp"
	"context"
	"crypto/rand"
	"encoding/base64"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"sync"
	"unicode/utf8"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui"
)

// StatusNotices supplies the status page's active incidents and maintenance
// for the signed-in shell banner.
type StatusNotices interface {
	Notices(context.Context) []core.StatusNotice
}

type Engine struct {
	store       core.Store
	csrf        core.CSRF
	status      StatusNotices
	noticeScope func(*http.Request) string
	dev         bool
	// cspReportURL receives the browser's Content-Security-Policy violation
	// reports; empty sends none.
	cspReportURL string
}

// New returns the page renderer. A nil status source renders no banner.
func New(store core.Store, csrf core.CSRF, status StatusNotices, noticeScope func(*http.Request) string, dev bool) *Engine {
	return &Engine{store: store, csrf: csrf, status: status, noticeScope: noticeScope, dev: dev}
}

// ReportCSPViolationsTo makes rendered pages ask the browser to post their
// Content-Security-Policy violations to url.
func (e *Engine) ReportCSPViolationsTo(url string) *Engine {
	e.cspReportURL = url
	return e
}

// buffers holds render buffers across requests; a page is rendered fully
// before any byte is written so a template error can still produce a 500.
var buffers = sync.Pool{New: func() any { return new(bytes.Buffer) }}

// maxPooledBuffer keeps an unusually large page from pinning its buffer.
const maxPooledBuffer = 1 << 20

// contentSecurityPolicy admits only same-origin resources and the page's own
// nonced scripts. A page with a code editor also admits Monaco's nonced
// <style> elements and the inline style attributes it positions lines with.
// Violations go to reportURL through report-uri only: GlitchTip's security
// endpoint accepts the legacy csp-report body, not the Reporting API's.
func contentSecurityPolicy(nonce string, features *ui.PageFeatures, reportURL string) string {
	style := "style-src 'self'"
	if features.CodeEditor {
		style = "style-src 'self' 'nonce-" + nonce + "'; style-src-attr 'unsafe-inline'"
	}
	policy := "default-src 'self'; script-src 'self' 'nonce-" + nonce + "'; " + style +
		"; img-src 'self' data: https://flagcdn.com; font-src 'self'; connect-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'"
	if reportURL != "" {
		policy += "; report-uri " + reportURL
	}
	return policy
}

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
		if err := e.store.ViewAs(r.Context(), page.Principal, func(account core.Account) error {
			if page.Layout == "admin" && !account.User.PlatformAdmin {
				return core.ErrForbidden
			}
			page.Shell = shellFor(account, page.Principal, page.Layout, r)
			return nil
		}); err != nil {
			return err
		}
		if e.status != nil {
			page.Shell.Notices = e.status.Notices(r.Context())
			if e.noticeScope != nil {
				page.Shell.NoticeScope = e.noticeScope(r)
			}
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
	ctx, features := ui.WithPageFeatures(templ.WithNonce(r.Context(), page.Nonce))
	if err := document.Render(ctx, output); err != nil {
		return err
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "private, no-store")
	w.Header().Set("Content-Security-Policy", contentSecurityPolicy(page.Nonce, features, e.cspReportURL))
	if page.Status != 0 {
		w.WriteHeader(page.Status)
	}
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
		{Items: []core.NavItem{
			{Label: "Overview", URL: "/"},
		}},
		{Label: "Build", Items: []core.NavItem{
			// Query subscriptions (/subscriptions) is hidden from the navigation;
			// the page and its endpoints stay available.
			{Label: "Databases", URL: "/databases"},
		}},
		{Label: "Secure", Items: []core.NavItem{
			{Label: "API keys", URL: "/api-keys"}, {Label: "Team", URL: "/access"},
			{Label: "Authentication", URL: "/auth", Children: core.AuthNavigation()}, {Label: "Audit", URL: "/audit"},
		}},
		{Label: "Manage", Items: []core.NavItem{
			{Label: "Usage", URL: "/usage"}, {Label: "Billing", URL: "/billing"},
			{Label: "Organization", URL: "/organization"}, {Label: "Project settings", URL: "/settings"},
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

// navAliases lists pages that belong to a navigation section without being
// nested under its URL.
var navAliases = map[string][]string{"/auth/overview": {"/auth"}}

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
	for _, notification := range account.Notifications {
		if notification.VisibleTo(p) && notification.Unread() {
			shell.UnreadNotifications++
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
		shell.Navigation[i] = core.NavGroup{Label: group.Label, Items: navItems(group.Items, r.URL.Path, shell)}
	}
	shell.ReturnURL = switchReturnURL(shell.Navigation, r.URL)
	return shell
}

// switchReturnURL is where switching organization or project lands: the
// deepest navigation entry holding the current page. The page URL itself can
// name a database, filter or tab of the previous project that the next one
// lacks. Pages outside the navigation belong to the account and keep their URL.
func switchReturnURL(groups []core.NavGroup, current *url.URL) string {
	for _, group := range groups {
		if section := activeSection(group.Items); section != "" {
			return section
		}
	}
	if uri := current.RequestURI(); strings.HasPrefix(uri, "/") && !strings.HasPrefix(uri, "//") {
		return uri
	}
	return "/"
}

// activeSection returns the URL of the deepest active item, or "" when no
// item is active.
func activeSection(items []core.NavItem) string {
	for _, item := range items {
		if item.Active {
			return cmp.Or(activeSection(item.Children), item.URL)
		}
	}
	return ""
}

// navItems copies items for one request, flagging the sections that contain
// path at every sub-navigation level and counting what they hold.
func navItems(items []core.NavItem, path string, shell core.Shell) []core.NavItem {
	items = slices.Clone(items)
	for i := range items {
		items[i].Active = navActive(items[i].URL, path)
		items[i].Children = navItems(items[i].Children, path, shell)
		switch items[i].URL {
		case "/organization":
			items[i].BadgeCount = len(shell.Organizations)
			items[i].BadgeLabel = ui.CountLabel(items[i].BadgeCount, "organization", "organizations")
		}
	}
	return items
}

// navActive reports whether path is the section at url, nested under it, or
// one of its aliases. Aliases name single pages, so they match exactly.
func navActive(url, path string) bool {
	return path == url || (url != "/" && strings.HasPrefix(path, url+"/")) || slices.Contains(navAliases[url], path)
}
