package render

import (
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"html/template"
	"io/fs"
	"net/http"
	"os"
	"strings"
	"sync"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
)

type Engine struct {
	base  *template.Template
	store *core.Store
	csrf  core.CSRF
	mu    sync.RWMutex
	pages map[string]*template.Template
}

// New parses common layout definitions once. Register clones them for each
// feature page so every page may define its own content template safely.
func New(layouts fs.FS, store *core.Store, csrf core.CSRF) (*Engine, error) {
	base, err := template.New("layouts").ParseFS(layouts, "*.html")
	if err != nil {
		return nil, err
	}
	return &Engine{base: base, store: store, csrf: csrf, pages: make(map[string]*template.Template)}, nil
}

func (e *Engine) Register(name string, files fs.FS, patterns ...string) error {
	if name == "" || len(patterns) == 0 {
		return core.ErrInvalid
	}
	parsed, err := e.base.Clone()
	if err != nil {
		return err
	}
	if _, err := parsed.ParseFS(files, patterns...); err != nil {
		return err
	}
	if parsed.Lookup("content") == nil {
		return errors.New("feature template missing content definition")
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	if _, exists := e.pages[name]; exists {
		return core.ErrConflict
	}
	e.pages[name] = parsed
	return nil
}

func (e *Engine) Page(w http.ResponseWriter, r *http.Request, page core.Page) error {
	if page.Layout != "shell" && page.Layout != "auth" && page.Layout != "admin" {
		return core.ErrInvalid
	}
	page.Title = documentTitle(page.Title)
	parsed := e.lookup(page.Template)
	if parsed == nil || parsed.Lookup(page.Layout) == nil {
		return core.ErrNotFound
	}
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
	if err := parsed.ExecuteTemplate(&output, page.Layout, page); err != nil {
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
	parsed := e.lookup(fragment.Template)
	if parsed == nil {
		return core.ErrNotFound
	}
	if fragment.CSRFToken == "" && e.csrf != nil {
		fragment.CSRFToken = e.csrf.Token(w, r)
	}
	var output bytes.Buffer
	if err := parsed.ExecuteTemplate(&output, "content", core.Page{Data: fragment.Data, CSRFToken: fragment.CSRFToken}); err != nil {
		return err
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "private, no-store")
	_, err := w.Write(output.Bytes())
	return err
}

func (e *Engine) lookup(name string) *template.Template {
	e.mu.RLock()
	defer e.mu.RUnlock()
	return e.pages[name]
}

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
	if layout == "admin" {
		shell.Navigation = []core.NavItem{
			{Label: "Overview", URL: "/admin"}, {Label: "Users", URL: "/admin/users"},
			{Label: "Organizations", URL: "/admin/organizations"}, {Label: "Projects", URL: "/admin/projects"},
			{Label: "Plans", URL: "/admin/plans"},
		}
	} else {
		shell.Navigation = []core.NavItem{
			{Label: "Overview", URL: "/"}, {Label: "Databases", URL: "/databases"},
			{Label: "Data", URL: "/data"}, {Label: "API keys", URL: "/api-keys"},
			{Label: "Backups", URL: "/backups"}, {Label: "Authentication", URL: "/auth"},
			{Label: "Access", URL: "/access"}, {Label: "Usage", URL: "/usage"},
			{Label: "Engine", URL: "/engine"}, {Label: "Audit", URL: "/audit"},
			{Label: "Billing", URL: "/subscriptions"}, {Label: "Settings", URL: "/settings"},
		}
	}
	for i := range shell.Navigation {
		url := shell.Navigation[i].URL
		shell.Navigation[i].Active = r.URL.Path == url || (url != "/" && strings.HasPrefix(r.URL.Path, url+"/"))
	}
	return shell
}
